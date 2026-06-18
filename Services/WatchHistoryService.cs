using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AIRecommendations.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Reads user watch history from the Jellyfin library.
/// </summary>
public class WatchHistoryService
{
    private readonly ILibraryManager _libraryManager;

    public WatchHistoryService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<WatchedItemSummary> GetWatchedItems(User user, int limit)
    {
        var half = Math.Max(limit / 2, 1);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = half,
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = half,
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        var liked = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            OrderBy = [(ItemSortBy.Random, SortOrder.Descending)],
            Limit = Math.Min(10, limit / 4),
            Recursive = true,
            IsFavoriteOrLiked = true,
            EnableGroupByMetadataKey = true
        });

        return movies
            .Concat(series)
            .Concat(liked)
            .DistinctBy(i => i.Id)
            .Take(limit)
            .Select(MapItem)
            .ToList();
    }

    private static WatchedItemSummary MapItem(BaseItem item)
    {
        item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId);
        var isSeries = item is Series;

        return new WatchedItemSummary
        {
            Title = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            TmdbId = tmdbId,
            IsSeries = isSeries,
            Genres = item.Genres?.ToArray() ?? Array.Empty<string>(),
            Overview = item.Overview,
            UserRating = item is Movie or Series ? null : null
        };
    }
}

/// <summary>
/// Builds exclusion sets from the user's real library.
/// </summary>
public class LibraryFilterService
{
    private readonly ILibraryManager _libraryManager;

    public LibraryFilterService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public HashSet<int> GetOwnedTmdbIds()
    {
        var ids = new HashSet<int>();

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in movies)
        {
            if (item.TryGetProviderId(MetadataProvider.Tmdb, out var idStr)
                && int.TryParse(idStr, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public IReadOnlyList<string> GetOwnedTitles(User user)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false
            })
            .Select(i => i.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
