using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Orchestrates LLM + TMDB recommendation generation with a 2-round feedback loop.
///
/// Round 1: ask LLM for 3× the target count, verify all via TMDB in parallel.
/// Round 2 (only if still below target): ask again for the deficit, passing back
///   titles that failed TMDB lookup so the LLM avoids re-suggesting them.
/// </summary>
public class RecommendationEngine
{
    private readonly WatchHistoryService _watchHistory;
    private readonly LibraryFilterService _libraryFilter;
    private readonly LlmProviderFactory _llmFactory;
    private readonly TmdbMetadataService _tmdb;
    private readonly ILogger<RecommendationEngine> _logger;

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
        CancellationToken cancellationToken)
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

        // Exclude: library ownership + everything the user has personally watched
        // (watched check prevents checkmarks on stubs for already-seen content)
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var watchedIds = _watchHistory.GetWatchedTmdbIds(user);
        var seenTmdbIds = new HashSet<int>(ownedIds);
        seenTmdbIds.UnionWith(watchedIds);

        // Prompt-level hints: library titles + watched titles help the LLM avoid suggesting them
        var ownedTitles = _libraryFilter.GetOwnedTitles(user);
        var watchedTitles = _watchHistory.GetWatchedTitles(user);
        var baseTitleExcludes = ownedTitles
            .Concat(watchedTitles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

            // Round 1: ask for need+5 (buffer for TMDB misses, capped at 25 to keep prompts small)
            // Round 2: ask exactly the deficit
            var ask = round == 0
                ? Math.Min(moviesNeed + showsNeed + 5, 25)
                : moviesNeed + showsNeed;

            var allExclude = baseTitleExcludes
                .Concat(confirmedTitles)
                .ToList();

            _logger.LogInformation(
                "LLM round {Round}: asking for {Ask} candidates (need {Movies} movies, {Shows} shows) for {User}",
                round + 1, ask, moviesNeed, showsNeed, user.Username);

            IReadOnlyList<LlmRecommendationItem> candidates;
            try
            {
                candidates = await llm.GetRecommendationsAsync(
                    profile, allExclude, ask, cancellationToken,
                    round > 0 ? notFoundTitles : null)
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

            // Verify candidates via TMDB in parallel (capped at 8 concurrent)
            var results = await VerifyViaTmdbAsync(candidates, seenTmdbIds, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (item, rec) in results)
            {
                if (rec is not null)
                {
                    seenTmdbIds.Add(rec.TmdbId);
                    confirmedTitles.Add(item.Title);
                    confirmed.Add(rec);
                }
                else
                {
                    notFoundTitles.Add(item.Title);
                }
            }

            _logger.LogInformation(
                "Round {Round} result: {Confirmed} confirmed, {Failed} not found on TMDB",
                round + 1, results.Count(r => r.rec is not null), results.Count(r => r.rec is null));
        }

        return confirmed;
    }

    private async Task<IReadOnlyList<(LlmRecommendationItem item, ResolvedRecommendation? rec)>> VerifyViaTmdbAsync(
        IReadOnlyList<LlmRecommendationItem> candidates,
        HashSet<int> seenTmdbIds,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(8, 8);

        var tasks = candidates.Select(async item =>
        {
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
