using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Thin HTTP wrapper for Radarr v3 and Sonarr v3 APIs, plus Jellyseerr status checks.
/// All methods gracefully no-op when the relevant service is not configured.
/// </summary>
public sealed class ArrRequestService
{
    private const string ClientName = "ArrService";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArrRequestService> _logger;

    public ArrRequestService(IHttpClientFactory httpClientFactory, ILogger<ArrRequestService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── Radarr ────────────────────────────────────────────────────────────────

    /// <summary>Adds a movie to Radarr by TMDB ID and triggers a search. Returns false if already present.</summary>
    public async Task<bool> RequestMovieAsync(int tmdbId, string title, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.RadarrBaseUrl)) return false;

        var baseUrl = config.RadarrBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(ClientName);

        var payload = new
        {
            tmdbId,
            title,
            qualityProfileId = config.RadarrQualityProfileId,
            rootFolderPath = config.RadarrRootFolderPath,
            monitored = true,
            addOptions = new { searchForMovie = true }
        };

        using var addReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/movie");
        addReq.Headers.Add("X-Api-Key", config.RadarrApiKey);
        addReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var addResp = await client.SendAsync(addReq, ct).ConfigureAwait(false);

        if (addResp.StatusCode == HttpStatusCode.Conflict) return false;

        if (!addResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Radarr add failed for '{Title}' (TMDB {Id}): {Status}", title, tmdbId, addResp.StatusCode);
            return false;
        }

        _logger.LogInformation("Radarr: queued movie '{Title}' (TMDB {Id})", title, tmdbId);
        return true;
    }

    /// <summary>Returns (exists, hasFile) for a movie in Radarr.</summary>
    public async Task<(bool Exists, bool HasFile, string? Title)> CheckRadarrStatusAsync(int tmdbId, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.RadarrBaseUrl)) return (false, false, null);

        var baseUrl = config.RadarrBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(ClientName);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/movie?tmdbId={tmdbId}");
        req.Headers.Add("X-Api-Key", config.RadarrApiKey);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (false, false, null);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetArrayLength() == 0) return (false, false, null);

        var movie = doc.RootElement[0];
        var hasFile = movie.TryGetProperty("hasFile", out var hf) && hf.GetBoolean();
        var title = movie.TryGetProperty("title", out var t) ? t.GetString() : null;
        return (true, hasFile, title);
    }

    // ── Sonarr ────────────────────────────────────────────────────────────────

    /// <summary>Adds a series to Sonarr by TMDB ID (resolves TVDB internally) and triggers a search.</summary>
    public async Task<bool> RequestShowAsync(int tmdbId, string title, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SonarrBaseUrl)) return false;

        var baseUrl = config.SonarrBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(ClientName);

        // Sonarr lookup by TMDB ID
        using var lookupReq = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/api/v3/series/lookup?term=tmdb:{tmdbId}");
        lookupReq.Headers.Add("X-Api-Key", config.SonarrApiKey);
        using var lookupResp = await client.SendAsync(lookupReq, ct).ConfigureAwait(false);

        if (!lookupResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sonarr lookup failed for TMDB {Id}: {Status}", tmdbId, lookupResp.StatusCode);
            return false;
        }

        var lookupJson = await lookupResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var lookupDoc = JsonDocument.Parse(lookupJson);
        if (lookupDoc.RootElement.GetArrayLength() == 0)
        {
            _logger.LogWarning("Sonarr: no series found for TMDB {Id}", tmdbId);
            return false;
        }

        var series = lookupDoc.RootElement[0];
        var tvdbId = series.TryGetProperty("tvdbId", out var tid) ? tid.GetInt32() : 0;

        // Build seasons list from lookup result
        var seasons = new List<object>();
        if (series.TryGetProperty("seasons", out var seasonsEl))
        {
            foreach (var s in seasonsEl.EnumerateArray())
            {
                var num = s.TryGetProperty("seasonNumber", out var sn) ? sn.GetInt32() : -1;
                if (num >= 0)
                    seasons.Add(new { seasonNumber = num, monitored = num > 0 });
            }
        }

        var payload = new
        {
            tvdbId,
            title,
            qualityProfileId = config.SonarrQualityProfileId,
            rootFolderPath = config.SonarrRootFolderPath,
            monitored = true,
            addOptions = new { searchForMissingEpisodes = true },
            seasons = seasons.ToArray()
        };

        using var addReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/series");
        addReq.Headers.Add("X-Api-Key", config.SonarrApiKey);
        addReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var addResp = await client.SendAsync(addReq, ct).ConfigureAwait(false);

        if (addResp.StatusCode == HttpStatusCode.Conflict) return false;

        if (!addResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sonarr add failed for '{Title}' (TVDB {TvdbId}): {Status}", title, tvdbId, addResp.StatusCode);
            return false;
        }

        _logger.LogInformation("Sonarr: queued show '{Title}' (TMDB {TmdbId}, TVDB {TvdbId})", title, tmdbId, tvdbId);
        return true;
    }

    /// <summary>Returns (exists, percentDownloaded, title) for a series in Sonarr.</summary>
    public async Task<(bool Exists, int PercentDownloaded, string? Title)> CheckSonarrStatusAsync(int tmdbId, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.SonarrBaseUrl)) return (false, 0, null);

        var baseUrl = config.SonarrBaseUrl.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(ClientName);

        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/api/v3/series/lookup?term=tmdb:{tmdbId}");
        req.Headers.Add("X-Api-Key", config.SonarrApiKey);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (false, 0, null);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetArrayLength() == 0) return (false, 0, null);

        var series = doc.RootElement[0];
        // A series with a non-empty "path" is already added to Sonarr
        var hasPath = series.TryGetProperty("path", out var p) && !string.IsNullOrWhiteSpace(p.GetString());
        if (!hasPath) return (false, 0, null);

        var title = series.TryGetProperty("title", out var t) ? t.GetString() : null;
        int pct = 0;
        if (series.TryGetProperty("statistics", out var stats)
            && stats.TryGetProperty("percentOfEpisodes", out var pctEl))
        {
            pct = (int)Math.Round(pctEl.GetDouble());
        }

        return (true, pct, title);
    }

    // ── Jellyseerr ────────────────────────────────────────────────────────────

    /// <summary>Returns (statusCode, statusName, title) from Jellyseerr for a given TMDB ID.</summary>
    public async Task<(int StatusCode, string StatusName, string? Title)> CheckJellyseerrStatusAsync(
        int tmdbId,
        bool isSeries,
        CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)) return (0, "not configured", null);

        var baseUrl = config.JellyseerrBaseUrl.TrimEnd('/');
        var mediaType = isSeries ? "tv" : "movie";
        var client = _httpClientFactory.CreateClient(ClientName);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/{mediaType}/{tmdbId}");
        req.Headers.Add("X-Api-Key", config.JellyseerrApiKey);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) return (0, "not requested", null);
        if (!resp.IsSuccessStatusCode) return (0, "error", null);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Title is at root level for Jellyseerr media details
        var title = root.TryGetProperty("title", out var mt) ? mt.GetString()
            : root.TryGetProperty("name", out var mn) ? mn.GetString()
            : null;

        if (root.TryGetProperty("mediaInfo", out var mi)
            && mi.TryGetProperty("status", out var s)
            && s.TryGetInt32(out var code))
        {
            var name = code switch
            {
                1 => "unknown",
                2 => "pending",
                3 => "processing",
                4 => "partial",
                5 => "available",
                _ => "unknown"
            };
            return (code, name, title);
        }

        return (0, "not requested", title);
    }
}
