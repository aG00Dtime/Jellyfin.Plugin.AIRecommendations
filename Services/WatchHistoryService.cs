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

    /// <summary>
    /// Builds a compact taste profile from the user's watch history and favourites.
    /// This is what gets sent to the LLM instead of a raw item list.
    /// </summary>
    public UserTasteProfile BuildTasteProfile(User user, int maxWatched)
    {
        var half = Math.Max(maxWatched / 2, 1);

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

        var favorites = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Limit = 10,
            Recursive = true,
            IsFavoriteOrLiked = true,
            EnableGroupByMetadataKey = true
        });

        var allWatched = movies.Concat(series).DistinctBy(i => i.Id).ToList();

        if (allWatched.Count == 0)
        {
            return new UserTasteProfile { TotalWatched = 0 };
        }

        var topGenres = allWatched
            .SelectMany(i => i.Genres ?? Array.Empty<string>())
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => (g.Key, g.Count()))
            .ToList();

        var years = allWatched
            .Where(i => i.ProductionYear.HasValue)
            .Select(i => i.ProductionYear!.Value)
            .ToList();

        var moviePct = allWatched.Count > 0 ? movies.Count * 100 / allWatched.Count : 50;

        // Representative sample spread across watch history (not just most recent)
        var step = Math.Max(1, allWatched.Count / 8);
        var sample = allWatched
            .Where((_, i) => i % step == 0)
            .Take(8)
            .Select(i => i.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var favTitles = favorites
            .Select(i => i.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(5)
            .ToList();

        return new UserTasteProfile
        {
            TotalWatched = allWatched.Count,
            TopGenres = topGenres,
            EraPreference = BuildEraLabel(years),
            MoviePercent = moviePct,
            SampleTitles = sample,
            FavoriteTitles = favTitles
        };
    }

    /// <summary>
    /// Returns TMDB IDs of everything the user has played — used to exclude already-watched
    /// content from recommendations. Includes partially-watched shows (any played episode)
    /// so the LLM doesn't re-recommend something the user is already partway through.
    /// </summary>
    public HashSet<int> GetWatchedTmdbIds(User user)
    {
        var ids = new HashSet<int>();

        // Fully-played movies and completed series
        var fullyWatched = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        foreach (var item in fullyWatched)
        {
            if (item.TryGetProviderId(MetadataProvider.Tmdb, out var idStr)
                && int.TryParse(idStr, out var id))
            {
                ids.Add(id);
            }
        }

        // Also include series where the user has watched ANY episode (partial watches)
        // IsPlayed=true on Series requires all episodes — this catches the rest.
        var watchedEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsPlayed = true,
            Limit = 2000,
            EnableGroupByMetadataKey = true
        });

        foreach (var ep in watchedEpisodes)
        {
            // Get the series: Episode → Season → Series (or Episode → Series if no season folder)
            var series = ep.GetParent() as MediaBrowser.Controller.Entities.TV.Series
                ?? ep.GetParent()?.GetParent() as MediaBrowser.Controller.Entities.TV.Series;

            if (series is not null
                && series.TryGetProviderId(MetadataProvider.Tmdb, out var seriesIdStr)
                && int.TryParse(seriesIdStr, out var seriesId))
            {
                ids.Add(seriesId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Returns titles of everything the user has watched — used as LLM exclusion hints.
    /// </summary>
    public IReadOnlyList<string> GetWatchedTitles(User user, int limit = 150)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsPlayed = true,
            Limit = limit,
            EnableGroupByMetadataKey = true
        });

        return items
            .Select(i => i.Name ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildEraLabel(List<int> years)
    {
        if (years.Count == 0) return "varied";
        var modern = years.Count(y => y >= 2000);
        var pct = modern * 100 / years.Count;
        if (pct >= 75) return "mostly modern (2000s–2020s)";
        if (pct >= 40) return "mix of classic and modern";
        return "mostly classic (pre-2000s)";
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

        // Exclude AI recommendation library folders so stubs don't count as "owned"
        // and block the engine from ever generating new recommendations.
        var aiPaths = Plugin.Instance?.Configuration.UserLibraries
            .SelectMany(r => new[] { r.MoviePath, r.ShowPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? [];

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in items)
        {
            if (aiPaths.Count > 0
                && aiPaths.Any(p => item.Path?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

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
