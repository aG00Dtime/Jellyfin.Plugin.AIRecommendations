using System.Globalization;
using System.Reflection;
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

    private const string PlaceholderExtension = ".mp4";

    private static readonly Regex TmdbIdPattern = new(@"\[tmdbid-(\d+)\]", RegexOptions.Compiled);

    // Minimal episode NFO with lockdata=true and no TMDB ID.
    // Without a TMDB provider ID the episode's UserDataKey is path-based, so a fresh
    // stub always starts unplayed regardless of the user's prior watch history.
    //
    // Episode 0, not 1: the show's tvshow.nfo carries a real TMDB ID so Jellyfin
    // fetches the actual season/episode list from TMDB and shows it as real (but
    // unplayable — Path is null) virtual episodes, including its own "Episode 1".
    // Our stub can't be matched into that slot without a real TMDB episode ID (which
    // would reintroduce inherited watched-state), so instead it's given episode 0 —
    // sorts before the real episode list and reads clearly as "start here" rather
    // than blending in as an odd extra episode 1.
    private static readonly string EpisodeNfo =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <title>▶ AI Recommendation — tap to request or dismiss</title>
          <season>1</season>
          <episode>0</episode>
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

        // Replace any leftover .strm stub (pre-v1.0.119, whether it held a JustWatch
        // URL or a local path to the placeholder clip) with a real copy of the
        // placeholder video. Jellyfin treats .strm as a remote-stream pointer, not a
        // normal local video file, even when its content is just a local path —
        // that's still enough to make Play fail. A real video file avoids the whole
        // .strm code path.
        MigrateStrmStubs(moviesPath);
        MigrateStrmStubs(showsPath);

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

    /// <summary>
    /// Replaces any leftover .strm stub with a real copy of the placeholder video,
    /// same base filename so the existing companion .nfo still matches it.
    /// </summary>
    private static void MigrateStrmStubs(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var placeholderPath = GetPlaceholderPath();
        foreach (var strmPath in Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories))
        {
            var videoPath = Path.ChangeExtension(strmPath, PlaceholderExtension);
            if (!File.Exists(videoPath))
            {
                File.Copy(placeholderPath, videoPath);
            }

            File.Delete(strmPath);
        }
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

        var videoPath = Path.Combine(folder, $"{folderName}{PlaceholderExtension}");
        if (!File.Exists(videoPath))
        {
            File.Copy(GetPlaceholderPath(), videoPath);
        }

        var nfoPath = Path.Combine(folder, $"{folderName}.nfo");
        File.WriteAllText(nfoPath, BuildMovieNfo(movie), Encoding.UTF8);
    }

    private static void WriteShow(string showsPath, ResolvedRecommendation show, bool limitToSeasonOne)
    {
        var folderName = GetShowFolderName(show);
        var showFolder = Path.Combine(showsPath, folderName);
        Directory.CreateDirectory(showFolder);

        File.WriteAllText(Path.Combine(showFolder, "tvshow.nfo"), BuildShowNfo(show), Encoding.UTF8);

        // Write a Season 01/S01E00 stub so the show surfaces in "Recently Added".
        // The companion NFO sets lockdata=true with no TMDB ID so Jellyfin cannot match
        // this episode to a real TMDB entry — the UserDataKey is path-based, meaning
        // fresh stubs always start unplayed regardless of the user's watch history.
        var seasonFolder = Path.Combine(showFolder, "Season 01");
        Directory.CreateDirectory(seasonFolder);
        WriteEpisodeStub(seasonFolder, show.Title);
    }

    private static void WriteEpisodeStub(string seasonFolder, string showTitle)
    {
        var episodeName = $"{Sanitize(showTitle)} - S01E00";
        var videoPath = Path.Combine(seasonFolder, $"{episodeName}{PlaceholderExtension}");
        var nfoPath = Path.Combine(seasonFolder, $"{episodeName}.nfo");

        if (!File.Exists(videoPath))
        {
            File.Copy(GetPlaceholderPath(), videoPath);
        }

        if (!File.Exists(nfoPath))
        {
            File.WriteAllText(nfoPath, EpisodeNfo, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Ensures every show stub folder on disk has a Season 01/S01E00 pair that
    /// includes the protective episode NFO (lockdata=true, no TMDB ID).
    /// Handles three migration scenarios:
    ///   - v1.0.45 stubs (tvshow.nfo only, no season) → adds Season 01 so the show
    ///     appears in Jellyfin's "Recently Added" section.
    ///   - Pre-v1.0.45 stubs (Season folder with .strm but no .nfo) → deletes and
    ///     recreates the season so Jellyfin's database entry loses its old TMDB episode
    ///     ID on the next library scan, clearing inherited played state.
    ///   - Pre-v1.0.120 stubs (episode numbered S01E01) → replaced with S01E00, since
    ///     TMDB-matched shows get their own real (unplayable) "Episode 1" from Jellyfin's
    ///     season data; numbering ours 1 too meant it collided/blended in with that
    ///     instead of clearly sorting first as "start here".
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

            foreach (var seasonDir in seasonDirs)
            {
                var oldEpisodeFiles = Directory.GetFiles(seasonDir, "* - S01E01.*");
                if (oldEpisodeFiles.Length == 0)
                {
                    continue;
                }

                foreach (var f in oldEpisodeFiles)
                {
                    File.Delete(f);
                }

                var tagI = showFolderName.IndexOf(" [tmdbid-", StringComparison.Ordinal);
                var showTitle = tagI > 0 ? showFolderName.Substring(0, tagI) : showFolderName;
                WriteEpisodeStub(seasonDir, showTitle);
            }

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

    /// <summary>
    /// Path to a short local clip explaining that this item is an AI recommendation,
    /// not real content — extracted once from the embedded resource into the plugin's
    /// data folder so it survives version upgrades (unlike the versioned plugin install
    /// path). Every stub gets its own copy of this file (not a .strm pointer — Jellyfin
    /// treats .strm as a remote-stream pointer even when its content is a local path,
    /// which was enough to make Play fail), so a mistaken tap on Play is instant and
    /// harmless instead of erroring.
    /// </summary>
    private static string GetPlaceholderPath()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var path = Path.Combine(plugin.DataFolderPath, "placeholder.mp4");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(plugin.DataFolderPath);
            using var resourceStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Jellyfin.Plugin.AIRecommendations.Assets.placeholder.mp4")
                ?? throw new InvalidOperationException("Embedded placeholder.mp4 resource not found");
            using var fileStream = File.Create(path);
            resourceStream.CopyTo(fileStream);
        }

        return path;
    }

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
