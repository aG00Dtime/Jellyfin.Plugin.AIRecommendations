using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Submits Jellyseerr media requests for AI recommendations.
/// </summary>
public class JellyseerrService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JellyseerrService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JellyseerrService(IHttpClientFactory httpClientFactory, ILogger<JellyseerrService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Submits requests for all recommendations that are not yet available or pending in Jellyseerr.
    /// Returns the number of new requests created.
    /// </summary>
    public async Task<int> RequestRecommendationsAsync(
        IReadOnlyList<ResolvedRecommendation> recommendations,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)
            || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return 0;
        }

        var baseUrl = config.JellyseerrBaseUrl.TrimEnd('/');
        var requested = 0;

        foreach (var rec in recommendations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var alreadyHandled = await IsAlreadyRequestedOrAvailableAsync(
                    baseUrl, config.JellyseerrApiKey, rec, cancellationToken)
                    .ConfigureAwait(false);

                if (alreadyHandled)
                {
                    _logger.LogDebug(
                        "Jellyseerr: {Title} already requested or available, skipping",
                        rec.Title);
                    continue;
                }

                var created = await SubmitRequestAsync(
                    baseUrl, config.JellyseerrApiKey, rec, cancellationToken)
                    .ConfigureAwait(false);

                if (created)
                {
                    _logger.LogInformation(
                        "Jellyseerr: requested {Type} \"{Title}\" (tmdb {Id})",
                        rec.IsSeries ? "show" : "movie", rec.Title, rec.TmdbId);
                    requested++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellyseerr request failed for {Title}", rec.Title);
            }
        }

        return requested;
    }

    /// <summary>
    /// Fetches popular movies or shows from Jellyseerr's Discover feed, excluding
    /// anything Jellyseerr already knows about (pending/processing/available) and
    /// anything in <paramref name="excludeIds"/>. Returns an empty list (never throws)
    /// if Jellyseerr isn't configured or the request fails, so callers can fall back
    /// to TMDB directly.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedRecommendation>> DiscoverAsync(
        bool isMovie,
        HashSet<int> excludeIds,
        int limit,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)
            || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return [];
        }

        var baseUrl = config.JellyseerrBaseUrl.TrimEnd('/');
        var endpoint = isMovie ? "movies" : "tv";
        var results = new List<ResolvedRecommendation>();

        try
        {
            for (var page = 1; results.Count < limit && page <= 5; page++)
            {
                var client = _httpClientFactory.CreateClient(nameof(JellyseerrService));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/discover/{endpoint}?page={page}");
                request.Headers.Add("X-Api-Key", config.JellyseerrApiKey);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var items) || items.GetArrayLength() == 0)
                {
                    break;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (results.Count >= limit) break;

                    var id = item.GetProperty("id").GetInt32();
                    if (excludeIds.Contains(id)) continue;

                    // mediaInfo.status: 1=unknown, 2=pending, 3=processing, 4=partial, 5=available
                    if (item.TryGetProperty("mediaInfo", out var mediaInfo)
                        && mediaInfo.TryGetProperty("status", out var statusProp)
                        && statusProp.TryGetInt32(out var status)
                        && status >= 2)
                    {
                        continue;
                    }

                    var titleField = isMovie ? "title" : "name";
                    var dateField = isMovie ? "releaseDate" : "firstAirDate";
                    var title = item.TryGetProperty(titleField, out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    int? year = null;
                    if (item.TryGetProperty(dateField, out var dateEl))
                    {
                        var dateStr = dateEl.GetString();
                        if (!string.IsNullOrWhiteSpace(dateStr) && dateStr.Length >= 4
                            && int.TryParse(dateStr.AsSpan(0, 4), out var y))
                        {
                            year = y;
                        }
                    }

                    results.Add(new ResolvedRecommendation
                    {
                        TmdbId = id,
                        Title = title,
                        Year = year,
                        IsSeries = !isMovie,
                        Reason = "Trending now",
                        Overview = item.TryGetProperty("overview", out var ov) ? ov.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyseerr discover failed for {Endpoint}", endpoint);
        }

        return results;
    }

    /// <summary>Returns true if Jellyseerr knows about this item and it's already being handled.</summary>
    private async Task<bool> IsAlreadyRequestedOrAvailableAsync(
        string baseUrl,
        string apiKey,
        ResolvedRecommendation rec,
        CancellationToken cancellationToken)
    {
        var mediaType = rec.IsSeries ? "tv" : "movie";
        var url = $"{baseUrl}/api/v1/{mediaType}/{rec.TmdbId}";

        var client = _httpClientFactory.CreateClient(nameof(JellyseerrService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", apiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        // mediaInfo.status: 1=unknown, 2=pending, 3=processing, 4=partial, 5=available
        if (doc.RootElement.TryGetProperty("mediaInfo", out var mediaInfo)
            && mediaInfo.TryGetProperty("status", out var statusProp)
            && statusProp.TryGetInt32(out var status))
        {
            return status >= 2; // pending, processing, partial, or available
        }

        return false;
    }

    private async Task<bool> SubmitRequestAsync(
        string baseUrl,
        string apiKey,
        ResolvedRecommendation rec,
        CancellationToken cancellationToken)
    {
        var payload = rec.IsSeries
            ? (object)new TvRequestPayload { MediaId = rec.TmdbId }
            : new MovieRequestPayload { MediaId = rec.TmdbId };

        var client = _httpClientFactory.CreateClient(nameof(JellyseerrService));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/request");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 = already exists
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed class MovieRequestPayload
    {
        [JsonPropertyName("mediaType")]
        public string MediaType => "movie";

        [JsonPropertyName("mediaId")]
        public int MediaId { get; set; }
    }

    private sealed class TvRequestPayload
    {
        [JsonPropertyName("mediaType")]
        public string MediaType => "tv";

        [JsonPropertyName("mediaId")]
        public int MediaId { get; set; }

        [JsonPropertyName("seasons")]
        public int[] Seasons => [1];
    }
}
