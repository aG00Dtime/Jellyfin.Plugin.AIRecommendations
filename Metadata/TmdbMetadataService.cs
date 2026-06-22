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
        CancellationToken cancellationToken,
        int page = 1)
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

            var langParam = !string.IsNullOrWhiteSpace(config.DiscoverLanguage)
                ? $"&with_original_language={Uri.EscapeDataString(config.DiscoverLanguage)}"
                : string.Empty;

            var url = $"https://api.themoviedb.org/3/discover/{type}" +
                      $"?api_key={config.TmdbApiKey}" +
                      $"&with_genres={genreId}" +
                      $"&sort_by=popularity.desc" +
                      $"&vote_count.gte=50" +
                      $"&page={Math.Clamp(page, 1, 5)}" +
                      $"&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}" +
                      langParam;

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

    /// <summary>
    /// Searches TMDB across movies AND TV simultaneously using the multi-search endpoint.
    /// Returns up to <paramref name="limit"/> results, optionally sorted so the preferred type comes first.
    /// </summary>
    public async Task<IReadOnlyList<TmdbCandidate>> SearchMultiAsync(
        string query,
        string? preferType,
        int? year,
        int limit,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            return Array.Empty<TmdbCandidate>();

        var q = Uri.EscapeDataString(query);
        var url = $"https://api.themoviedb.org/3/search/multi?api_key={config.TmdbApiKey}" +
                  $"&query={q}&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));
        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Array.Empty<TmdbCandidate>();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results))
            return Array.Empty<TmdbCandidate>();

        var list = new List<TmdbCandidate>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("media_type", out var mt)) continue;
            var mediaType = mt.GetString();
            if (mediaType != "movie" && mediaType != "tv") continue;

            var isMovie = mediaType == "movie";
            var title = isMovie
                ? (item.TryGetProperty("title",    out var t) ? t.GetString() : null)
                : (item.TryGetProperty("name",     out var n) ? n.GetString() : null);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var resultYear = isMovie
                ? ParseYear(item, "release_date")
                : ParseYear(item, "first_air_date");

            // If year specified, skip results more than 1 year off
            if (year.HasValue && resultYear.HasValue && Math.Abs(resultYear.Value - year.Value) > 1)
                continue;

            list.Add(new TmdbCandidate
            {
                TmdbId   = item.GetProperty("id").GetInt32(),
                Title    = title,
                Year     = resultYear,
                IsSeries = !isMovie,
                Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null
            });
        }

        // Sort preferred type first, then by position (TMDB already ranks by relevance)
        if (!string.IsNullOrEmpty(preferType))
        {
            var preferSeries = preferType.Equals("tv", StringComparison.OrdinalIgnoreCase)
                            || preferType.Equals("series", StringComparison.OrdinalIgnoreCase);
            list = [.. list.OrderBy(r => r.IsSeries == preferSeries ? 0 : 1)];
        }

        return list.Take(limit).ToList();
    }

    /// <summary>
    /// Checks TMDB release dates to determine if a movie is available digitally/physically,
    /// currently in theaters only, or not yet released.
    /// Uses US release dates by default (the most complete dataset on TMDB).
    /// </summary>
    public async Task<MovieAvailability> GetMovieAvailabilityAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            return MovieAvailability.Unknown;

        var url = $"https://api.themoviedb.org/3/movie/{tmdbId}/release_dates?api_key={config.TmdbApiKey}";
        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));

        try
        {
            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return MovieAvailability.Unknown;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results))
                return MovieAvailability.Unknown;

            // Prefer US; fall back to first available country
            JsonElement? countryDates = null;
            foreach (var country in results.EnumerateArray())
            {
                var iso = country.TryGetProperty("iso_3166_1", out var c) ? c.GetString() : null;
                if (iso == "US")
                {
                    countryDates = country;
                    break;
                }
                countryDates ??= country;
            }

            if (countryDates is null) return MovieAvailability.Unknown;
            if (!countryDates.Value.TryGetProperty("release_dates", out var dates))
                return MovieAvailability.Unknown;

            var now = DateTime.UtcNow;
            bool hasTheatrical = false;
            bool hasDigital    = false;
            DateTime? digitalDate = null;

            foreach (var d in dates.EnumerateArray())
            {
                // type: 1=Premiere, 2=Theatrical(limited), 3=Theatrical, 4=Digital, 5=Physical, 6=TV
                if (!d.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetInt32();

                var dateStr = d.TryGetProperty("release_date", out var rd) ? rd.GetString() : null;
                if (!DateTime.TryParse(dateStr, out var releaseDate)) continue;

                if (type is 3 or 2 && releaseDate <= now) hasTheatrical = true;
                if (type is 4 or 5)
                {
                    if (releaseDate <= now)
                        hasDigital = true;
                    else if (digitalDate is null || releaseDate < digitalDate)
                        digitalDate = releaseDate;
                }
            }

            if (hasDigital)  return MovieAvailability.Digital;
            if (hasTheatrical)
            {
                return digitalDate.HasValue
                    ? MovieAvailability.TheatersOnly with { UpcomingDigitalDate = digitalDate }
                    : MovieAvailability.TheatersOnly;
            }

            return digitalDate.HasValue
                ? MovieAvailability.Upcoming with { UpcomingDigitalDate = digitalDate }
                : MovieAvailability.NotReleased;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetMovieAvailabilityAsync failed for tmdbId {Id}", tmdbId);
            return MovieAvailability.Unknown;
        }
    }

    /// <summary>
    /// Fetches TMDB's curated /recommendations list for a given title.
    /// Better signal-to-noise than /similar — TMDB editorial picks based on the seed title.
    /// </summary>
    public async Task<IReadOnlyList<TmdbCandidate>> GetTmdbRecommendationsAsync(
        int tmdbId,
        bool isSeries,
        HashSet<int> excludeIds,
        int limit,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            return Array.Empty<TmdbCandidate>();

        var type       = isSeries ? "tv" : "movie";
        var titleField = isSeries ? "name" : "title";
        var dateField  = isSeries ? "first_air_date" : "release_date";
        var url = $"https://api.themoviedb.org/3/{type}/{tmdbId}/recommendations?api_key={config.TmdbApiKey}";

        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));
        try
        {
            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<TmdbCandidate>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return Array.Empty<TmdbCandidate>();

            var list = new List<TmdbCandidate>();
            foreach (var item in results.EnumerateArray())
            {
                if (list.Count >= limit) break;

                var id = item.GetProperty("id").GetInt32();
                if (excludeIds.Contains(id)) continue;

                var title = item.TryGetProperty(titleField, out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;

                list.Add(new TmdbCandidate
                {
                    TmdbId   = id,
                    Title    = title,
                    Year     = ParseYear(item, dateField),
                    IsSeries = isSeries,
                    Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetTmdbRecommendationsAsync failed for {Type} {Id}", type, tmdbId);
            return Array.Empty<TmdbCandidate>();
        }
    }

    /// <summary>
    /// Fetches a named TMDB list (popular, top_rated, trending, now_playing, upcoming, etc.)
    /// for movies or TV. <paramref name="page"/> enables pagination for variety.
    /// </summary>
    public async Task<IReadOnlyList<TmdbCandidate>> BrowseTmdbAsync(
        string category,
        bool isMovie,
        int page,
        int limit,
        HashSet<int> excludeIds,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            return Array.Empty<TmdbCandidate>();

        var type = isMovie ? "movie" : "tv";

        var endpoint = category switch
        {
            "top_rated"     => $"{type}/top_rated",
            "trending_week" => $"trending/{type}/week",
            "trending_day"  => $"trending/{type}/day",
            "now_playing"   => isMovie ? "movie/now_playing" : "tv/airing_today",
            "upcoming"      => isMovie ? "movie/upcoming"    : "tv/on_the_air",
            "airing_today"  => "tv/airing_today",
            "on_the_air"    => "tv/on_the_air",
            _               => $"{type}/popular"
        };

        var url = $"https://api.themoviedb.org/3/{endpoint}" +
                  $"?api_key={config.TmdbApiKey}" +
                  $"&page={Math.Clamp(page, 1, 5)}" +
                  $"&include_adult={config.IncludeAdult.ToString().ToLowerInvariant()}";

        var client = _httpClientFactory.CreateClient(nameof(TmdbMetadataService));
        try
        {
            using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<TmdbCandidate>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return Array.Empty<TmdbCandidate>();

            var list = new List<TmdbCandidate>();
            foreach (var item in results.EnumerateArray())
            {
                if (list.Count >= limit) break;

                var id = item.GetProperty("id").GetInt32();
                if (excludeIds.Contains(id)) continue;

                // trending endpoints carry per-item media_type — use it to pick the right fields
                var itemIsSeries = !isMovie;
                var titleField   = isMovie ? "title" : "name";
                var dateField    = isMovie ? "release_date" : "first_air_date";

                if (item.TryGetProperty("media_type", out var mt))
                {
                    itemIsSeries = mt.GetString() == "tv";
                    titleField   = itemIsSeries ? "name"           : "title";
                    dateField    = itemIsSeries ? "first_air_date" : "release_date";
                }

                var title = item.TryGetProperty(titleField, out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;

                list.Add(new TmdbCandidate
                {
                    TmdbId   = id,
                    Title    = title,
                    Year     = ParseYear(item, dateField),
                    IsSeries = itemIsSeries,
                    Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrowseTmdbAsync failed for {Category}/{Type} page {Page}", category, type, page);
            return Array.Empty<TmdbCandidate>();
        }
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
