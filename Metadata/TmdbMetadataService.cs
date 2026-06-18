using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Metadata;

/// <summary>
/// TMDB metadata and search service.
/// </summary>
public class TmdbMetadataService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbMetadataService> _logger;

    public TmdbMetadataService(IHttpClientFactory httpClientFactory, ILogger<TmdbMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ResolvedRecommendation?> ResolveAsync(
        LlmRecommendationItem item,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _logger.LogWarning("TMDB API key not configured; cannot resolve {Title}", item.Title);
            return null;
        }

        var endpoint = item.IsSeries ? "search/tv" : "search/movie";
        var query = Uri.EscapeDataString(item.Title);
        var yearParam = item.Year.HasValue
            ? (item.IsSeries ? $"&first_air_date_year={item.Year}" : $"&year={item.Year}")
            : string.Empty;

        var url =
            $"https://api.themoviedb.org/3/{endpoint}?api_key={config.TmdbApiKey}&query={query}{yearParam}&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));
        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TMDB search failed for {Title}: {Status}", item.Title, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return null;
        }

        var first = results[0];
        var tmdbId = first.GetProperty("id").GetInt32();
        var title = item.IsSeries
            ? (first.TryGetProperty("name", out var name) ? name.GetString() : null)
            : (first.TryGetProperty("title", out var movieTitle) ? movieTitle.GetString() : null);

        var year = item.IsSeries
            ? ParseYear(first, "first_air_date")
            : ParseYear(first, "release_date");

        return new ResolvedRecommendation
        {
            TmdbId = tmdbId,
            Title = title ?? item.Title,
            Year = year ?? item.Year,
            IsSeries = item.IsSeries,
            Reason = item.Reason,
            Overview = first.TryGetProperty("overview", out var overview) ? overview.GetString() : null,
            PosterPath = first.TryGetProperty("poster_path", out var poster) ? poster.GetString() : null
        };
    }

    public async Task<IReadOnlyList<string>> GetSimilarTitlesAsync(
        string tmdbId,
        bool isSeries,
        int limit,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return Array.Empty<string>();
        }

        var type = isSeries ? "tv" : "movie";
        var url = $"https://api.themoviedb.org/3/{type}/{tmdbId}/similar?api_key={config.TmdbApiKey}";

        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));
        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<TmdbSearchResponse>(json);
        if (parsed?.Results is null)
        {
            return Array.Empty<string>();
        }

        return parsed.Results
            .Take(limit)
            .Select(r => isSeries ? r.Name ?? string.Empty : r.Title ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    private static int? ParseYear(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var dateProp))
        {
            return null;
        }

        var value = dateProp.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(0, 4), CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    private sealed class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbResult>? Results { get; set; }
    }

    private sealed class TmdbResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
