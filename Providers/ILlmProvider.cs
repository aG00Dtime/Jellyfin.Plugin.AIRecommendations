using Jellyfin.Plugin.AIRecommendations.Models;

namespace Jellyfin.Plugin.AIRecommendations.Providers;

/// <summary>
/// LLM provider abstraction.
/// </summary>
public interface ILlmProvider
{
    string Name { get; }

    Task<IReadOnlyList<LlmRecommendationItem>> GetRecommendationsAsync(
        IReadOnlyList<WatchedItemSummary> watchedItems,
        IReadOnlyList<string> excludeTitles,
        int count,
        CancellationToken cancellationToken);
}
