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
    private readonly IUserDataManager _userDataManager;

    public WatchHistoryService(ILibraryManager libraryManager, IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
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

        // Exclude AI stub paths so stub shows that Jellyfin considers "played"
        // (due to having only virtual episodes) don't pollute the watched set.
        var aiPaths = Plugin.Instance?.Configuration.UserLibraries
            .SelectMany(r => new[] { r.MoviePath, r.ShowPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? [];

        bool IsAiStub(BaseItem item) =>
            aiPaths.Count > 0
            && aiPaths.Any(p => item.Path?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true);

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
            if (IsAiStub(item))
            {
                continue;
            }

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
            Limit = 2000
        });

        foreach (var ep in watchedEpisodes)
        {
            if (IsAiStub(ep))
            {
                continue;
            }

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

    /// <summary>
    /// Returns watched movies and series annotated with completion percentage.
    /// Also includes partially-started items tagged as Abandoned.
    /// </summary>
    public List<WatchedItemDetail> GetWatchedWithCompletion(User user, int limit = 200)
    {
        var aiPaths = Plugin.Instance?.Configuration.UserLibraries
            .SelectMany(r => new[] { r.MoviePath, r.ShowPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? [];

        bool IsAiStub(BaseItem item) =>
            aiPaths.Count > 0
            && aiPaths.Any(p => item.Path?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true);

        var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = limit,
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        var resumable = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsResumable = true,
            Limit = 200
        });

        var seenIds = new HashSet<Guid>();
        var results = new List<WatchedItemDetail>();

        foreach (var item in played)
        {
            if (IsAiStub(item) || !seenIds.Add(item.Id)) continue;
            var ud = _userDataManager.GetUserData(user, item);
            var pct = ComputeCompletionPct(ud, item);
            var tag = pct >= 90.0 ? WatchCompletion.Loved : WatchCompletion.Watched;
            results.Add(BuildDetail(item, pct, tag));
        }

        foreach (var item in resumable)
        {
            if (IsAiStub(item) || !seenIds.Add(item.Id)) continue;
            var ud = _userDataManager.GetUserData(user, item);
            var pct = ComputeCompletionPct(ud, item);
            if (pct <= 0 || pct >= 30) continue;
            results.Add(BuildDetail(item, pct, WatchCompletion.Abandoned));
        }

        return results;
    }

    /// <summary>
    /// Returns TMDB IDs of items the user started but abandoned (0–30% watched).
    /// Added to the recommendation exclusion set so the engine doesn't re-suggest them.
    /// </summary>
    public HashSet<int> GetAbandonedTmdbIds(User user)
    {
        var ids = new HashSet<int>();
        var aiPaths = Plugin.Instance?.Configuration.UserLibraries
            .SelectMany(r => new[] { r.MoviePath, r.ShowPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList() ?? [];

        var resumable = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsResumable = true,
            Limit = 200
        });

        foreach (var item in resumable)
        {
            if (aiPaths.Count > 0 && aiPaths.Any(p => item.Path?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true))
                continue;

            var ud = _userDataManager.GetUserData(user, item);
            var pct = ComputeCompletionPct(ud, item);
            if (pct <= 0 || pct >= 30) continue;

            if (item.TryGetProviderId(MetadataProvider.Tmdb, out var idStr)
                && int.TryParse(idStr, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Computes 0–100 completion percentage from user data and item runtime.
    /// Favorites and highly-rated items are treated as fully loved (100%).
    /// For played items with no position data the value defaults to 100.
    /// </summary>
    private static double ComputeCompletionPct(UserItemData? ud, BaseItem item)
    {
        if (ud is null) return 0.0;
        if (ud.IsFavorite || (ud.Rating.HasValue && ud.Rating.Value >= 7.0))
            return 100.0;

        var runTicks = item.RunTimeTicks ?? 0;

        if (!ud.Played)
        {
            if (runTicks <= 0 || ud.PlaybackPositionTicks <= 0) return 0.0;
            return Math.Min(99.0, ud.PlaybackPositionTicks * 100.0 / runTicks);
        }

        if (runTicks > 0 && ud.PlaybackPositionTicks > 0)
            return Math.Min(100.0, ud.PlaybackPositionTicks * 100.0 / runTicks);

        return 100.0;
    }

    private static WatchedItemDetail BuildDetail(BaseItem item, double pct, WatchCompletion tag)
    {
        item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbStr);
        _ = int.TryParse(tmdbStr, out var tmdbId);
        return new WatchedItemDetail
        {
            Title             = item.Name ?? string.Empty,
            Year              = item.ProductionYear,
            TmdbId            = tmdbId > 0 ? tmdbId : null,
            IsSeries          = item is MediaBrowser.Controller.Entities.TV.Series,
            CompletionPercent = pct,
            Tag               = tag
        };
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

public enum WatchCompletion { Loved, Watched, Abandoned }

public sealed class WatchedItemDetail
{
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public int? TmdbId { get; init; }
    public bool IsSeries { get; init; }
    public double CompletionPercent { get; init; }
    public WatchCompletion Tag { get; init; }
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

    public IReadOnlyList<LibrarySearchResult> SearchByTitle(User user, string query, int limit = 10)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        });

        return items
            .Where(i => i.Name != null
                && i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(i => ToSearchResult(i))
            .ToList();
    }

    public IReadOnlyList<LibrarySearchResult> GetRecentlyAdded(User user, string type, int limit = 15)
    {
        var kinds = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? [BaseItemKind.Movie]
            : type.Equals("tv", StringComparison.OrdinalIgnoreCase)
                ? [BaseItemKind.Series]
                : (BaseItemKind[])[BaseItemKind.Movie, BaseItemKind.Series];

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = limit,
            Recursive = true,
            IsVirtualItem = false
        })
        .Select(i => ToSearchResult(i))
        .ToList();
    }

    public IReadOnlyList<LibrarySearchResult> GetAiRecommendations(User user, string? type = null)
    {
        var config = Plugin.Instance?.Configuration;
        var reg = config?.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
        if (reg is null) return [];

        var moviePath = reg.MoviePath;
        var showPath  = reg.ShowPath;
        if (string.IsNullOrEmpty(moviePath) && string.IsNullOrEmpty(showPath)) return [];

        var kinds = type?.Equals("movie", StringComparison.OrdinalIgnoreCase) == true
            ? (BaseItemKind[])[BaseItemKind.Movie]
            : type is "tv" or "series"
                ? (BaseItemKind[])[BaseItemKind.Series]
                : (BaseItemKind[])[BaseItemKind.Movie, BaseItemKind.Series];

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            Recursive        = true,
            IsVirtualItem    = false
        })
        .Where(item =>
            (!string.IsNullOrEmpty(moviePath) && item.Path?.StartsWith(moviePath, StringComparison.OrdinalIgnoreCase) == true)
            || (!string.IsNullOrEmpty(showPath)  && item.Path?.StartsWith(showPath,  StringComparison.OrdinalIgnoreCase) == true))
        .Select(i => ToSearchResult(i))
        .ToList();
    }

    private static LibrarySearchResult ToSearchResult(BaseItem i)
    {
        i.TryGetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, out var tmdbIdStr);
        _ = int.TryParse(tmdbIdStr, out var tmdbId);
        return new LibrarySearchResult
        {
            Title     = i.Name ?? string.Empty,
            Year      = i.ProductionYear,
            Type      = i is MediaBrowser.Controller.Entities.TV.Series ? "tv" : "movie",
            TmdbId    = tmdbId > 0 ? tmdbId : null,
            Overview  = i.Overview,
            DateAdded = i.DateCreated == default ? null : i.DateCreated
        };
    }
}

public sealed class LibrarySearchResult
{
    public string    Title     { get; init; } = string.Empty;
    public int?      Year      { get; init; }
    public string    Type      { get; init; } = "movie";
    public int?      TmdbId    { get; init; }
    public string?   Overview  { get; init; }
    public DateTime? DateAdded { get; init; }
}
