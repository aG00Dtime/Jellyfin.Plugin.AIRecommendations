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
        // Only write the series NFO — no episode stub.
        // A Season 1 / S01E01.strm file causes Jellyfin to assign the real TMDB episode
        // ID to the stub episode, which means Jellyfin inherits whatever played-state is
        // stored under that episode key. If the user previously tried to dismiss this show
        // (marking the episode as watched), the stub would immediately re-appear as watched
        // on the next sync. Without an episode stub, all episodes are virtual and start
        // as unplayed. Users dismiss by marking the series itself as watched, which fires
        // a Series-level TogglePlayed event that our handler catches correctly.
        var folderName = GetShowFolderName(show);
        var showFolder = Path.Combine(showsPath, folderName);
        Directory.CreateDirectory(showFolder);

        var nfoPath = Path.Combine(showFolder, "tvshow.nfo");
        File.WriteAllText(nfoPath, BuildShowNfo(show), Encoding.UTF8);
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
              <tagline>AI Pick — ❤️ to request via Jellyseerr · ✅ Mark watched to dismiss forever</tagline>
              <plot>{X(plot)}</plot>
              <tag>AI Recommendation</tag>
              <dateadded>2000-01-01 00:00:00</dateadded>
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
              <tagline>AI Pick — ❤️ to request via Jellyseerr · ✅ Mark series as watched to dismiss</tagline>
              <plot>{X(plot)}</plot>
              <tag>AI Recommendation</tag>
              <dateadded>2000-01-01 00:00:00</dateadded>
              <lockdata>false</lockdata>
              <lockedfields>Tagline|Overview</lockedfields>
            </tvshow>
            """;
    }

    private static string BuildPlot(ResolvedRecommendation item)
    {
        var reason = string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : $"💡 {item.Reason}";
        var overview = string.IsNullOrWhiteSpace(item.Overview) ? string.Empty : item.Overview;
        const string hint = "AI Pick — ❤️ to request via Jellyseerr · ✅ Mark watched to dismiss forever";
        var body = string.IsNullOrWhiteSpace(overview) ? reason : $"{reason}\n\n{overview}";
        return string.IsNullOrWhiteSpace(body) ? hint : $"{body}\n\n{hint}";
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
