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
        IReadOnlyList<WatchedItemSummary> watchedItems,
        IReadOnlyList<string> excludeTitles,
        int count,
        IReadOnlyList<string>? notFoundTitles = null)
    {
        var topGenres = watchedItems
            .SelectMany(w => w.Genres)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => g.Key);

        var watched = watchedItems.Select(w =>
            $"- {w.Title} ({w.Year?.ToString() ?? "?"}) [{w.TypeLabel()}]");

        var exclude = excludeTitles.Take(200).Select(t => $"- {t}");

        var notFoundSection = notFoundTitles is { Count: > 0 }
            ? $"""

            NOTE: These titles were previously suggested but could NOT be verified on TMDB — do NOT suggest them again:
            {string.Join(Environment.NewLine, notFoundTitles.Select(t => $"- {t}"))}
            """
            : string.Empty;

        return $$"""
            You are a media recommendation engine. Based on this user's watch history, suggest exactly {{count}} movies and TV shows they would enjoy.

            TOP GENRES WATCHED: {{string.Join(", ", topGenres)}}

            WATCH HISTORY (most recent first):
            {{string.Join(Environment.NewLine, watched)}}

            DO NOT RECOMMEND (already in library):
            {{string.Join(Environment.NewLine, exclude)}}
            {{notFoundSection}}

            Rules:
            - Only suggest titles that genuinely exist and are on TMDB
            - Include a mix of movies and series
            - Vary by era (classic and recent)
            - Be specific with the year

            Return ONLY valid JSON:
            {"recommendations":[{"title":"Name","year":2020,"type":"movie","reason":"one sentence"}]}
            Use type "movie" or "series".
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

    private static string TypeLabel(this WatchedItemSummary item)
        => item.IsSeries ? "series" : "movie";

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
        IReadOnlyList<WatchedItemSummary> watchedItems,
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

        var prompt = LlmProviderHelpers.BuildPrompt(watchedItems, excludeTitles, count, notFoundTitles);
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
        IReadOnlyList<WatchedItemSummary> watchedItems,
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

        var prompt = LlmProviderHelpers.BuildPrompt(watchedItems, excludeTitles, count, notFoundTitles);
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
