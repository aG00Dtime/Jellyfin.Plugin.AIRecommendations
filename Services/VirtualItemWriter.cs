using System.Globalization;
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

        // Filename matches folder name so Jellyfin's metadata matcher picks up title+year+tmdbid
        var strmPath = Path.Combine(folder, $"{folderName}.strm");
        File.WriteAllText(strmPath, "https://example.invalid/ai-recommendations/not-playable", Encoding.UTF8);

        var reasonPath = Path.Combine(folder, ".ai-reason.txt");
        File.WriteAllText(reasonPath, movie.Reason, Encoding.UTF8);
    }

    private static void WriteShow(string showsPath, ResolvedRecommendation show, bool limitToSeasonOne)
    {
        var folderName = GetShowFolderName(show);
        var showFolder = Path.Combine(showsPath, folderName);
        var seasonFolder = Path.Combine(showFolder, "Season 1");
        Directory.CreateDirectory(seasonFolder);

        var episodeName = $"{Sanitize(show.Title)} - S01E01 [tmdbid-{show.TmdbId.ToString(CultureInfo.InvariantCulture)}].strm";
        var strmPath = Path.Combine(seasonFolder, episodeName);
        File.WriteAllText(strmPath, "https://example.invalid/ai-recommendations/not-playable", Encoding.UTF8);

        var reasonPath = Path.Combine(showFolder, ".ai-reason.txt");
        File.WriteAllText(reasonPath, show.Reason, Encoding.UTF8);

        if (!limitToSeasonOne)
        {
            // v1 only writes season 1 stub
        }
    }

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
