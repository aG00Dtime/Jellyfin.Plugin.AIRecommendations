using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Maintains the shared (not per-user) "Discover" library of trending/popular movies
/// and shows. Sourced from Jellyseerr's Discover feed when configured; falls back to
/// TMDB's popular/trending lists directly so the feature works even without
/// Jellyseerr, matching the plugin's general "don't hard-depend on Jellyseerr" stance.
/// </summary>
public class DiscoverSyncService
{
    private const int TmdbPagesPerType = 3;

    private readonly VirtualLibraryManager _virtualLibraryManager;
    private readonly VirtualItemWriter _itemWriter;
    private readonly JellyseerrService _jellyseerr;
    private readonly TmdbMetadataService _tmdb;
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryFilterService _libraryFilter;
    private readonly ILogger<DiscoverSyncService> _logger;

    public DiscoverSyncService(
        VirtualLibraryManager virtualLibraryManager,
        VirtualItemWriter itemWriter,
        JellyseerrService jellyseerr,
        TmdbMetadataService tmdb,
        ILibraryManager libraryManager,
        LibraryFilterService libraryFilter,
        ILogger<DiscoverSyncService> logger)
    {
        _virtualLibraryManager = virtualLibraryManager;
        _itemWriter = itemWriter;
        _jellyseerr = jellyseerr;
        _tmdb = tmdb;
        _libraryManager = libraryManager;
        _libraryFilter = libraryFilter;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.DiscoverLibraryEnabled)
        {
            return;
        }

        await _virtualLibraryManager.EnsureDiscoverLibrariesAsync(cancellationToken).ConfigureAwait(false);

        var excludeIds = _libraryFilter.GetOwnedTmdbIds();
        excludeIds.UnionWith(config.DiscoverRejectedTmdbIds);

        var movies = await FetchCandidatesAsync(isMovie: true, excludeIds, config.DiscoverItemsPerType, cancellationToken)
            .ConfigureAwait(false);
        excludeIds.UnionWith(movies.Select(m => m.TmdbId));
        var shows = await FetchCandidatesAsync(isMovie: false, excludeIds, config.DiscoverItemsPerType, cancellationToken)
            .ConfigureAwait(false);

        var all = movies.Concat(shows).ToList();

        // Remove stubs for content now present in the real library (e.g. a download completed)
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var removeIds = new HashSet<int>(config.DiscoverRejectedTmdbIds);
        removeIds.UnionWith(config.DiscoverPlacedTmdbIds.Where(id => ownedIds.Contains(id)));

        var placedIds = _itemWriter.SyncRecommendations(
            config.DiscoverMoviePath,
            config.DiscoverShowPath,
            all,
            removeIds,
            limitShowsToSeasonOne: true);

        config.DiscoverPlacedTmdbIds = placedIds.ToList();
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation("Discover: synced {MovieCount} movies and {ShowCount} shows", movies.Count, shows.Count);

        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ResolvedRecommendation>> FetchCandidatesAsync(
        bool isMovie,
        HashSet<int> excludeIds,
        int limit,
        CancellationToken cancellationToken)
    {
        var viaJellyseerr = await _jellyseerr.DiscoverAsync(isMovie, excludeIds, limit, cancellationToken)
            .ConfigureAwait(false);
        if (viaJellyseerr.Count > 0)
        {
            return viaJellyseerr;
        }

        _logger.LogInformation(
            "Discover: Jellyseerr unavailable or returned nothing for {Type}, falling back to TMDB",
            isMovie ? "movies" : "shows");

        var results = new List<ResolvedRecommendation>();
        for (var page = 1; results.Count < limit && page <= TmdbPagesPerType; page++)
        {
            var candidates = await _tmdb.BrowseTmdbAsync("popular", isMovie, page, limit, excludeIds, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            foreach (var c in candidates)
            {
                if (results.Count >= limit) break;
                results.Add(new ResolvedRecommendation
                {
                    TmdbId = c.TmdbId,
                    Title = c.Title,
                    Year = c.Year,
                    IsSeries = c.IsSeries,
                    Reason = "Trending now",
                    Overview = c.Overview
                });
            }
        }

        return results;
    }
}
