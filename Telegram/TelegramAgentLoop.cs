using System.Collections.Concurrent;
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
    private const int MaxHistoryMessages = 20;
    private const string AgentClientName = "TelegramAgent";

    private readonly TmdbMetadataService _tmdb;
    private readonly JellyseerrService _jellyseerr;
    private readonly ArrRequestService _arr;
    private readonly RecommendationSyncService _syncService;
    private readonly WatchHistoryService _watchHistory;
    private readonly LibraryFilterService _libraryFilter;
    private readonly TasteProfileService _tasteProfile;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramAgentLoop> _logger;

    // Per-user test sessions (used by the admin test endpoint, not Telegram)
    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _testSessions = new();

    public TelegramAgentLoop(
        TmdbMetadataService tmdb,
        JellyseerrService jellyseerr,
        ArrRequestService arr,
        RecommendationSyncService syncService,
        WatchHistoryService watchHistory,
        LibraryFilterService libraryFilter,
        TasteProfileService tasteProfile,
        IUserManager userManager,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramAgentLoop> logger)
    {
        _tmdb = tmdb;
        _jellyseerr = jellyseerr;
        _arr = arr;
        _syncService = syncService;
        _watchHistory = watchHistory;
        _libraryFilter = libraryFilter;
        _tasteProfile = tasteProfile;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Runs the agent for the admin test UI, maintaining a stateful per-user session.</summary>
    public async Task<string> TestAsync(string jellyfinUserId, string message, CancellationToken ct)
    {
        var history = _testSessions.GetOrAdd(jellyfinUserId, _ => new List<ConversationMessage>());
        var reply = await RunAsync(jellyfinUserId, message, history, null, ct).ConfigureAwait(false);
        while (history.Count > MaxHistoryMessages) history.RemoveAt(0);
        return reply;
    }

    public void ResetTestSession(string jellyfinUserId) =>
        _testSessions.TryRemove(jellyfinUserId, out _);

    /// <summary>
    /// Processes one user turn. Appends the user message to <paramref name="history"/>,
    /// runs the tool-calling loop, appends all assistant/tool messages, and returns the
    /// final text reply.
    /// </summary>
    /// <param name="onStatus">Optional callback invoked before each tool call with a brief human-readable status.</param>
    public async Task<string> RunAsync(
        string jellyfinUserId,
        string userText,
        List<ConversationMessage> history,
        Func<string, Task>? onStatus,
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
                _logger.LogInformation("TelegramAgentLoop: calling LLM round {Round} for user {UserId}", round + 1, jellyfinUserId);
                responseJson = await CallLlmAsync(config, payload, ct).ConfigureAwait(false);
                _logger.LogInformation("TelegramAgentLoop: LLM round {Round} returned {Bytes} bytes", round + 1, responseJson.Length);
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation — the caller (HandleUpdateAsync) handles the turn timeout
                // and server shutdown cases. Never swallow this or the timeout message won't fire.
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "TelegramAgentLoop: LLM network error on round {Round} (provider={Provider}, status={Status})",
                    round + 1, config.ActiveProvider, ex.StatusCode);
                return "I had trouble reaching the AI provider. Please try again shortly.";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramAgentLoop: LLM call failed on round {Round} (provider={Provider})",
                    round + 1, config.ActiveProvider);
                return "I had trouble reaching the AI provider. Please try again shortly.";
            }

            JsonElement choice;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                choice = doc.RootElement.GetProperty("choices")[0].GetProperty("message").Clone();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramAgentLoop: failed to parse LLM response on round {Round}. Response: {Json}",
                    round + 1, responseJson.Length > 500 ? responseJson[..500] : responseJson);
                return "The AI provider returned an unexpected response. Please try again.";
            }

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

                    _logger.LogInformation("TelegramAgentLoop: tool call {Tool}({Args})", tcName, tcArgs.Length > 200 ? tcArgs[..200] : tcArgs);

                    if (onStatus != null)
                    {
                        var statusMsg = BuildToolStatusMessage(tcName, tcArgs);
                        if (statusMsg != null)
                            try { await onStatus(statusMsg).ConfigureAwait(false); } catch { }
                    }

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
            "get_ai_recommendations"  => GetAiRecommendations(args, user),
            "browse_tmdb"             => await BrowseTmdbAsync(args, user, ct).ConfigureAwait(false),
            "search_content"          => await SearchContentAsync(args, ct).ConfigureAwait(false),
            "search_library"          => SearchLibrary(args, user),
            "get_recently_added"      => GetRecentlyAdded(args, user),
            "discover_content"        => await DiscoverContentAsync(args, user, ct).ConfigureAwait(false),
            "request_media"           => await RequestMediaAsync(args, jellyfinUserId, ct).ConfigureAwait(false),
            "check_status"            => await CheckStatusAsync(args, ct).ConfigureAwait(false),
            "sync_to_jellyfin"        => await SyncToJellyfinAsync(user, ct).ConfigureAwait(false),
            "refresh_taste_profile"   => await RefreshTasteProfileAsync(user, ct).ConfigureAwait(false),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };
    }

    private string GetAiRecommendations(JsonElement args, User user)
    {
        var type = args.TryGetProperty("type", out var t) ? t.GetString() : null;
        var results = _libraryFilter.GetAiRecommendations(user, type);

        if (results.Count == 0)
            return JsonSerializer.Serialize(new
            {
                found   = false,
                message = "No AI recommendations in the library yet — they're generated during the daily sync. Use discover_content as a fallback."
            });

        return JsonSerializer.Serialize(new
        {
            found   = true,
            count   = results.Count,
            source  = "AI recommendation engine (personalised from watch history)",
            results = results.Select(r => new
            {
                title    = r.Title,
                year     = r.Year,
                type     = r.Type,
                tmdb_id  = r.TmdbId,
                overview = r.Overview
            })
        });
    }

    private async Task<string> BrowseTmdbAsync(JsonElement args, User user, CancellationToken ct)
    {
        var category = args.TryGetProperty("category", out var cat) ? cat.GetString() ?? "popular" : "popular";
        var type     = args.TryGetProperty("type",     out var t)   ? t.GetString()   ?? "movie"   : "movie";
        var isMovie  = !type.Equals("tv", StringComparison.OrdinalIgnoreCase)
                    && !type.Equals("series", StringComparison.OrdinalIgnoreCase);
        var page     = args.TryGetProperty("page",  out var pg) && pg.TryGetInt32(out var p) ? Math.Clamp(p, 1, 5) : 1;
        var count    = args.TryGetProperty("count", out var c)  && c.TryGetInt32(out var n)  ? Math.Clamp(n, 5, 20) : 10;

        var config   = Plugin.Instance!.Configuration;
        var reg      = config.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
        var excludeIds = reg is not null
            ? new HashSet<int>(reg.RejectedTmdbIds.Concat(reg.RequestedTmdbIds))
            : [];
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();

        var results = await _tmdb.BrowseTmdbAsync(category, isMovie, page, count * 2, excludeIds, ct)
            .ConfigureAwait(false);

        var filtered = results
            .Where(r => !ownedIds.Contains(r.TmdbId))
            .Take(count)
            .Select(r => new
            {
                tmdb_id  = r.TmdbId,
                title    = r.Title,
                year     = r.Year,
                type     = r.IsSeries ? "tv" : "movie",
                overview = r.Overview
            }).ToList();

        return JsonSerializer.Serialize(new
        {
            count    = filtered.Count,
            category,
            page,
            results  = filtered
        });
    }

    private async Task<string> SearchContentAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var type  = args.TryGetProperty("type",  out var t) ? t.GetString() : null; // null = search both
        var year  = args.TryGetProperty("year",  out var y) && y.TryGetInt32(out var yr) ? (int?)yr : null;

        var results = await _tmdb.SearchMultiAsync(query, type, year, 5, ct).ConfigureAwait(false);

        if (results.Count == 0)
            return JsonSerializer.Serialize(new { found = false, message = $"No TMDB results for '{query}'" });

        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var matches = results.Select(r => new
        {
            tmdb_id    = r.TmdbId,
            title      = r.Title,
            year       = r.Year,
            type       = r.IsSeries ? "tv" : "movie",
            overview   = r.Overview,
            in_library = ownedIds.Contains(r.TmdbId)
        }).ToList();

        return JsonSerializer.Serialize(new { found = true, results = matches });
    }

    private string SearchLibrary(JsonElement args, User user)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { error = "query is required" });

        var results = _libraryFilter.SearchByTitle(user, query);
        if (results.Count == 0)
            return JsonSerializer.Serialize(new { found = false, message = $"Nothing in the library matching '{query}'" });

        return JsonSerializer.Serialize(new
        {
            found = true,
            count = results.Count,
            results = results.Select(r => new
            {
                title   = r.Title,
                year    = r.Year,
                type    = r.Type,
                tmdb_id = r.TmdbId
            })
        });
    }

    private string GetRecentlyAdded(JsonElement args, User user)
    {
        var type  = args.TryGetProperty("type",  out var t) ? t.GetString() ?? "all" : "all";
        var count = args.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 25) : 15;

        var results = _libraryFilter.GetRecentlyAdded(user, type, count);
        if (results.Count == 0)
            return JsonSerializer.Serialize(new { found = false, message = "No items found in the library." });

        return JsonSerializer.Serialize(new
        {
            found = true,
            count = results.Count,
            results = results.Select(r => new
            {
                title      = r.Title,
                year       = r.Year,
                type       = r.Type,
                tmdb_id    = r.TmdbId,
                date_added = r.DateAdded.HasValue
                    ? r.DateAdded.Value.ToString("yyyy-MM-dd")
                    : null
            })
        });
    }

    private async Task<string> DiscoverContentAsync(JsonElement args, User user, CancellationToken ct)
    {
        var type    = args.TryGetProperty("type",  out var t) ? t.GetString() ?? "movie" : "movie";
        var isMovie = !type.Equals("tv", StringComparison.OrdinalIgnoreCase)
                   && !type.Equals("series", StringComparison.OrdinalIgnoreCase);
        var count   = args.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? Math.Clamp(n, 5, 10) : 5;
        var page    = args.TryGetProperty("page",  out var pg) && pg.TryGetInt32(out var pn)
            ? Math.Clamp(pn, 1, 5) : 1;

        // Use genres from args, or fall back to the user's taste profile
        List<string> genres;
        if (args.TryGetProperty("genres", out var genreEl) && genreEl.ValueKind == JsonValueKind.Array)
        {
            genres = genreEl.EnumerateArray()
                .Select(g => g.GetString() ?? string.Empty)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToList();
        }
        else
        {
            var profile = _watchHistory.BuildTasteProfile(user, 30);
            genres = profile.TopGenres.Take(3).Select(g => g.Genre).ToList();
        }

        if (genres.Count == 0)
            genres = isMovie ? ["Drama", "Action"] : ["Drama", "Comedy"];

        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();

        // Pre-exclude rejected/requested IDs at fetch time (these are rare).
        // Owned IDs are post-filtered so TMDB's page-1 results aren't exhausted
        // before we collect enough candidates.
        var excludeForFetch = reg is not null
            ? new HashSet<int>(reg.RejectedTmdbIds.Concat(reg.RequestedTmdbIds))
            : [];

        // Fetch 4× count per genre so there's room to absorb owned-item filtering
        var fetchLimit = Math.Max(count * 4, 20);
        var results = await _tmdb.DiscoverAsync(genres, isMovie, excludeForFetch, fetchLimit, ct, page).ConfigureAwait(false);
        var top = results
            .Where(r => !ownedIds.Contains(r.TmdbId))
            .Take(count)
            .Select(r => new
            {
                tmdb_id  = r.TmdbId,
                title    = r.Title,
                year     = r.Year,
                type     = r.IsSeries ? "tv" : "movie",
                overview = r.Overview
            }).ToList();

        return JsonSerializer.Serialize(new { count = top.Count, genres_used = genres, results = top });
    }

    private async Task<string> SyncToJellyfinAsync(User user, CancellationToken ct)
    {
        try
        {
            await _syncService.SyncUserAsync(user, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Jellyfin AI library updated — new recommendation stubs are now available."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TelegramAgentLoop: sync_to_jellyfin failed for {User}", user.Username);
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private async Task<string> RefreshTasteProfileAsync(User user, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
        if (reg is null)
            return JsonSerializer.Serialize(new { success = false, error = "No library registration found for this user." });

        try
        {
            await _tasteProfile.ForceRefreshAsync(user, reg, config, ct).ConfigureAwait(false);
            Plugin.Instance!.SaveConfiguration();

            var preview = reg.TasteProfileText is { Length: > 0 }
                ? reg.TasteProfileText[..Math.Min(200, reg.TasteProfileText.Length)] + "..."
                : "(empty)";

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "Taste profile regenerated.",
                profile_preview = preview
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TelegramAgentLoop: refresh_taste_profile failed for {User}", user.Username);
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
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

        _logger.LogInformation("RequestMedia: tmdb={TmdbId} title={Title} type={Type} jellyseerr={Jelly} radarr={Radarr} sonarr={Sonarr}",
            tmdbId, title, type,
            !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.JellyseerrBaseUrl),
            !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.RadarrBaseUrl),
            !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.SonarrBaseUrl));

        // Track in RequestedTmdbIds so it's excluded from future stub recommendations
        var config = Plugin.Instance!.Configuration;
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == jellyfinUserId);
        if (reg is not null && !reg.RequestedTmdbIds.Contains(tmdbId))
        {
            reg.RequestedTmdbIds.Add(tmdbId);
            Plugin.Instance!.SaveConfiguration();
        }

        // Check movie release availability — warn if not yet out digitally so the user knows
        // a download may not be possible until it leaves theaters or gets a digital release.
        string? availabilityWarning = null;
        if (!isSeries)
        {
            var avail = await _tmdb.GetMovieAvailabilityAsync(tmdbId, ct).ConfigureAwait(false);
            availabilityWarning = avail.Status switch
            {
                MovieReleaseStatus.TheatersOnly =>
                    avail.UpcomingDigitalDate.HasValue
                        ? $"This movie is currently in theaters only. Digital release expected around {avail.UpcomingDigitalDate:MMMM yyyy}. It can be queued but may not download until then."
                        : "This movie is currently in theaters only and has no confirmed digital release date yet. It can be queued but may not download for some time.",
                MovieReleaseStatus.NotReleased =>
                    "This movie hasn't been released yet. It can be queued but won't be available to download until it releases.",
                MovieReleaseStatus.Upcoming =>
                    avail.UpcomingDigitalDate.HasValue
                        ? $"This movie is not yet released. Digital release expected around {avail.UpcomingDigitalDate:MMMM yyyy}."
                        : "This movie is not yet released and has no confirmed digital release date.",
                _ => null
            };
        }

        var results = new List<string>();

        // Jellyseerr is the primary request method — it routes internally to Radarr/Sonarr.
        // Direct arr integration is only used when Jellyseerr is not configured.
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
        else
        {
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
            tmdb_id = tmdbId,
            availability_note = availabilityWarning
        });
    }

    private async Task<string> CheckStatusAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("tmdb_id", out var tidEl) || !tidEl.TryGetInt32(out var tmdbId))
            return JsonSerializer.Serialize(new { error = "tmdb_id is required" });

        var type     = args.TryGetProperty("type", out var t) ? t.GetString() ?? "movie" : "movie";
        var isSeries = type == "tv" || type == "series";
        var config   = Plugin.Instance!.Configuration;

        // Jellyfin library is the only ground truth — if it's here, it's watchable NOW
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var inJellyfin = ownedIds.Contains(tmdbId);

        if (inJellyfin)
        {
            // Item is in the library — skip download service polling, the answer is already definitive
            return JsonSerializer.Serialize(new
            {
                tmdb_id    = tmdbId,
                in_jellyfin = true,
                message    = "Available to watch now in Jellyfin. Download service status is irrelevant — the file is there."
            });
        }

        // Not in Jellyfin yet — check download services to give progress info
        var serviceStatuses = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            var (code, name, _) = await _arr.CheckJellyseerrStatusAsync(tmdbId, isSeries, ct).ConfigureAwait(false);
            serviceStatuses.Add($"Jellyseerr: {name}");
        }

        if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl) && !isSeries)
        {
            var (exists, hasFile, _) = await _arr.CheckRadarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            serviceStatuses.Add(exists ? (hasFile ? "Radarr: downloaded (importing soon)" : "Radarr: monitored, not yet downloaded") : "Radarr: not added");
        }

        if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl) && isSeries)
        {
            var (exists, pct, _) = await _arr.CheckSonarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            serviceStatuses.Add(exists ? $"Sonarr: {pct}% downloaded" : "Sonarr: not added");
        }

        return JsonSerializer.Serialize(new
        {
            tmdb_id     = tmdbId,
            in_jellyfin = false,
            message     = "Not in Jellyfin yet.",
            service_statuses = serviceStatuses
        });
    }

    // ── LLM plumbing ─────────────────────────────────────────────────────────

    private string BuildSystemPrompt(User user, Configuration.PluginConfiguration config)
    {
        var services = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)) services.Add("Jellyseerr");
        else
        {
            if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl)) services.Add("Radarr");
            if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl)) services.Add("Sonarr");
        }
        var serviceList = services.Count > 0 ? string.Join(", ", services) : "none configured";

        // Prefer the stored LLM-generated taste profile; fall back to live stats summary
        var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == user.Id.ToString("N"));
        string tasteSection;
        if (reg is not null && !string.IsNullOrWhiteSpace(reg.TasteProfileText))
        {
            tasteSection = reg.TasteProfileText;
        }
        else
        {
            var profile = _watchHistory.BuildTasteProfile(user, Math.Min(config.MaxWatchedItems, 30));
            if (profile.TotalWatched == 0)
            {
                tasteSection = "Watch history: No data yet — ask the user what genres or titles they enjoy before making recommendations.";
            }
            else
            {
                var genres  = string.Join(", ", profile.TopGenres.Select(g => g.Genre));
                var samples = profile.SampleTitles.Count > 0 ? string.Join(", ", profile.SampleTitles) : "none recorded";
                var favs    = profile.FavoriteTitles.Count > 0 ? string.Join(", ", profile.FavoriteTitles) : "none recorded";
                tasteSection = $"Top genres: {genres}\nEra preference: {profile.EraPreference}\n" +
                               $"Content mix: {profile.MoviePercent}% movies, {100 - profile.MoviePercent}% shows\n" +
                               $"Enjoyed: {samples}\nFavourites: {favs}";
            }
        }

        return $"""
You are a media assistant for a personal Jellyfin home server. Your sole purpose is helping the user discover, search, and request movies and TV shows. You do not write letters, poems, code, or anything unrelated to media discovery and requests. If asked to do something outside media (write a letter, answer trivia, help with work, etc.), reply: "I'm only here to help with movies and TV — ask me what you want to watch!"

USER TASTE PROFILE:
- {tasteSection}

DOWNLOAD SERVICES: {serviceList}

TOOLS:
- get_ai_recommendations: returns the personalised AI picks already computed from the user's watch history — USE THIS FIRST for any recommendation request
- browse_tmdb: browse TMDB lists — popular, top_rated, trending_week, now_playing, upcoming — use this when the user asks "what's popular?", "what's trending?", "what are top-rated movies?", "what's in theaters?", "what's coming soon?", or wants variety after already seeing AI picks
- search_library: search the user's actual Jellyfin library by title — use this when they ask what's in their library or if a specific title is there
- get_recently_added: list the most recently added movies/shows in the library, sorted by date added — use this for "what's new?", "recently added", "what did I get lately?" questions
- discover_content: browse TMDB by genre/type with optional page for variety — use as a fallback when the user wants a specific genre or get_ai_recommendations is empty
- search_content: find a specific title on TMDB (searches movies + TV simultaneously) to get its verified TMDB ID
- request_media: submit a download request to Jellyseerr/Radarr/Sonarr
- check_status: check if something is already downloaded or queued
- sync_to_jellyfin: refresh the AI recommendation stubs in the user's Jellyfin library (only if they ask)
- refresh_taste_profile: regenerate the user's taste profile from their watch history (only if they ask)

RULES:
1. For any "recommend", "what should I watch", or "find me something" request, ALWAYS call get_ai_recommendations first. These are personalised picks from the recommendation engine — show them all. Only call discover_content if the user explicitly asks for a specific genre, or get_ai_recommendations returns found: false.
2. When the user says "something different", "more options", "not those", "other recommendations", "try again", or any similar phrase AFTER already seeing a recommendation list — do NOT call get_ai_recommendations again (it returns the same pre-computed list every time). Instead: call browse_tmdb with page=2 for variety, OR call discover_content with different genres and page=2. Rotate through both to give genuinely different results.
3. Present ALL titles returned by any tool, exactly as returned, in order. Use the title, year, and overview from the tool result. Never invent, substitute, omit, or reorder titles. Never filter by era, prestige, mainstream vs art-house, or any other personal judgment — show everything the tool returns.
4. For every initial recommendation set, include at least 1 wildcard — a genre or style clearly outside the user's usual taste. Call discover_content with a different genre to get this wildcard pick. Label it lightly (e.g. "something different") so the user knows it's a stretch pick.
5. get_ai_recommendations results are titles NOT YET in the main library — they can be requested. discover_content and browse_tmdb already exclude items in the library too.
6. If you want to refine or try different genres, call discover_content again with those genres — do not ask the user what to search for without doing it.
7. For "what's popular?", "what's trending?", "what's top rated?", "what's in theaters?", "what's coming soon?" — use browse_tmdb with the appropriate category (popular, trending_week, top_rated, now_playing, upcoming).
8. Always call search_content before request_media to get the verified TMDB ID — never guess it.
9. If search_content returns in_library: true, tell the user that title is already in their Jellyfin library — do NOT offer to request it.
10. Confirm what you're about to request before calling request_media, unless the user already said "yes", "sure", or "request it".
11. After a successful request_media call, always tell the user: "You'll get a notification here when it arrives in Jellyfin." The download status poller tracks all requests and sends automatic notifications — never tell the user you can't notify them.
    If the result contains an availability_note (e.g. "in theaters only", "not yet released"), always include it in your reply so the user understands the download may be delayed.
12. When check_status returns in_jellyfin: true, ALWAYS tell the user the title is available to watch in Jellyfin RIGHT NOW. Never mention download service statuses when in_jellyfin is true — they lag behind and are irrelevant. "Available now" is the only answer that matters.
13. If the user asks to "search my library", "check my library", or "browse my library" without specifying a title, ask what specific title or genre they're looking for rather than calling search_library with no query.
14. Be concise. Use <b>bold</b> for titles. No markdown asterisks or bullet dashes.
15. If no download services are configured, say so when the user tries to request something.
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
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("TelegramAgentLoop: provider returned {Status} — {Body}",
                (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
            resp.EnsureSuccessStatusCode(); // throws HttpRequestException with status code
        }
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static object[] GetToolDefinitions() =>
    [
        new
        {
            type = "function",
            function = new
            {
                name = "get_ai_recommendations",
                description = "Returns the personalised AI recommendation picks already computed from this user's watch history. ALWAYS call this first for any recommendation request — these are curated by the recommendation engine, not generic popularity lists.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["type"] = new { type = "string", @enum = new[] { "movie", "tv" }, description = "Optional — filter to movies or TV shows only. Omit to return both." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "browse_tmdb",
                description = "Browse a named TMDB list — popular, top-rated, trending, now playing, or upcoming. Use this when the user asks what's popular/trending/top-rated/in-theaters/coming-soon, or wants variety after already seeing AI picks. Use page 2+ to get a genuinely different set of results.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["category"] = new { type = "string", @enum = new[] { "popular", "top_rated", "trending_week", "trending_day", "now_playing", "upcoming", "airing_today", "on_the_air" }, description = "Which TMDB list to fetch. 'now_playing'/'upcoming' are movies; 'airing_today'/'on_the_air' are TV." },
                        ["type"]     = new { type = "string", @enum = new[] { "movie", "tv" }, description = "Movie or TV show" },
                        ["page"]     = new { type = "integer", description = "Page number 1-5. Use 2+ to get a different set of results for variety." },
                        ["count"]    = new { type = "integer", description = "How many results to return (5-20, default 10)." }
                    },
                    required = new[] { "category", "type" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "search_library",
                description = "Search the user's Jellyfin library by title. Use this when they ask what's in their library, whether a specific title is available, or to browse what they already have.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "Title or partial title to search for" }
                    },
                    required = new[] { "query" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_recently_added",
                description = "Returns the most recently added movies or TV shows from the user's Jellyfin library, sorted by date added (newest first). Use this for questions like 'what's new?', 'what was recently added?', 'what did I get lately?'.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["type"]  = new { type = "string", @enum = new[] { "movie", "tv", "all" }, description = "Filter by type. Omit or use 'all' for both movies and shows." },
                        ["count"] = new { type = "integer", description = "How many items to return (1-25, default 15)" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "discover_content",
                description = "Browse TMDB for popular movies or TV shows matching given genres. Use as a fallback when get_ai_recommendations is empty or the user wants genre-specific picks. Use page 2+ for variety when the user wants something different.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["type"]   = new { type = "string", @enum = new[] { "movie", "tv" }, description = "Movie or TV show" },
                        ["genres"] = new { type = "array", items = new { type = "string" }, description = "Genre names e.g. ['Action','Thriller']. Omit to use the user's taste profile." },
                        ["count"]  = new { type = "integer", description = "How many results to return (5-10, default 5). Always use at least 5." },
                        ["page"]   = new { type = "integer", description = "Page number 1-5. Use 2+ to get a genuinely different set of results when the user wants variety." }
                    },
                    required = new[] { "type" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "search_content",
                description = "Search TMDB for a specific movie or TV show by title. Searches both movies and TV simultaneously — omit type if unsure. Returns up to 5 matches with in_library flag. Always call this before request_media.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "Title to search for" },
                        ["type"]  = new { type = "string", @enum = new[] { "movie", "tv" }, description = "Optional — omit to search both movies and TV at once" },
                        ["year"]  = new { type = "integer", description = "Optional release year to narrow results" }
                    },
                    required = new[] { "query" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "request_media",
                description = "Submit a download request via Jellyseerr, Radarr, or Sonarr. Always call search_content first to confirm the TMDB ID.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["tmdb_id"] = new { type = "integer", description = "TMDB ID from search_content" },
                        ["title"]   = new { type = "string",  description = "Human-readable title" },
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
                description = "Check the download or availability status of a specific title.",
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
        },
        new
        {
            type = "function",
            function = new
            {
                name = "sync_to_jellyfin",
                description = "Refresh the user's AI recommendation stubs in their Jellyfin library. Only call this when the user explicitly asks to update their Jellyfin recommendations.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "refresh_taste_profile",
                description = "Regenerate the user's taste profile from their watch history. Only call this when the user explicitly asks to refresh or update their taste profile.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            }
        }
    ];

    // ── Tool status messages ──────────────────────────────────────────────────

    private static string? BuildToolStatusMessage(string toolName, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var args = doc.RootElement;
            return toolName switch
            {
                "get_ai_recommendations" =>
                    args.TryGetProperty("type", out var art) && art.GetString() is { } artype && artype != "all"
                        ? $"🤖 Fetching your AI {artype} picks..."
                        : "🤖 Fetching your personalised AI picks...",

                "browse_tmdb" =>
                    args.TryGetProperty("category", out var bcat) && bcat.GetString() is { } bcatStr
                        ? bcatStr switch
                        {
                            "top_rated"     => "⭐ Fetching top-rated titles...",
                            "trending_week" => "🔥 Fetching what's trending this week...",
                            "trending_day"  => "🔥 Fetching what's trending today...",
                            "now_playing"   => "🎟️ Fetching what's in theaters now...",
                            "upcoming"      => "📅 Fetching upcoming releases...",
                            "airing_today"  => "📺 Fetching shows airing today...",
                            "on_the_air"    => "📺 Fetching currently airing shows...",
                            _               => "🌟 Fetching popular titles..."
                        }
                        : "🌟 Fetching popular titles...",

                "search_library" =>
                    args.TryGetProperty("query", out var lq) && lq.GetString() is { Length: > 0 } ltitle
                        ? $"📚 Searching library for <b>{EscapeHtml(ltitle)}</b>..."
                        : "📚 Searching your library...",

                "get_recently_added" =>
                    args.TryGetProperty("type", out var rt) && rt.GetString() is { } rtype && rtype != "all"
                        ? $"🆕 Getting recently added {rtype}s..."
                        : "🆕 Getting recently added titles...",

                "search_content" =>
                    args.TryGetProperty("query", out var q) && q.GetString() is { Length: > 0 } title
                        ? $"🔍 Searching for <b>{EscapeHtml(title)}</b>..."
                        : "🔍 Searching TMDB...",

                "discover_content" =>
                    args.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                    && g.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Take(2).ToList() is { Count: > 0 } genreList
                        ? $"🎬 Finding <b>{string.Join(" &amp; ", genreList)}</b> picks..."
                        : "🎬 Finding recommendations...",

                "request_media" =>
                    args.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } rt
                        ? $"📤 Requesting <b>{EscapeHtml(rt)}</b>..."
                        : "📤 Submitting request...",

                "check_status" =>
                    args.TryGetProperty("title", out var st) && st.GetString() is { Length: > 0 } stt
                        ? $"📊 Checking status of <b>{EscapeHtml(stt)}</b>..."
                        : "📊 Checking download status...",

                "sync_to_jellyfin" => "🔄 Syncing your Jellyfin library...",

                "refresh_taste_profile" => "🧠 Regenerating your taste profile from watch history...",

                _ => null
            };
        }
        catch { return null; }
    }

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
