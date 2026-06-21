using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Generates and stores a per-user LLM taste profile based on deep watch history analysis.
/// </summary>
public class TasteProfileService
{
    private readonly WatchHistoryService _watchHistory;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TasteProfileService> _logger;

    private const string ClientName = "TasteProfile";

    public TasteProfileService(
        WatchHistoryService watchHistory,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<TasteProfileService> logger)
    {
        _watchHistory    = watchHistory;
        _libraryManager  = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger          = logger;
    }

    /// <summary>
    /// Generates and stores a taste profile if one doesn't exist or is stale.
    /// Returns true if a new profile was generated.
    /// </summary>
    public async Task<bool> RefreshIfNeededAsync(
        User user,
        Configuration.UserLibraryRegistration reg,
        Configuration.PluginConfiguration config,
        CancellationToken ct)
    {
        var intervalDays = config.TasteProfileRegenerationIntervalDays;
        var hasProfile   = !string.IsNullOrWhiteSpace(reg.TasteProfileText);
        var ageOk        = reg.TasteProfileGeneratedAt.HasValue
            && intervalDays > 0
            && (DateTime.UtcNow - reg.TasteProfileGeneratedAt.Value).TotalDays < intervalDays;

        if (hasProfile && ageOk)
            return false;

        _logger.LogInformation("TasteProfileService: generating profile for {User} (exists={Has}, interval={Days}d)",
            user.Username, hasProfile, intervalDays);

        try
        {
            var profile = await GenerateAsync(user, config, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                reg.TasteProfileText        = profile;
                reg.TasteProfileGeneratedAt = DateTime.UtcNow;
                _logger.LogInformation("TasteProfileService: profile stored for {User} ({Chars} chars)", user.Username, profile.Length);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TasteProfileService: failed to generate profile for {User}", user.Username);
        }

        return false;
    }

    /// <summary>
    /// Force-regenerates the taste profile regardless of age.
    /// </summary>
    public async Task ForceRefreshAsync(
        User user,
        Configuration.UserLibraryRegistration reg,
        Configuration.PluginConfiguration config,
        CancellationToken ct)
    {
        reg.TasteProfileGeneratedAt = null; // clear age so RefreshIfNeeded triggers
        await RefreshIfNeededAsync(user, reg, config, ct).ConfigureAwait(false);
    }

    private async Task<string> GenerateAsync(
        User user,
        Configuration.PluginConfiguration config,
        CancellationToken ct)
    {
        var data = BuildHistoryData(user);

        if (data.TotalWatched == 0)
        {
            _logger.LogInformation("TasteProfileService: {User} has no watch history, skipping", user.Username);
            return string.Empty;
        }

        var prompt = BuildPrompt(data);

        var payload = new
        {
            model = GetModel(config),
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0.7
        };

        var (baseUrl, apiKey) = GetCredentials(config);
        var client = _httpClientFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim() ?? string.Empty;
    }

    private RichHistoryData BuildHistoryData(User user)
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 200,
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 200,
            Recursive = true,
            IsPlayed = true,
            EnableGroupByMetadataKey = true
        });

        var favorites = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Limit = 20,
            Recursive = true,
            IsFavoriteOrLiked = true,
            EnableGroupByMetadataKey = true
        });

        var allWatched = movies.Concat(series).DistinctBy(i => i.Id).ToList();

        // AI stub paths to exclude from analysis
        var aiPaths = Plugin.Instance?.Configuration.UserLibraries
            .SelectMany(r => new[] { r.MoviePath, r.ShowPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        allWatched = allWatched
            .Where(i => i.Path is null || !aiPaths.Any(p => i.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var recentMovies  = movies.Take(15).Select(FormatTitle).Where(t => t.Length > 0).ToList();
        var recentSeries  = series.Take(15).Select(FormatTitle).Where(t => t.Length > 0).ToList();

        // Spread sample across full history
        var step   = Math.Max(1, allWatched.Count / 40);
        var spread = allWatched
            .Where((_, i) => i % step == 0)
            .Take(40)
            .Select(FormatTitle)
            .Where(t => t.Length > 0)
            .ToList();

        var genres = allWatched
            .SelectMany(i => i.Genres ?? Array.Empty<string>())
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        var years = allWatched.Where(i => i.ProductionYear.HasValue).Select(i => i.ProductionYear!.Value).ToList();
        var eraBreakdown = years.Count == 0 ? "unknown" : BuildEraBreakdown(years);

        return new RichHistoryData
        {
            TotalWatched  = allWatched.Count,
            MovieCount    = movies.Count,
            SeriesCount   = series.Count,
            RecentMovies  = recentMovies,
            RecentSeries  = recentSeries,
            SpreadSample  = spread,
            GenreCounts   = genres,
            EraBreakdown  = eraBreakdown,
            Favorites     = favorites.Select(FormatTitle).Where(t => t.Length > 0).Take(20).ToList()
        };
    }

    private static string FormatTitle(BaseItem item)
    {
        var name = item.Name ?? string.Empty;
        return item.ProductionYear.HasValue ? $"{name} ({item.ProductionYear})" : name;
    }

    private static string BuildEraBreakdown(List<int> years)
    {
        var pre2000  = years.Count(y => y < 2000);
        var s2000s   = years.Count(y => y is >= 2000 and < 2010);
        var s2010s   = years.Count(y => y is >= 2010 and < 2020);
        var s2020s   = years.Count(y => y >= 2020);
        var total    = years.Count;
        return $"pre-2000: {pre2000 * 100 / total}%, 2000s: {s2000s * 100 / total}%, 2010s: {s2010s * 100 / total}%, 2020s: {s2020s * 100 / total}%";
    }

    private static string BuildPrompt(RichHistoryData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyse this Jellyfin user's viewing history and write a concise taste profile.");
        sb.AppendLine();
        sb.AppendLine($"Total watched: {d.TotalWatched} items ({d.MovieCount} movies, {d.SeriesCount} shows)");
        sb.AppendLine($"Era distribution: {d.EraBreakdown}");
        sb.AppendLine();
        sb.AppendLine($"Top genres: {string.Join(", ", d.GenreCounts)}");
        sb.AppendLine();
        if (d.Favorites.Count > 0)
            sb.AppendLine($"Favourited titles: {string.Join(", ", d.Favorites)}");
        sb.AppendLine($"Recently watched movies: {string.Join(", ", d.RecentMovies)}");
        sb.AppendLine($"Recently watched shows: {string.Join(", ", d.RecentSeries)}");
        sb.AppendLine($"Broader history sample: {string.Join(", ", d.SpreadSample)}");
        sb.AppendLine();
        sb.AppendLine("Write a taste profile in 2-3 short paragraphs (third person, \"This user...\"). Cover:");
        sb.AppendLine("1. Genre and theme preferences with specific patterns from the titles");
        sb.AppendLine("2. Preferred tone/mood/style (e.g. dark vs light, procedural vs serialised, fast-paced vs slow-burn)");
        sb.AppendLine("3. What to recommend more of and what to clearly avoid");
        sb.AppendLine("Be specific and cite titles as evidence. Keep it under 250 words.");
        return sb.ToString();
    }

    private static string GetModel(Configuration.PluginConfiguration config) =>
        config.ActiveProvider switch
        {
            var p when p.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) => config.OpenRouterModel,
            var p when p.Equals("Ollama",     StringComparison.OrdinalIgnoreCase) => config.OllamaModel,
            _ => config.OpenAiModel
        };

    private static (string BaseUrl, string ApiKey) GetCredentials(Configuration.PluginConfiguration config) =>
        config.ActiveProvider switch
        {
            var p when p.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) =>
                (string.IsNullOrWhiteSpace(config.OpenRouterBaseUrl) ? "https://openrouter.ai/api/v1" : config.OpenRouterBaseUrl.TrimEnd('/'),
                 config.OpenRouterApiKey),
            var p when p.Equals("Ollama", StringComparison.OrdinalIgnoreCase) =>
                ($"{(string.IsNullOrWhiteSpace(config.OllamaBaseUrl) ? "http://localhost:11434" : config.OllamaBaseUrl.TrimEnd('/'))}/v1",
                 config.OllamaApiKey),
            _ =>
                (string.IsNullOrWhiteSpace(config.OpenAiBaseUrl) ? "https://api.openai.com/v1" : config.OpenAiBaseUrl.TrimEnd('/'),
                 config.OpenAiApiKey)
        };

    private sealed class RichHistoryData
    {
        public int TotalWatched  { get; init; }
        public int MovieCount    { get; init; }
        public int SeriesCount   { get; init; }
        public List<string> RecentMovies { get; init; } = [];
        public List<string> RecentSeries { get; init; } = [];
        public List<string> SpreadSample { get; init; } = [];
        public List<string> GenreCounts  { get; init; } = [];
        public string EraBreakdown       { get; init; } = string.Empty;
        public List<string> Favorites    { get; init; } = [];
    }
}
