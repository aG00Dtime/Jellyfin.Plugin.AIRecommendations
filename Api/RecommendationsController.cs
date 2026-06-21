using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Services;
using Jellyfin.Plugin.AIRecommendations.Telegram;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AIRecommendations.Api;

/// <summary>
/// Admin API for AI recommendations sync and status.
/// </summary>
[ApiController]
[Route("AIRecommendations")]
[Authorize(Policy = "RequiresElevation")]
[ApiExplorerSettings(IgnoreApi = true)]
public class RecommendationsController : ControllerBase
{
    private readonly RecommendationSyncService _syncService;
    private readonly TasteProfileService _tasteProfile;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramBotService _telegramBot;
    private readonly TelegramAgentLoop _telegramAgent;

    public RecommendationsController(
        RecommendationSyncService syncService,
        TasteProfileService tasteProfile,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        TelegramBotService telegramBot,
        TelegramAgentLoop telegramAgent)
    {
        _syncService = syncService;
        _tasteProfile = tasteProfile;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _telegramBot = telegramBot;
        _telegramAgent = telegramAgent;
    }

    /// <summary>
    /// Gets plugin sync status.
    /// </summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginStatusDto> GetStatus()
        => Ok(_syncService.GetStatus());

    /// <summary>
    /// Triggers a full sync for all users (admin only).
    /// </summary>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SyncAll(CancellationToken cancellationToken)
    {
        await _syncService.SyncAllUsersAsync(null, cancellationToken, force: true).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Triggers sync for a single user.
    /// </summary>
    [HttpPost("Sync/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncUser([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        await _syncService.SyncUserAsync(user, cancellationToken, force: true).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Force-regenerates the LLM taste profile for all users.
    /// </summary>
    [HttpPost("TasteProfile/Refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RefreshAllTasteProfiles(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        foreach (var user in _userManager.GetUsers())
        {
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
            if (reg is null) continue;
            await _tasteProfile.ForceRefreshAsync(user, reg, config, cancellationToken).ConfigureAwait(false);
        }
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Force-regenerates the LLM taste profile for a single user.
    /// </summary>
    [HttpPost("TasteProfile/{userId:guid}/Refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefreshTasteProfile([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null) return NotFound();
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == userId.ToString("N"));
        if (reg is null) return NotFound();
        await _tasteProfile.ForceRefreshAsync(user, reg, config, cancellationToken).ConfigureAwait(false);
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Clears recommendation stubs and persisted state for all users or a specific user,
    /// then triggers a library scan so Jellyfin removes the deleted items from its database.
    /// </summary>
    [HttpPost("Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearAll(CancellationToken cancellationToken)
    {
        ClearUserLibraries(null);
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Clear/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearUser([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null) return NotFound();
        ClearUserLibraries(userId);
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    private static void ClearUserLibraries(Guid? userId)
    {
        var config = Plugin.Instance!.Configuration;
        var targets = userId.HasValue
            ? config.UserLibraries.Where(r => r.UserId == userId.Value.ToString("N")).ToList()
            : config.UserLibraries.ToList();

        foreach (var reg in targets)
        {
            if (Directory.Exists(reg.MoviePath))
            {
                Directory.Delete(reg.MoviePath, recursive: true);
            }

            if (Directory.Exists(reg.ShowPath))
            {
                Directory.Delete(reg.ShowPath, recursive: true);
            }

            reg.RequestedTmdbIds.Clear();
            reg.RejectedTmdbIds.Clear();
            reg.PlacedTmdbIds.Clear();
        }

        Plugin.Instance!.SaveConfiguration();
    }

    /// <summary>
    /// Returns registered users and their library state for the admin UI.
    /// </summary>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetUsers()
    {
        var config = Plugin.Instance!.Configuration;
        var result = config.UserLibraries.Select(r =>
        {
            var user = _userManager.GetUserById(Guid.Parse(r.UserId));
            return new
            {
                UserId = r.UserId,
                Username = user?.Username ?? r.UserId,
                r.MovieLibraryName,
                r.ShowLibraryName,
                StubCount = r.PlacedTmdbIds.Count,
                RequestedCount = r.RequestedTmdbIds.Count,
                RejectedCount = r.RejectedTmdbIds.Count
            };
        });
        return Ok(result);
    }

    /// <summary>
    /// Returns the TMDB IDs and titles of stubs currently on disk for a user.
    /// </summary>
    [HttpGet("Recommendations/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<object>> GetRecommendations([FromRoute] Guid userId)
    {
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == userId.ToString("N"));
        if (reg is null) return NotFound();

        var result = new List<object>();
        result.AddRange(ScanStubFolder(reg.MoviePath, "movie"));
        result.AddRange(ScanStubFolder(reg.ShowPath, "series"));
        return Ok(result);
    }

    private static IEnumerable<object> ScanStubFolder(string path, string type)
    {
        if (!Directory.Exists(path)) yield break;
        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = System.IO.Path.GetFileName(dir);
            var tmdbId = Services.VirtualItemWriter.ParseTmdbId(name);
            if (tmdbId is null) continue;
            // Strip tmdbid suffix and year for display
            var title = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d{4}\)\s*\[tmdbid-\d+\]|\s*\[tmdbid-\d+\]", "").Trim();
            yield return new { tmdbId, title, type };
        }
    }

    /// <summary>
    /// Permanently dismisses a recommendation for a user (adds to reject list and removes stub).
    /// </summary>
    [HttpPost("Dismiss/{userId:guid}/{tmdbId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DismissRecommendation([FromRoute] Guid userId, [FromRoute] int tmdbId)
    {
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == userId.ToString("N"));
        if (reg is null) return NotFound();

        if (!reg.RejectedTmdbIds.Contains(tmdbId))
        {
            reg.RejectedTmdbIds.Add(tmdbId);
        }

        // Remove matching stub folder immediately
        DeleteStubFolder(reg.MoviePath, tmdbId);
        DeleteStubFolder(reg.ShowPath, tmdbId);

        // Remove from placed tracking
        reg.PlacedTmdbIds.Remove(tmdbId);

        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    private static void DeleteStubFolder(string path, int tmdbId)
    {
        if (!Directory.Exists(path)) return;
        foreach (var dir in Directory.GetDirectories(path))
        {
            var id = Services.VirtualItemWriter.ParseTmdbId(System.IO.Path.GetFileName(dir));
            if (id == tmdbId)
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
        }
    }

    // ── Telegram agent test ────────────────────────────────────────────────────

    /// <summary>Sends a message to the agent and returns its reply. Maintains per-user session state.</summary>
    [HttpPost("Telegram/TestAgent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestAgent(
        [FromBody] TestAgentRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.JellyfinUserId, out var userId)
            || _userManager.GetUserById(userId) is null)
            return NotFound(new { error = "Jellyfin user not found" });

        var reply = await _telegramAgent
            .TestAsync(request.JellyfinUserId, request.Message, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new { reply });
    }

    /// <summary>Clears the test session for a user so the next message starts fresh.</summary>
    [HttpDelete("Telegram/TestAgent/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ResetTestAgent([FromRoute] string userId)
    {
        _telegramAgent.ResetTestSession(userId);
        return NoContent();
    }

    // ── Telegram link management ───────────────────────────────────────────────

    /// <summary>
    /// Links a Telegram chat to a Jellyfin user via a one-time code generated
    /// by the bot's /link command.
    /// </summary>
    [HttpPost("Telegram/Link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult LinkTelegramAccount([FromBody] TelegramLinkRequest request)
    {
        var pending = _telegramBot.ConsumePendingCode(request.Code?.Trim() ?? string.Empty);
        if (pending is null)
            return BadRequest(new { error = "Invalid or expired code. Have the user send /link to the bot again." });

        var config = Plugin.Instance!.Configuration;
        config.TelegramUserLinks.RemoveAll(l => l.ChatId == pending.ChatId
                                              || l.JellyfinUserId == request.JellyfinUserId);
        config.TelegramUserLinks.Add(new TelegramUserLink
        {
            ChatId          = pending.ChatId,
            JellyfinUserId  = request.JellyfinUserId,
            TelegramUsername= pending.Username,
            LinkedAt        = DateTime.UtcNow
        });
        Plugin.Instance!.SaveConfiguration();

        // Confirm in Telegram
        _ = _telegramBot.SendMessageAsync(
            pending.ChatId,
            "✅ Linked to Jellyfin! Ask me what to watch.",
            CancellationToken.None);

        return Ok(new { message = $"Linked chat {pending.ChatId} to Jellyfin user {request.JellyfinUserId}" });
    }

    /// <summary>Returns all linked Telegram accounts (for the admin UI).</summary>
    [HttpGet("Telegram/Links")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetTelegramLinks()
    {
        var config = Plugin.Instance!.Configuration;
        return Ok(config.TelegramUserLinks.Select(l =>
        {
            var user = _userManager.GetUserById(Guid.TryParse(l.JellyfinUserId, out var uid) ? uid : Guid.Empty);
            return new
            {
                l.ChatId,
                l.JellyfinUserId,
                Username         = user?.Username ?? l.JellyfinUserId,
                l.TelegramUsername,
                l.LinkedAt,
                NotifiedCount    = l.NotifiedAvailableTmdbIds.Count
            };
        }));
    }

    /// <summary>Unlinks a Telegram account by chat ID.</summary>
    [HttpDelete("Telegram/Links/{chatId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult UnlinkTelegramAccount([FromRoute] long chatId)
    {
        var config = Plugin.Instance!.Configuration;
        config.TelegramUserLinks.RemoveAll(l => l.ChatId == chatId);
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    // ── Arr profile helpers ────────────────────────────────────────────────────

    /// <summary>Returns available quality profiles from Radarr or Sonarr (for the config UI dropdowns).</summary>
    [HttpGet("ArrProfiles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<object>>> GetArrProfiles(
        [FromQuery] string service,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        string baseUrl, apiKey;

        if (service.Equals("Radarr", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = config.RadarrBaseUrl.TrimEnd('/');
            apiKey  = config.RadarrApiKey;
        }
        else if (service.Equals("Sonarr", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = config.SonarrBaseUrl.TrimEnd('/');
            apiKey  = config.SonarrApiKey;
        }
        else
        {
            return BadRequest(new { error = "service must be Radarr or Sonarr" });
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { error = $"{service} base URL is not configured" });

        try
        {
            var client = _httpClientFactory.CreateClient("ProviderTest");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/qualityprofile");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var profiles = doc.RootElement.EnumerateArray().Select(p => new
            {
                id   = p.GetProperty("id").GetInt32(),
                name = p.TryGetProperty("name", out var n) ? n.GetString() : null
            }).ToList();
            return Ok(profiles);
        }
        catch (Exception ex)
        {
            return Ok(new[] { new { id = 0, name = $"Error: {ex.Message}" } });
        }
    }

    /// <summary>Returns root folders from Radarr or Sonarr.</summary>
    [HttpGet("ArrRootFolders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<object>>> GetArrRootFolders(
        [FromQuery] string service,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        string baseUrl, apiKey;

        if (service.Equals("Radarr", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = config.RadarrBaseUrl.TrimEnd('/');
            apiKey  = config.RadarrApiKey;
        }
        else if (service.Equals("Sonarr", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = config.SonarrBaseUrl.TrimEnd('/');
            apiKey  = config.SonarrApiKey;
        }
        else
        {
            return BadRequest(new { error = "service must be Radarr or Sonarr" });
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { error = $"{service} base URL is not configured" });

        try
        {
            var client = _httpClientFactory.CreateClient("ProviderTest");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/rootfolder");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var folders = doc.RootElement.EnumerateArray().Select(f => new
            {
                path = f.TryGetProperty("path", out var p) ? p.GetString() : null
            }).ToList();
            return Ok(folders);
        }
        catch (Exception ex)
        {
            return Ok(new[] { new { path = $"Error: {ex.Message}" } });
        }
    }

    /// <summary>
    /// Tests connectivity for a given provider using supplied credentials.
    /// </summary>
    [HttpPost("TestProvider")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestProviderResult>> TestProvider(
        [FromBody] TestProviderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await RunProviderTestAsync(request, cancellationToken).ConfigureAwait(false);
            return Ok(new TestProviderResult { Success = true, Message = message });
        }
        catch (Exception ex)
        {
            return Ok(new TestProviderResult { Success = false, Message = ex.Message });
        }
    }

    private async Task<string> RunProviderTestAsync(TestProviderRequest req, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ProviderTest");

        switch (req.Provider)
        {
            case "Jellyseerr":
            {
                var baseUrl = req.BaseUrl.TrimEnd('/');
                using var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/auth/me");
                pingReq.Headers.Add("X-Api-Key", req.ApiKey);
                using var pingResp = await client.SendAsync(pingReq, cancellationToken).ConfigureAwait(false);
                pingResp.EnsureSuccessStatusCode();
                var body = await pingResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var displayName = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                return $"Connected to Jellyseerr{(displayName is not null ? $" as {displayName}" : string.Empty)}";
            }

            case "Ollama":
            {
                var isCloud = req.OllamaDeployment.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? (isCloud ? "https://ollama.com" : "http://localhost:11434")
                    : req.BaseUrl.TrimEnd('/');

                using var tagsReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/tags");
                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                {
                    tagsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
                }

                using var tagsResp = await client.SendAsync(tagsReq, cancellationToken).ConfigureAwait(false);
                tagsResp.EnsureSuccessStatusCode();
                return $"Connected to Ollama at {baseUrl}";
            }

            case "OpenRouter":
            {
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? "https://openrouter.ai/api/v1"
                    : req.BaseUrl.TrimEnd('/');
                return await TestOpenAiCompatibleAsync(client, baseUrl, req.ApiKey, req.Model, cancellationToken)
                    .ConfigureAwait(false);
            }

            case "Telegram":
            {
                var token = req.ApiKey;
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Bot token is required.");
                using var tgReq = new HttpRequestMessage(
                    HttpMethod.Get, $"https://api.telegram.org/bot{token}/getMe");
                using var tgResp = await client.SendAsync(tgReq, cancellationToken).ConfigureAwait(false);
                tgResp.EnsureSuccessStatusCode();
                var body = await tgResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var botUsername = doc.RootElement.TryGetProperty("result", out var r)
                    && r.TryGetProperty("username", out var un)
                    ? un.GetString() : "bot";
                return $"Connected as @{botUsername}";
            }

            case "Radarr":
            {
                var baseUrl = req.BaseUrl.TrimEnd('/');
                using var arrReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/system/status");
                arrReq.Headers.Add("X-Api-Key", req.ApiKey);
                using var arrResp = await client.SendAsync(arrReq, cancellationToken).ConfigureAwait(false);
                arrResp.EnsureSuccessStatusCode();
                var body = await arrResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "?";
                return $"Connected to Radarr v{version}";
            }

            case "Sonarr":
            {
                var baseUrl = req.BaseUrl.TrimEnd('/');
                using var arrReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/system/status");
                arrReq.Headers.Add("X-Api-Key", req.ApiKey);
                using var arrResp = await client.SendAsync(arrReq, cancellationToken).ConfigureAwait(false);
                arrResp.EnsureSuccessStatusCode();
                var body = await arrResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "?";
                return $"Connected to Sonarr v{version}";
            }

            default: // OpenAI
            {
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? "https://api.openai.com/v1"
                    : req.BaseUrl.TrimEnd('/');
                return await TestOpenAiCompatibleAsync(client, baseUrl, req.ApiKey, req.Model, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> TestOpenAiCompatibleAsync(
        HttpClient client,
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = "Hi" } },
            max_tokens = 1
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return $"Connected successfully (model: {model})";
    }
}

public class TestProviderRequest
{
    public string Provider { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string OllamaDeployment { get; set; } = "Local";
}

public class TestProviderResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TelegramLinkRequest
{
    public string Code { get; set; } = string.Empty;
    public string JellyfinUserId { get; set; } = string.Empty;
}

public class TestAgentRequest
{
    public string JellyfinUserId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
