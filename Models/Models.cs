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
/// LLM recommendation before TMDB resolution.
/// </summary>
public class LlmRecommendationItem
{
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string Type { get; set; } = "movie";

    public string Reason { get; set; } = string.Empty;

    public bool IsSeries =>
        Type.Equals("series", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("tv", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("show", StringComparison.OrdinalIgnoreCase);
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
