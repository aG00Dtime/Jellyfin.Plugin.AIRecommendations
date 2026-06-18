using Jellyfin.Data.Entities;
using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Orchestrates LLM + TMDB recommendation generation.
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

        var watched = _watchHistory.GetWatchedItems(user, config.MaxWatchedItems);
        if (watched.Count == 0)
        {
            _logger.LogInformation("User {User} has no watch history; skipping", user.Username);
            return Array.Empty<ResolvedRecommendation>();
        }

        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var excludeTitles = _libraryFilter.GetOwnedTitles(user);

        var llm = _llmFactory.GetActiveProvider();
        var requestCount = config.MaxRecommendationsPerType * 2;

        var llmItems = await llm.GetRecommendationsAsync(watched, excludeTitles, requestCount, cancellationToken)
            .ConfigureAwait(false);

        var resolved = new List<ResolvedRecommendation>();
        var seenIds = new HashSet<int>();

        foreach (var item in llmItems)
        {
            if (resolved.Count >= requestCount)
            {
                break;
            }

            try
            {
                var rec = await _tmdb.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
                if (rec is null || ownedIds.Contains(rec.TmdbId) || !seenIds.Add(rec.TmdbId))
                {
                    continue;
                }

                resolved.Add(rec);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve recommendation {Title}", item.Title);
            }
        }

        return resolved;
    }
}
