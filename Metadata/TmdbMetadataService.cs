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

    // TMDB genre name → ID maps (separate for movies vs TV)
    private static readonly Dictionary<string, int> MovieGenreIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 28, ["Adventure"] = 12, ["Animation"] = 16, ["Comedy"] = 35,
        ["Crime"] = 80, ["Documentary"] = 99, ["Drama"] = 18, ["Family"] = 10751,
        ["Fantasy"] = 14, ["History"] = 36, ["Horror"] = 27, ["Music"] = 10402,
        ["Mystery"] = 9648, ["Romance"] = 10749, ["Science Fiction"] = 878,
        ["Sci-Fi"] = 878, ["Thriller"] = 53, ["War"] = 10752, ["Western"] = 37,
    };

    private static readonly Dictionary<string, int> TvGenreIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 10759, ["Action & Adventure"] = 10759, ["Adventure"] = 10759,
        ["Animation"] = 16, ["Comedy"] = 35, ["Crime"] = 80, ["Documentary"] = 99,
        ["Drama"] = 18, ["Family"] = 10751, ["Fantasy"] = 10765, ["Kids"] = 10762,
        ["Mystery"] = 9648, ["Reality"] = 10764, ["Romance"] = 10749,
        ["Science Fiction"] = 10765, ["Sci-Fi"] = 10765, ["Thriller"] = 9648,
        ["War"] = 10768, ["Western"] = 37,
    };

    /// <summary>
    /// Fetches TMDB Discover results for each of the supplied genre names.
    /// Runs one request per genre (TMDB Discover uses AND for multi-genre, so we query
    /// separately and union the results for better coverage).
    /// Returns at most <paramref name="limitPerGenre"/> items per genre, deduplicated.
    /// </summary>
    public async Task<IReadOnlyList<TmdbCandidate>> DiscoverAsync(
        IEnumerable<string> genreNames,
        bool isMovie,
        HashSet<int> excludeIds,
        int limitPerGenre,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return Array.Empty<TmdbCandidate>();
        }

        var genreMap = isMovie ? MovieGenreIds : TvGenreIds;
        var type = isMovie ? "movie" : "tv";
        var dateField = isMovie ? "release_date" : "first_air_date";
        var titleField = isMovie ? "title" : "name";

        var seen = new HashSet<int>(excludeIds);
        var results = new List<TmdbCandidate>();
        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));

        foreach (var genre in genreNames)
        {
            if (!genreMap.TryGetValue(genre, out var genreId))
            {
                continue;
            }

            var url = $"https://api.themoviedb.org/3/discover/{type}" +
                      $"?api_key={config.TmdbApiKey}" +
                      $"&with_genres={genreId}" +
                      $"&sort_by=vote_average.desc" +
                      $"&vote_count.gte=100" +
                      $"&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

            try
            {
                using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var items))
                {
                    continue;
                }

                var added = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (added >= limitPerGenre)
                    {
                        break;
                    }

                    var id = item.GetProperty("id").GetInt32();
                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    var title = item.TryGetProperty(titleField, out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null;
                    var year = ParseYear(item, dateField);

                    results.Add(new TmdbCandidate
                    {
                        TmdbId = id,
                        Title = title,
                        Year = year,
                        IsSeries = !isMovie,
                        Overview = overview
                    });

                    added++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDB Discover failed for genre {Genre}", genre);
            }
        }

        return results;
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
