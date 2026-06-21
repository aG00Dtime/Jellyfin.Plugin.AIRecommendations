using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Telegram;

/// <summary>
/// Lightweight agentic harness. Runs a tool-calling while-loop against the active
/// OpenAI-compatible LLM provider, dispatching tools to existing plugin services.
/// </summary>
public sealed class TelegramAgentLoop
{
    private const int MaxToolRounds = 5;
    private const string AgentClientName = "TelegramAgent";

    private readonly TmdbMetadataService _tmdb;
    private readonly JellyseerrService _jellyseerr;
    private readonly ArrRequestService _arr;
    private readonly RecommendationEngine _engine;
    private readonly WatchHistoryService _watchHistory;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramAgentLoop> _logger;

    public TelegramAgentLoop(
        TmdbMetadataService tmdb,
        JellyseerrService jellyseerr,
        ArrRequestService arr,
        RecommendationEngine engine,
        WatchHistoryService watchHistory,
        IUserManager userManager,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramAgentLoop> logger)
    {
        _tmdb = tmdb;
        _jellyseerr = jellyseerr;
        _arr = arr;
        _engine = engine;
        _watchHistory = watchHistory;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Processes one user turn. Appends the user message to <paramref name="history"/>,
    /// runs the tool-calling loop, appends all assistant/tool messages, and returns the
    /// final text reply.
    /// </summary>
    public async Task<string> RunAsync(
        string jellyfinUserId,
        string userText,
        List<ConversationMessage> history,
        CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return "Plugin not initialized.";

        if (!Guid.TryParse(jellyfinUserId, out var userId))
            return "Could not identify your Jellyfin account.";

        var user = _userManager.GetUserById(userId);
        if (user is null) return "Your Jellyfin user account was not found.";

        var systemPrompt = BuildSystemPrompt(user, config);

        // Append the user turn
        history.Add(new ConversationMessage("user", userText));

        var toolDefs = GetToolDefinitions();

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var messages = BuildMessages(systemPrompt, history);
            var payload = new
            {
                model = GetActiveModel(config),
                messages,
                tools = toolDefs,
                tool_choice = "auto",
                temperature = 0.5
            };

            string responseJson;
            try
            {
                responseJson = await CallLlmAsync(config, payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramAgentLoop: LLM call failed on round {Round}", round + 1);
                return "I had trouble reaching the AI provider. Please try again shortly.";
            }

            using var doc = JsonDocument.Parse(responseJson);
            var choice = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            if (choice.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var toolCallsJson = toolCalls.GetRawText();
                var assistantContent = choice.TryGetProperty("content", out var ac) ? ac.GetString() : null;
                history.Add(new ConversationMessage("assistant", assistantContent, ToolCallsJson: toolCallsJson));

                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var tcId = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var tcName = tc.TryGetProperty("function", out var fn)
                        && fn.TryGetProperty("name", out var fnName)
                        ? fnName.GetString() ?? "" : "";
                    var tcArgs = tc.TryGetProperty("function", out var fn2)
                        && fn2.TryGetProperty("arguments", out var fnArgs)
                        ? fnArgs.GetString() ?? "{}" : "{}";

                    _logger.LogDebug("TelegramAgentLoop: tool {Tool}({Args})", tcName, tcArgs);

                    string toolResult;
                    try
                    {
                        toolResult = await ExecuteToolAsync(tcName, tcArgs, jellyfinUserId, user, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TelegramAgentLoop: tool {Tool} threw", tcName);
                        toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                    }

                    history.Add(new ConversationMessage("tool", toolResult, ToolCallId: tcId, ToolName: tcName));
                }

                continue;
            }

            var content = choice.TryGetProperty("content", out var contentEl)
                ? contentEl.GetString()?.Trim()
                : null;

            if (!string.IsNullOrWhiteSpace(content))
            {
                history.Add(new ConversationMessage("assistant", content));
                return content;
            }

            break;
        }

        return "I wasn't able to generate a response. Please try rephrasing your request.";
    }

    // ── Tool implementations ──────────────────────────────────────────────────

    private async Task<string> ExecuteToolAsync(
        string toolName,
        string argsJson,
        string jellyfinUserId,
        User user,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var args = doc.RootElement;

        return toolName switch
        {
            "search_content"     => await SearchContentAsync(args, ct).ConfigureAwait(false),
            "get_recommendations"=> await GetRecommendationsAsync(args, jellyfinUserId, user, ct).ConfigureAwait(false),
            "request_media"      => await RequestMediaAsync(args, jellyfinUserId, ct).ConfigureAwait(false),
            "check_status"       => await CheckStatusAsync(args, ct).ConfigureAwait(false),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };
    }

    private async Task<string> SearchContentAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var type  = args.TryGetProperty("type",  out var t) ? t.GetString() ?? "movie" : "movie";
        var year  = args.TryGetProperty("year",  out var y) && y.TryGetInt32(out var yr) ? (int?)yr : null;

        var item = new LlmRecommendationItem { Title = query, Year = year, Type = type };
        var result = await _tmdb.ResolveAsync(item, ct).ConfigureAwait(false);

        if (result is null)
            return JsonSerializer.Serialize(new { found = false, message = $"No TMDB result for '{query}'" });

        return JsonSerializer.Serialize(new
        {
            found = true,
            tmdb_id = result.TmdbId,
            title = result.Title,
            year = result.Year,
            type = result.IsSeries ? "tv" : "movie",
            overview = result.Overview
        });
    }

