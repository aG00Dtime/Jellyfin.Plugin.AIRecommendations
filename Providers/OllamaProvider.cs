using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Providers;

/// <summary>
/// Ollama provider. Uses /api/generate for both local and cloud (ollama.com).
/// </summary>
public class OllamaProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaProvider> _logger;

    public OllamaProvider(IHttpClientFactory httpClientFactory, ILogger<OllamaProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Ollama";

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmRecommendationItem>> GetRecommendationsAsync(
        UserTasteProfile profile,
        IReadOnlyList<string> excludeTitles,
        int count,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? notFoundTitles = null,
        IReadOnlyList<Models.TmdbCandidate>? catalog = null,
        string? tasteProfileText = null)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized");

        var isCloud = config.OllamaDeployment.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
        var baseUrl = string.IsNullOrWhiteSpace(config.OllamaBaseUrl)
            ? (isCloud ? "https://ollama.com" : "http://localhost:11434")
            : config.OllamaBaseUrl.TrimEnd('/');

        if (isCloud && string.IsNullOrWhiteSpace(config.OllamaApiKey))
        {
            throw new InvalidOperationException("Ollama Cloud requires an API key from ollama.com.");
        }

        var prompt = LlmProviderHelpers.BuildPrompt(profile, excludeTitles, count, notFoundTitles, catalog, tasteProfileText);
        return await ChatAsync(baseUrl, config, prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LlmRecommendationItem>> ChatAsync(
        string baseUrl,
        Configuration.PluginConfiguration config,
        string prompt,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = config.OllamaModel,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = "You are a media recommendation assistant. Return only valid JSON." },
                new { role = "user", content = prompt }
            }
        };

        var client = _httpClientFactory.CreateClient(nameof(OllamaProvider));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat");
        if (!string.IsNullOrWhiteSpace(config.OllamaApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OllamaApiKey);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        return LlmProviderHelpers.ParseRecommendations(content, _logger);
    }
}

/// <summary>
/// Resolves the active LLM provider from plugin configuration.
/// </summary>
public class LlmProviderFactory
{
    private readonly OpenAiProvider _openAi;
    private readonly OpenRouterProvider _openRouter;
    private readonly OllamaProvider _ollama;

    public LlmProviderFactory(
        OpenAiProvider openAi,
        OpenRouterProvider openRouter,
        OllamaProvider ollama)
    {
        _openAi = openAi;
        _openRouter = openRouter;
        _ollama = ollama;
    }

    public ILlmProvider GetActiveProvider()
    {
        var name = Plugin.Instance?.Configuration.ActiveProvider ?? "OpenAI";
        return name switch
        {
            var n when n.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) => _openRouter,
            var n when n.Equals("Ollama", StringComparison.OrdinalIgnoreCase) => _ollama,
            _ => _openAi
        };
    }
}
