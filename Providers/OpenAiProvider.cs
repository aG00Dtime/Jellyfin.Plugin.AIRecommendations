using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Providers;

/// <summary>
/// Shared helpers for LLM providers.
/// </summary>
public static class LlmProviderHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string BuildPrompt(
        UserTasteProfile profile,
        IReadOnlyList<string> excludeTitles,
        int count,
        IReadOnlyList<string>? notFoundTitles = null)
    {
        var genres = profile.TopGenres.Count > 0
            ? string.Join(", ", profile.TopGenres.Select(g => $"{g.Genre} ({g.Count})"))
            : "varied";

        var mix = $"{profile.MoviePercent}% movies, {100 - profile.MoviePercent}% series";
        var samples = string.Join(", ", profile.SampleTitles);

        var favSection = profile.FavoriteTitles.Count > 0
            ? $"\nFavourites: {string.Join(", ", profile.FavoriteTitles)}"
            : string.Empty;

        var exclude = string.Join(", ", excludeTitles.Take(100));

        var notFoundSection = notFoundTitles is { Count: > 0 }
            ? $"\nDo NOT suggest (TMDB lookup failed previously): {string.Join(", ", notFoundTitles)}"
            : string.Empty;

        return $$"""
            Suggest exactly {{count}} movies/shows for this user. Return ONLY valid JSON — no prose.

            TASTE PROFILE:
            Genres: {{genres}}
            Era preference: {{profile.EraPreference}}
            Content mix: {{mix}}
            Enjoys: {{samples}}{{favSection}}

            Skip (already watched or owned): {{exclude}}{{notFoundSection}}

            Rules: real titles only (must exist on TMDB), mix movies+series, vary eras.
            {"recommendations":[{"title":"Name","year":2020,"type":"movie","reason":"one line why"}]}
            type must be "movie" or "series".
            """;
    }

    public static IReadOnlyList<LlmRecommendationItem> ParseRecommendations(string content, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<LlmRecommendationItem>();
        }

        var json = ExtractJson(content);
        try
        {
            var parsed = JsonSerializer.Deserialize<LlmResponse>(json, JsonOptions);
            return parsed?.Recommendations ?? (IReadOnlyList<LlmRecommendationItem>)Array.Empty<LlmRecommendationItem>();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM JSON response");
            return Array.Empty<LlmRecommendationItem>();
        }
    }

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return content[start..(end + 1)];
        }

        return content;
    }

    private sealed class LlmResponse
    {
        [JsonPropertyName("recommendations")]
        public List<LlmRecommendationItem>? Recommendations { get; set; }
    }
}

/// <summary>
/// OpenAI chat completions provider.
/// </summary>
public class OpenAiProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiProvider> _logger;

    public OpenAiProvider(IHttpClientFactory httpClientFactory, ILogger<OpenAiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "OpenAI";

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmRecommendationItem>> GetRecommendationsAsync(
        UserTasteProfile profile,
        IReadOnlyList<string> excludeTitles,
        int count,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? notFoundTitles = null)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized");

        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        var prompt = LlmProviderHelpers.BuildPrompt(profile, excludeTitles, count, notFoundTitles);
        var body = new
        {
            model = config.OpenAiModel,
            messages = new[]
            {
                new { role = "system", content = "You are a media recommendation assistant. Return only valid JSON." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7
        };

        var baseUrl = string.IsNullOrWhiteSpace(config.OpenAiBaseUrl)
            ? "https://api.openai.com/v1"
            : config.OpenAiBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(nameof(OpenAiProvider));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return LlmProviderHelpers.ParseRecommendations(content, _logger);
    }
}

/// <summary>
/// OpenRouter provider.
/// </summary>
public class OpenRouterProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenRouterProvider> _logger;

    public OpenRouterProvider(IHttpClientFactory httpClientFactory, ILogger<OpenRouterProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "OpenRouter";

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmRecommendationItem>> GetRecommendationsAsync(
        UserTasteProfile profile,
        IReadOnlyList<string> excludeTitles,
        int count,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? notFoundTitles = null)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized");

        if (string.IsNullOrWhiteSpace(config.OpenRouterApiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is not configured.");
        }

        var prompt = LlmProviderHelpers.BuildPrompt(profile, excludeTitles, count, notFoundTitles);
        var body = new
        {
            model = config.OpenRouterModel,
            messages = new[]
            {
                new { role = "system", content = "You are a media recommendation assistant. Return only valid JSON." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7
        };

        var baseUrl = string.IsNullOrWhiteSpace(config.OpenRouterBaseUrl)
            ? "https://openrouter.ai/api/v1"
            : config.OpenRouterBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(nameof(OpenRouterProvider));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenRouterApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return LlmProviderHelpers.ParseRecommendations(content, _logger);
    }
}
