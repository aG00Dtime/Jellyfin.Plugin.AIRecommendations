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

    // Minimal episode NFO with lockdata=true and no TMDB ID.
    // Without a TMDB provider ID the episode's UserDataKey is path-based, so a fresh
    // stub always starts unplayed regardless of the user's prior watch history.
    private static readonly string EpisodeNfo =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <season>1</season>
          <episode>1</episode>
          <lockdata>true</lockdata>
        </episodedetails>
        """;

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

        // Upgrade any legacy show stubs that lack the protective episode NFO, and
        // ensure stubs written by v1.0.45 (tvshow.nfo only) also get an episode stub
        // so they surface in Jellyfin's "Recently Added" section.
        EnsureShowEpisodeStubs(showsPath);

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
        Directory.CreateDirectory(showFolder);

        File.WriteAllText(Path.Combine(showFolder, "tvshow.nfo"), BuildShowNfo(show), Encoding.UTF8);

        // Write a Season 01/S01E01 stub so the show surfaces in "Recently Added".
        // The companion NFO sets lockdata=true with no TMDB ID so Jellyfin cannot match
        // this episode to a real TMDB entry — the UserDataKey is path-based, meaning
        // fresh stubs always start unplayed regardless of the user's watch history.
        var seasonFolder = Path.Combine(showFolder, "Season 01");
        Directory.CreateDirectory(seasonFolder);
        WriteEpisodeStub(seasonFolder, show.Title);
    }

    private static void WriteEpisodeStub(string seasonFolder, string showTitle)
    {
        var episodeName = $"{Sanitize(showTitle)} - S01E01";
        var strmPath = Path.Combine(seasonFolder, $"{episodeName}.strm");
        var nfoPath = Path.Combine(seasonFolder, $"{episodeName}.nfo");

        if (!File.Exists(strmPath))
        {
            File.WriteAllText(strmPath, JustWatchUrl(showTitle), Encoding.UTF8);
        }

        if (!File.Exists(nfoPath))
        {
            File.WriteAllText(nfoPath, EpisodeNfo, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Ensures every show stub folder on disk has a Season 01/S01E01 pair that
    /// includes the protective episode NFO (lockdata=true, no TMDB ID).
    /// Handles two migration scenarios:
    ///   - v1.0.45 stubs (tvshow.nfo only, no season) → adds Season 01 so the show
    ///     appears in Jellyfin's "Recently Added" section.
    ///   - Pre-v1.0.45 stubs (Season folder with .strm but no .nfo) → deletes and
    ///     recreates the season so Jellyfin's database entry loses its old TMDB episode
    ///     ID on the next library scan, clearing inherited played state.
    /// </summary>
    private static void EnsureShowEpisodeStubs(string showsPath)
    {
        if (!Directory.Exists(showsPath))
        {
            return;
        }

        foreach (var showDir in Directory.GetDirectories(showsPath))
        {
            var showFolderName = Path.GetFileName(showDir);
            var seasonDirs = Directory.GetDirectories(showDir);

            if (seasonDirs.Length == 0)
            {
                // v1.0.45 stub: only tvshow.nfo, no episode → add Season 01
                var seasonFolder = Path.Combine(showDir, "Season 01");
                Directory.CreateDirectory(seasonFolder);
                var tagIndex = showFolderName.IndexOf(" [tmdbid-", StringComparison.Ordinal);
                var title = tagIndex > 0 ? showFolderName.Substring(0, tagIndex) : showFolderName;
                WriteEpisodeStub(seasonFolder, title);
                continue;
            }

            // Legacy stub: season exists with .strm but no companion .nfo → delete and
            // recreate so the new scan doesn't inherit the old TMDB episode UserDataKey.
            var hasLegacyStrm = seasonDirs
                .SelectMany(d => Directory.GetFiles(d, "*.strm"))
                .Any(strm => !File.Exists(Path.ChangeExtension(strm, ".nfo")));

            if (!hasLegacyStrm)
            {
                continue;
            }

            foreach (var seasonDir in seasonDirs)
            {
                Directory.Delete(seasonDir, recursive: true);
            }

            var newSeasonFolder = Path.Combine(showDir, "Season 01");
            Directory.CreateDirectory(newSeasonFolder);
            var tagIdx = showFolderName.IndexOf(" [tmdbid-", StringComparison.Ordinal);
            var cleanTitle = tagIdx > 0 ? showFolderName.Substring(0, tagIdx) : showFolderName;
            WriteEpisodeStub(newSeasonFolder, cleanTitle);
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
