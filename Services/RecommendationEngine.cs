using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Orchestrates LLM + TMDB recommendation generation.
///
/// RAG path (default): fetches TMDB Discover candidates per genre first, passes
/// the catalog to the LLM so it picks from real, current data (no hallucination).
/// The LLM returns tmdbIds from the catalog — no TMDB search round needed.
///
/// Fallback path: if Discover yields too few candidates the engine falls back to
/// free-form LLM generation with a 2-round TMDB verification loop.
/// </summary>
public class RecommendationEngine
{
    private readonly WatchHistoryService _watchHistory;
    private readonly LibraryFilterService _libraryFilter;
    private readonly LlmProviderFactory _llmFactory;
    private readonly TmdbMetadataService _tmdb;
    private readonly ILogger<RecommendationEngine> _logger;

    // How many catalog items to fetch per genre (movies + shows separately)
    private const int DiscoverLimitPerGenre = 10;

    // Minimum catalog size (per type) to use RAG mode; fall back to free-form below this
    private const int MinCatalogPerType = 5;

    public RecommendationEngine(
        WatchHistoryService watchHistory,
        LibraryFilterService libraryFilter,
        LlmProviderFactory llmFactory,
        TmdbMetadataService tmdb,
        ILogger<RecommendationEngine> logger)
    {
        _watchHistory = watchHistory;
        _libraryFilter = libraryFilter;
        _llmFactory = llmFactory;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ResolvedRecommendation>> GenerateForUserAsync(
        User user,
        IReadOnlyCollection<int> extraExcludeIds,
        CancellationToken cancellationToken,
        string? tasteProfileText = null)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized");

        var profile = _watchHistory.BuildTasteProfile(user, config.MaxWatchedItems);
        if (profile.TotalWatched == 0)
        {
            _logger.LogInformation("User {User} has no watch history; skipping", user.Username);
            return Array.Empty<ResolvedRecommendation>();
        }

        var target = config.MaxRecommendationsPerType;

        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var watchedIds = _watchHistory.GetWatchedTmdbIds(user);
        var seenTmdbIds = new HashSet<int>(ownedIds);
        seenTmdbIds.UnionWith(watchedIds);
        seenTmdbIds.UnionWith(extraExcludeIds);

        // Exclude items the user started but abandoned — softer dislike signal
        var abandonedIds = _watchHistory.GetAbandonedTmdbIds(user);
        seenTmdbIds.UnionWith(abandonedIds);
        if (abandonedIds.Count > 0)
            _logger.LogInformation("Excluding {Count} abandoned-title IDs for {User}", abandonedIds.Count, user.Username);

        var ownedTitles = _libraryFilter.GetOwnedTitles(user);
        var watchedTitles = _watchHistory.GetWatchedTitles(user);
        var baseTitleExcludes = ownedTitles
            .Concat(watchedTitles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var excludedTitleSet = new HashSet<string>(baseTitleExcludes, StringComparer.OrdinalIgnoreCase);

        // Seed TMDB /recommendations from top loved titles (≥90% completion)
        var completionData = _watchHistory.GetWatchedWithCompletion(user, 100);
        var lovedMovieIds = completionData
            .Where(w => !w.IsSeries && w.Tag == WatchCompletion.Loved && w.TmdbId.HasValue)
            .Take(3)
            .Select(w => w.TmdbId!.Value)
            .ToList();
        var lovedShowIds = completionData
            .Where(w => w.IsSeries && w.Tag == WatchCompletion.Loved && w.TmdbId.HasValue)
            .Take(3)
            .Select(w => w.TmdbId!.Value)
            .ToList();

        // === RAG: Discover + loved-title TMDB recommendations in parallel ===
        var topGenres = profile.TopGenres.Select(g => g.Genre).ToList();
        var catalogTask = FetchCatalogAsync(topGenres, seenTmdbIds, cancellationToken);

        var tmdbRecTasks = lovedMovieIds
            .Select(id => _tmdb.GetTmdbRecommendationsAsync(id, false, seenTmdbIds, 10, cancellationToken))
            .Concat(lovedShowIds.Select(id => _tmdb.GetTmdbRecommendationsAsync(id, true, seenTmdbIds, 10, cancellationToken)))
            .ToList();

        var tmdbRecs = tmdbRecTasks.Count > 0
            ? await Task.WhenAll(tmdbRecTasks).ConfigureAwait(false)
            : Array.Empty<IReadOnlyList<TmdbCandidate>>();

        var (movieCatalog, showCatalog) = await catalogTask.ConfigureAwait(false);

        // Merge /recommendations into Discover catalog (deduplicated by TMDB ID)
        var recSeen = new HashSet<int>(seenTmdbIds);
        var totalRecAdded = 0;
        foreach (var recList in tmdbRecs)
        {
            foreach (var c in recList)
            {
                if (recSeen.Add(c.TmdbId))
                {
                    totalRecAdded++;
                    if (c.IsSeries) showCatalog.Add(c);
                    else movieCatalog.Add(c);
                }
            }
        }

        _logger.LogInformation(
            "RAG catalog for {User}: {Movies} movie candidates, {Shows} show candidates " +
            "(+{Recs} from TMDB /recommendations, {Loved} loved seeds)",
            user.Username, movieCatalog.Count, showCatalog.Count, totalRecAdded,
            lovedMovieIds.Count + lovedShowIds.Count);

        var catalogById = movieCatalog.Concat(showCatalog)
            .ToDictionary(c => c.TmdbId);

        var llm = _llmFactory.GetActiveProvider();

        var confirmed = new List<ResolvedRecommendation>();
        var notFoundTitles = new List<string>();
        var confirmedTitles = new List<string>();

        const int maxRounds = 2;
        for (var round = 0; round < maxRounds; round++)
        {
            var moviesHave = confirmed.Count(r => !r.IsSeries);
            var showsHave = confirmed.Count(r => r.IsSeries);
            var moviesNeed = Math.Max(0, target - moviesHave);
            var showsNeed = Math.Max(0, target - showsHave);

            if (moviesNeed == 0 && showsNeed == 0)
            {
                break;
            }

            // In RAG mode we ask for exactly what we need (catalog items are pre-verified).
            // In free-form mode we ask for slightly more to account for TMDB misses.
            var useRag = movieCatalog.Count >= MinCatalogPerType || showCatalog.Count >= MinCatalogPerType;

            var ask = useRag
                ? moviesNeed + showsNeed
                : (round == 0
                    ? Math.Min(moviesNeed + showsNeed + 5, 25)
                    : moviesNeed + showsNeed);

            var allExclude = baseTitleExcludes
                .Concat(confirmedTitles)
                .ToList();

            // Build per-round catalog filtered to items not yet confirmed/seen
            List<TmdbCandidate>? roundCatalog = null;
            if (useRag)
            {
                var availableMovies = movieCatalog
                    .Where(c => !seenTmdbIds.Contains(c.TmdbId))
                    .ToList();
                var availableShows = showCatalog
                    .Where(c => !seenTmdbIds.Contains(c.TmdbId))
                    .ToList();
                roundCatalog = availableMovies.Concat(availableShows).ToList();

                if (roundCatalog.Count == 0)
                {
                    _logger.LogInformation(
                        "Catalog exhausted for {User} on round {Round} — stopping",
                        user.Username, round + 1);
                    break;
                }
            }

            _logger.LogInformation(
                "LLM round {Round} for {User}: asking for {Ask} candidates " +
                "(need {Movies} movies, {Shows} shows, mode={Mode})",
                round + 1, user.Username, ask, moviesNeed, showsNeed,
                useRag ? "RAG" : "free-form");

            IReadOnlyList<LlmRecommendationItem> candidates;
            try
            {
                candidates = await llm.GetRecommendationsAsync(
                    profile, allExclude, ask, cancellationToken,
                    round > 0 ? notFoundTitles : null,
                    roundCatalog,
                    tasteProfileText)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM call failed on round {Round} for {User}", round + 1, user.Username);
                break;
            }

            if (candidates.Count == 0)
            {
                _logger.LogWarning("LLM returned 0 candidates on round {Round}", round + 1);
                break;
            }

            var results = await ResolveRecommendationsAsync(
                candidates, seenTmdbIds, catalogById, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (item, rec) in results)
            {
                if (rec is not null && !excludedTitleSet.Contains(rec.Title))
                {
                    seenTmdbIds.Add(rec.TmdbId);
                    excludedTitleSet.Add(rec.Title);
                    confirmedTitles.Add(item.Title);
                    confirmed.Add(rec);
                }
                else
                {
                    notFoundTitles.Add(item.Title);
                }
            }

            _logger.LogInformation(
                "Round {Round} result: {Confirmed} confirmed, {Failed} skipped",
                round + 1, results.Count(r => r.rec is not null), results.Count(r => r.rec is null));
        }

        return confirmed;
    }

    private async Task<(List<TmdbCandidate> movies, List<TmdbCandidate> shows)> FetchCatalogAsync(
        IReadOnlyList<string> genreNames,
        HashSet<int> excludeIds,
        CancellationToken cancellationToken)
    {
        var movieTask = _tmdb.DiscoverAsync(genreNames, isMovie: true, excludeIds, DiscoverLimitPerGenre, cancellationToken);
        var showTask = _tmdb.DiscoverAsync(genreNames, isMovie: false, excludeIds, DiscoverLimitPerGenre, cancellationToken);

        await Task.WhenAll(movieTask, showTask).ConfigureAwait(false);

        return (movieTask.Result.ToList(), showTask.Result.ToList());
    }

    private async Task<IReadOnlyList<(LlmRecommendationItem item, ResolvedRecommendation? rec)>> ResolveRecommendationsAsync(
        IReadOnlyList<LlmRecommendationItem> candidates,
        HashSet<int> seenTmdbIds,
        Dictionary<int, TmdbCandidate> catalogById,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(8, 8);

        var tasks = candidates.Select(async item =>
        {
            // Catalog (RAG) item: resolve directly without a TMDB search request
            if (item.TmdbId.HasValue && catalogById.TryGetValue(item.TmdbId.Value, out var candidate))
            {
                if (seenTmdbIds.Contains(candidate.TmdbId))
                {
                    return (item, (ResolvedRecommendation?)null);
                }

                return (item, (ResolvedRecommendation?)new ResolvedRecommendation
                {
                    TmdbId = candidate.TmdbId,
                    Title = candidate.Title,
                    Year = candidate.Year,
                    IsSeries = candidate.IsSeries,
                    Reason = item.Reason,
                    Overview = candidate.Overview
                });
            }

            // Free-form item: fall back to TMDB title search
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rec = await _tmdb.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
                if (rec is null || seenTmdbIds.Contains(rec.TmdbId))
                {
                    return (item, (ResolvedRecommendation?)null);
                }

                return (item, rec);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDB lookup failed for {Title}", item.Title);
                return (item, (ResolvedRecommendation?)null);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
