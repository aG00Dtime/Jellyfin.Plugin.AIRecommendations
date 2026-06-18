using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AIRecommendations.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Writes TMDB-identified stub files for Jellyfin library scanning.
/// Stubs accumulate up to MaxStubsPerType; only stubs whose TMDB ID is in
/// removeByTmdbId are deleted. Returns the full set of TMDB IDs now on disk.
/// </summary>
public class VirtualItemWriter
{
    private const int MaxStubsPerType = 50;

    private static readonly Regex TmdbIdPattern = new(@"\[tmdbid-(\d+)\]", RegexOptions.Compiled);

    private readonly ILogger<VirtualItemWriter> _logger;

    public VirtualItemWriter(ILogger<VirtualItemWriter> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<int> SyncRecommendations(
        string moviesPath,
        string showsPath,
        IReadOnlyList<ResolvedRecommendation> newRecommendations,
        HashSet<int> removeByTmdbId,
        bool limitShowsToSeasonOne)
    {
        Directory.CreateDirectory(moviesPath);
        Directory.CreateDirectory(showsPath);

        // Remove stubs for rejected / requested / owned items
        RemoveStaleStubs(moviesPath, removeByTmdbId);
        RemoveStaleStubs(showsPath, removeByTmdbId);

        // Count what remains
        var existingMovieIds = ScanTmdbIds(moviesPath);
        var existingShowIds = ScanTmdbIds(showsPath);

        var movies = newRecommendations.Where(r => !r.IsSeries).ToList();
        var shows = newRecommendations.Where(r => r.IsSeries).ToList();

        // Add new stubs only up to the per-type cap
        var moviesSlots = Math.Max(0, MaxStubsPerType - existingMovieIds.Count);
        var showsSlots = Math.Max(0, MaxStubsPerType - existingShowIds.Count);

        var moviesToAdd = movies.Where(m => !existingMovieIds.Contains(m.TmdbId)).Take(moviesSlots).ToList();
        var showsToAdd = shows.Where(s => !existingShowIds.Contains(s.TmdbId)).Take(showsSlots).ToList();

        foreach (var movie in moviesToAdd)
        {
            WriteMovie(moviesPath, movie);
        }

        foreach (var show in showsToAdd)
        {
            WriteShow(showsPath, show, limitShowsToSeasonOne);
        }

        _logger.LogInformation(
            "Added {MovieCount} movies and {ShowCount} shows to virtual libraries (totals: {TotalMovies}/{Cap} movies, {TotalShows}/{Cap} shows)",
            moviesToAdd.Count, showsToAdd.Count,
            existingMovieIds.Count + moviesToAdd.Count, MaxStubsPerType,
            existingShowIds.Count + showsToAdd.Count, MaxStubsPerType);

        // Return everything on disk so the sync service can track placed IDs
        var placed = new List<int>();
        placed.AddRange(ScanTmdbIds(moviesPath));
        placed.AddRange(ScanTmdbIds(showsPath));
        return placed;
    }

    /// <summary>
    /// Scans a directory and returns the TMDB IDs of all stub folders found.
    /// </summary>
    public static HashSet<int> ScanTmdbIds(string path)
    {
        if (!Directory.Exists(path))
        {
            return new HashSet<int>();
        }

        var ids = new HashSet<int>();
        foreach (var dir in Directory.GetDirectories(path))
        {
            var id = ParseTmdbId(Path.GetFileName(dir));
            if (id.HasValue)
            {
                ids.Add(id.Value);
            }
        }

        return ids;
    }

    public static int? ParseTmdbId(string folderName)
    {
        var m = TmdbIdPattern.Match(folderName);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private static void RemoveStaleStubs(string root, HashSet<int> removeByTmdbId)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var id = ParseTmdbId(Path.GetFileName(dir));
            if (id.HasValue && removeByTmdbId.Contains(id.Value))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void WriteMovie(string moviesPath, ResolvedRecommendation movie)
    {
        var folderName = GetMovieFolderName(movie);
        var folder = Path.Combine(moviesPath, folderName);
        Directory.CreateDirectory(folder);

        var strmPath = Path.Combine(folder, $"{folderName}.strm");
        File.WriteAllText(strmPath, JustWatchUrl(movie.Title), Encoding.UTF8);

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
              <tagline>AI Pick — ❤️ to request via Jellyseerr · 🗑️ Delete to dismiss forever</tagline>
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
              <tagline>AI Pick — ❤️ to request via Jellyseerr · 🗑️ Delete to dismiss forever</tagline>
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

    private static string X(string? value)
        => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

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