    private async Task<string> GetRecommendationsAsync(
        JsonElement args,
        string jellyfinUserId,
        User user,
        CancellationToken ct)
    {
        var count = args.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 10) : 5;

        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == jellyfinUserId);
        var excludeIds = reg is not null
            ? new HashSet<int>(reg.RejectedTmdbIds.Concat(reg.RequestedTmdbIds))
            : new HashSet<int>();

        var recs = await _engine.GenerateForUserAsync(user, excludeIds, ct).ConfigureAwait(false);
        var top = recs.Take(count).Select(r => new
        {
            tmdb_id  = r.TmdbId,
            title    = r.Title,
            year     = r.Year,
            type     = r.IsSeries ? "tv" : "movie",
            reason   = r.Reason,
            overview = r.Overview
        }).ToList();

        return JsonSerializer.Serialize(new { count = top.Count, recommendations = top });
    }

    private async Task<string> RequestMediaAsync(
        JsonElement args,
        string jellyfinUserId,
        CancellationToken ct)
    {
        if (!args.TryGetProperty("tmdb_id", out var tidEl) || !tidEl.TryGetInt32(out var tmdbId))
            return JsonSerializer.Serialize(new { success = false, error = "tmdb_id is required" });

        var title    = args.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
        var type     = args.TryGetProperty("type",  out var ty) ? ty.GetString() ?? "movie" : "movie";
        var isSeries = type == "tv" || type == "series";

        // Track in RequestedTmdbIds so it's excluded from future stub recommendations
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == jellyfinUserId);
        if (reg is not null && !reg.RequestedTmdbIds.Contains(tmdbId))
        {
            reg.RequestedTmdbIds.Add(tmdbId);
            Plugin.Instance!.SaveConfiguration();
        }

        var results = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            try
            {
                var rec = new ResolvedRecommendation { TmdbId = tmdbId, Title = title, IsSeries = isSeries };
                var submitted = await _jellyseerr.RequestRecommendationsAsync([rec], ct).ConfigureAwait(false);
                results.Add(submitted > 0 ? "submitted to Jellyseerr" : "already in Jellyseerr");
            }
            catch (Exception ex)
            {
                results.Add($"Jellyseerr error: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl) && !isSeries)
        {
            try
            {
                var ok = await _arr.RequestMovieAsync(tmdbId, title, ct).ConfigureAwait(false);
                results.Add(ok ? "submitted to Radarr" : "already in Radarr");
            }
            catch (Exception ex) { results.Add($"Radarr error: {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl) && isSeries)
        {
            try
            {
                var ok = await _arr.RequestShowAsync(tmdbId, title, ct).ConfigureAwait(false);
                results.Add(ok ? "submitted to Sonarr" : "already in Sonarr");
            }
            catch (Exception ex) { results.Add($"Sonarr error: {ex.Message}"); }
        }

        if (results.Count == 0)
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "No download services configured. Add Jellyseerr, Radarr, or Sonarr in plugin settings."
            });

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = string.Join("; ", results),
            title,
            tmdb_id = tmdbId
        });
    }

    private async Task<string> CheckStatusAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("tmdb_id", out var tidEl) || !tidEl.TryGetInt32(out var tmdbId))
            return JsonSerializer.Serialize(new { error = "tmdb_id is required" });

        var type     = args.TryGetProperty("type", out var t) ? t.GetString() ?? "movie" : "movie";
        var isSeries = type == "tv" || type == "series";
        var config   = Plugin.Instance!.Configuration;
        var statuses = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            var (code, name, _) = await _arr.CheckJellyseerrStatusAsync(tmdbId, isSeries, ct).ConfigureAwait(false);
            statuses.Add($"Jellyseerr: {name}");
        }

        if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl) && !isSeries)
        {
            var (exists, hasFile, _) = await _arr.CheckRadarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            statuses.Add(exists ? (hasFile ? "Radarr: downloaded" : "Radarr: monitored, not yet downloaded") : "Radarr: not added");
        }

        if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl) && isSeries)
        {
            var (exists, pct, _) = await _arr.CheckSonarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            statuses.Add(exists ? $"Sonarr: {pct}% downloaded" : "Sonarr: not added");
        }

        if (statuses.Count == 0)
            return JsonSerializer.Serialize(new { status = "unknown", message = "No download services configured" });

        return JsonSerializer.Serialize(new { tmdb_id = tmdbId, statuses });
    }

    // ── LLM plumbing ─────────────────────────────────────────────────────────

    private string BuildSystemPrompt(User user, Configuration.PluginConfiguration config)
    {
        var profile = _watchHistory.BuildTasteProfile(user, Math.Min(config.MaxWatchedItems, 30));
        var genres  = profile.TopGenres.Count > 0 ? string.Join(", ", profile.TopGenres.Select(g => g.Genre)) : "varied";
        var favs    = profile.FavoriteTitles.Count > 0 ? string.Join(", ", profile.FavoriteTitles) : "none recorded";
        var samples = profile.SampleTitles.Count > 0 ? string.Join(", ", profile.SampleTitles) : "none recorded";

        var services = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)) services.Add("Jellyseerr");
        if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl)) services.Add("Radarr");
        if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl)) services.Add("Sonarr");
        var serviceList = services.Count > 0 ? string.Join(", ", services) : "none configured";

        return $"""
You are a friendly media assistant for a personal Jellyfin home server. Help the user discover and request movies and TV shows.

USER TASTE PROFILE:
- Top genres: {genres}
- Era preference: {profile.EraPreference}
- Content mix: {profile.MoviePercent}% movies, {100 - profile.MoviePercent}% shows
- Enjoyed: {samples}
- Favourites: {favs}

DOWNLOAD SERVICES: {serviceList}

TOOLS: search_content, get_recommendations, request_media, check_status

RULES:
1. Always call search_content before request_media to get the real TMDB ID — never guess it.
2. Confirm what you're about to request before calling request_media, unless the user already said "yes", "sure", or "request it".
3. Be concise. Use <b>bold</b> for titles (Telegram HTML). No markdown asterisks.
4. If no download services are configured, say so when the user tries to request something.
""";
    }

    private static List<Dictionary<string, object?>> BuildMessages(
        string systemPrompt,
        IReadOnlyList<ConversationMessage> history)
    {
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt }
        };

        foreach (var msg in history)
        {
            var d = new Dictionary<string, object?> { ["role"] = msg.Role };

            if (msg.Role == "tool")
            {
                d["tool_call_id"] = msg.ToolCallId ?? string.Empty;
                d["name"]         = msg.ToolName   ?? string.Empty;
                d["content"]      = msg.Content    ?? string.Empty;
            }
            else if (msg.Role == "assistant" && msg.ToolCallsJson is not null)
            {
                d["content"] = msg.Content; // may be null — that is valid per spec
                using var tc = JsonDocument.Parse(msg.ToolCallsJson);
                d["tool_calls"] = tc.RootElement.Clone();
            }
            else
            {
                d["content"] = msg.Content ?? string.Empty;
            }

            messages.Add(d);
        }

        return messages;
    }

    private static string GetActiveModel(Configuration.PluginConfiguration config) =>
        config.ActiveProvider switch
        {
            var p when p.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) => config.OpenRouterModel,
            var p when p.Equals("Ollama",     StringComparison.OrdinalIgnoreCase) => config.OllamaModel,
            _ => config.OpenAiModel
        };

    private static (string BaseUrl, string ApiKey) GetProviderCredentials(Configuration.PluginConfiguration config) =>
        config.ActiveProvider switch
        {
            var p when p.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) =>
                (string.IsNullOrWhiteSpace(config.OpenRouterBaseUrl) ? "https://openrouter.ai/api/v1" : config.OpenRouterBaseUrl.TrimEnd('/'),
                 config.OpenRouterApiKey),
            // Ollama exposes an OpenAI-compatible endpoint at {host}/v1
            var p when p.Equals("Ollama", StringComparison.OrdinalIgnoreCase) =>
                ($"{(string.IsNullOrWhiteSpace(config.OllamaBaseUrl) ? "http://localhost:11434" : config.OllamaBaseUrl.TrimEnd('/'))}/v1",
                 config.OllamaApiKey),
            _ =>
                (string.IsNullOrWhiteSpace(config.OpenAiBaseUrl) ? "https://api.openai.com/v1" : config.OpenAiBaseUrl.TrimEnd('/'),
                 config.OpenAiApiKey)
        };

    private async Task<string> CallLlmAsync(
        Configuration.PluginConfiguration config,
        object payload,
        CancellationToken ct)
    {
        var (baseUrl, apiKey) = GetProviderCredentials(config);
        var client = _httpClientFactory.CreateClient(AgentClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static object[] GetToolDefinitions() =>
    [
        new
        {
            type = "function",
            function = new
            {
                name = "search_content",
                description = "Search TMDB for a movie or TV show by title to verify it exists and get its TMDB ID. Always call this before request_media.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "Title to search for" },
                        ["type"]  = new { type = "string", @enum = new[] { "movie", "tv" }, description = "Movie or TV show" },
                        ["year"]  = new { type = "integer", description = "Optional release year to narrow results" }
                    },
                    required = new[] { "query", "type" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_recommendations",
                description = "Generate personalised movie and TV show recommendations based on the user's Jellyfin watch history.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["count"] = new { type = "integer", description = "Number of recommendations to return (1-10)" }
                    },
                    required = new[] { "count" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "request_media",
                description = "Submit a download request for a movie or TV show via Jellyseerr, Radarr, or Sonarr. Always call search_content first to confirm the TMDB ID.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["tmdb_id"] = new { type = "integer", description = "TMDB ID from search_content result" },
                        ["title"]   = new { type = "string",  description = "Human-readable title for confirmation messages" },
                        ["type"]    = new { type = "string",  @enum = new[] { "movie", "tv" } }
                    },
                    required = new[] { "tmdb_id", "title", "type" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "check_status",
                description = "Check the download or availability status of a movie or TV show.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["tmdb_id"] = new { type = "integer", description = "TMDB ID of the title" },
                        ["type"]    = new { type = "string",  @enum = new[] { "movie", "tv" } }
                    },
                    required = new[] { "tmdb_id", "type" }
                }
            }
        }
    ];

}
