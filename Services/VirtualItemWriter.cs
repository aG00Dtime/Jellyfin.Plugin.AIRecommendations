using System.Globalization;
using System.Security;
using System.Text;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Writes TMDB-identified stub files for Jellyfin library scanning.
/// </summary>
public class VirtualItemWriter
{
    private readonly ILogger<VirtualItemWriter> _logger;

    public VirtualItemWriter(ILogger<VirtualItemWriter> logger)
    {
        _logger = logger;
    }

    public void SyncRecommendations(
        string moviesPath,
        string showsPath,
        IReadOnlyList<ResolvedRecommendation> recommendations,
        bool limitShowsToSeasonOne)
    {
        Directory.CreateDirectory(moviesPath);
        Directory.CreateDirectory(showsPath);

        var movies = recommendations.Where(r => !r.IsSeries).ToList();
        var shows = recommendations.Where(r => r.IsSeries).ToList();

        CleanDirectory(moviesPath, movies.Select(m => GetMovieFolderName(m)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        CleanDirectory(showsPath, shows.Select(s => GetShowFolderName(s)).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (var movie in movies)
        {
            WriteMovie(moviesPath, movie);
        }

        foreach (var show in shows)
        {
            WriteShow(showsPath, show, limitShowsToSeasonOne);
        }

        _logger.LogInformation("Wrote {MovieCount} movies and {ShowCount} shows to virtual libraries", movies.Count, shows.Count);
    }

    private static void WriteMovie(string moviesPath, ResolvedRecommendation movie)
    {
        var folderName = GetMovieFolderName(movie);
        var folder = Path.Combine(moviesPath, folderName);
        Directory.CreateDirectory(folder);

        // STRM → JustWatch so clicking Play gives the user somewhere to go
        var strmPath = Path.Combine(folder, $"{folderName}.strm");
        File.WriteAllText(strmPath, JustWatchUrl(movie.Title), Encoding.UTF8);

        // NFO sidecar: locks tagline + tag so Jellyfin shows "AI Recommendation"
        // even after TMDB refreshes the poster and other metadata.
        var nfoPath = Path.Combine(folder, $"{folderName}.nfo");
        File.WriteAllText(nfoPath, BuildMovieNfo(movie), Encoding.UTF8);
    }

    private static void WriteShow(string showsPath, ResolvedRecommendation show, bool limitToSeasonOne)
    {
        var folderName = GetShowFolderName(show);
        var showFolder = Path.Combine(showsPath, folderName);
        var seasonFolder = Path.Combine(showFolder, "Season 1");
        Directory.CreateDirectory(seasonFolder);

        var episodeName = $"{Sanitize(show.Title)} - S01E01 [tmdbid-{show.TmdbId.ToString(CultureInfo.InvariantCulture)}].strm";
        var strmPath = Path.Combine(seasonFolder, episodeName);
        File.WriteAllText(strmPath, JustWatchUrl(show.Title), Encoding.UTF8);

        // tvshow.nfo in show root — Jellyfin picks this up for series metadata
        var nfoPath = Path.Combine(showFolder, "tvshow.nfo");
        File.WriteAllText(nfoPath, BuildShowNfo(show), Encoding.UTF8);

        if (!limitToSeasonOne)
        {
            // v1 only writes season 1 stub
        }
    }

    private static string JustWatchUrl(string title)
        => $"https://www.justwatch.com/us/search?q={Uri.EscapeDataString(title)}";

    private static string BuildMovieNfo(ResolvedRecommendation movie)
    {
        var tmdbId = movie.TmdbId.ToString(CultureInfo.InvariantCulture);
        var plot = BuildPlot(movie);
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <movie>
              <title>{X(movie.Title)}</title>
              <year>{movie.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}</year>
              <uniqueid type="tmdb" default="true">{tmdbId}</uniqueid>
              <tagline>AI Recommendation · Find it on JustWatch</tagline>
              <plot>{X(plot)}</plot>
              <tag>AI Recommendation</tag>
              <lockdata>false</lockdata>
              <lockedfields>Tagline|Overview</lockedfields>
            </movie>
            """;
    }

    private static string BuildShowNfo(ResolvedRecommendation show)
    {
        var tmdbId = show.TmdbId.ToString(CultureInfo.InvariantCulture);
        var plot = BuildPlot(show);
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <tvshow>
              <title>{X(show.Title)}</title>
              <year>{show.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}</year>
              <uniqueid type="tmdb" default="true">{tmdbId}</uniqueid>
              <tagline>AI Recommendation · Find it on JustWatch</tagline>
              <plot>{X(plot)}</plot>
              <tag>AI Recommendation</tag>
              <lockdata>false</lockdata>
              <lockedfields>Tagline|Overview</lockedfields>
            </tvshow>
            """;
    }

    private static string BuildPlot(ResolvedRecommendation item)
    {
        var reason = string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : $"💡 {item.Reason}";
        var overview = string.IsNullOrWhiteSpace(item.Overview) ? string.Empty : item.Overview;
        return string.IsNullOrWhiteSpace(overview) ? reason : $"{reason}\n\n{overview}";
    }

    // XML-safe encoding for NFO text content
    private static string X(string? value)
        => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private static void CleanDirectory(string root, HashSet<string> desiredFolderNames)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!desiredFolderNames.Contains(name))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string GetMovieFolderName(ResolvedRecommendation movie)
    {
        var year = movie.Year?.ToString(CultureInfo.InvariantCulture) ?? "????";
        return $"{Sanitize(movie.Title)} ({year}) [tmdbid-{movie.TmdbId.ToString(CultureInfo.InvariantCulture)}]";
    }

    private static string GetShowFolderName(ResolvedRecommendation show)
        => $"{Sanitize(show.Title)} [tmdbid-{show.TmdbId.ToString(CultureInfo.InvariantCulture)}]";

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '-');
        }

        return name.Trim();
    }
}
