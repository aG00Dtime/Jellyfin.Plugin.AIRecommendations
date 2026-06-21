namespace Jellyfin.Plugin.AIRecommendations.Models;

/// <summary>
/// A resolved recommendation ready to write to a virtual library.
/// </summary>
public class ResolvedRecommendation
{
    public int TmdbId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public bool IsSeries { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }
}

/// <summary>
/// Item in the user's watch history used for taste profiling.
/// </summary>
public class WatchedItemSummary
{
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? TmdbId { get; set; }

    public bool IsSeries { get; set; }

    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    public string? Overview { get; set; }

    public double? UserRating { get; set; }
}

/// <summary>
/// Distilled taste profile derived from watch history — sent to the LLM instead of a raw item list.
/// </summary>
public class UserTasteProfile
{
    public int TotalWatched { get; set; }

    /// <summary>Genre name → frequency count, ordered by count descending.</summary>
    public List<(string Genre, int Count)> TopGenres { get; set; } = new();

    /// <summary>Human-readable era preference, e.g. "mostly modern (2000s–2020s)".</summary>
    public string EraPreference { get; set; } = "varied";

    /// <summary>Percentage of watched content that is movies (vs series).</summary>
    public int MoviePercent { get; set; } = 50;

    /// <summary>Representative sample of titles spread across watch history.</summary>
    public List<string> SampleTitles { get; set; } = new();

    /// <summary>Titles the user has marked as favourite.</summary>
    public List<string> FavoriteTitles { get; set; } = new();
}

/// <summary>
/// LLM recommendation before TMDB resolution.
/// When TmdbId is set the item came from the RAG catalog and needs no TMDB search.
/// </summary>
public class LlmRecommendationItem
{
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string Type { get; set; } = "movie";

    public string Reason { get; set; } = string.Empty;

    /// <summary>Set when the LLM picked from a TMDB Discover catalog; skips search.</summary>
    public int? TmdbId { get; set; }

    public bool IsSeries =>
        Type.Equals("series", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("tv", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("show", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A candidate from TMDB Discover — pre-fetched before the LLM call so the LLM
/// picks from real, current data rather than from training-data memory.
/// </summary>
public class TmdbCandidate
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public bool IsSeries { get; set; }
    public string? Overview { get; set; }
}

/// <summary>
/// TMDB movie release availability — whether a movie is out digitally, in theaters only, or not yet released.
/// </summary>
public sealed record MovieAvailability
{
    public MovieReleaseStatus Status { get; init; }
    public DateTime? UpcomingDigitalDate { get; init; }

    public static readonly MovieAvailability Unknown      = new() { Status = MovieReleaseStatus.Unknown };
    public static readonly MovieAvailability Digital      = new() { Status = MovieReleaseStatus.Digital };
    public static readonly MovieAvailability NotReleased  = new() { Status = MovieReleaseStatus.NotReleased };
    public static MovieAvailability TheatersOnly => new() { Status = MovieReleaseStatus.TheatersOnly };
    public static MovieAvailability Upcoming     => new() { Status = MovieReleaseStatus.Upcoming };
}

public enum MovieReleaseStatus
{
    Unknown,
    NotReleased,
    Upcoming,
    TheatersOnly,
    Digital
}

/// <summary>
/// Plugin status DTO.
/// </summary>
public class PluginStatusDto
{
    public string ActiveProvider { get; set; } = string.Empty;

    public DateTime? LastSyncUtc { get; set; }

    public string LastSyncMessage { get; set; } = string.Empty;

    public int RegisteredUsers { get; set; }
}
