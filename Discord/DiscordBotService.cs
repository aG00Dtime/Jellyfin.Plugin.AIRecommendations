using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommendations.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Discord;

/// <summary>
/// Background service that connects to the Discord Gateway (WebSocket), handles DMs,
/// manages conversation sessions and link codes, and dispatches messages to
/// <see cref="TelegramAgentLoop"/> (the shared agent — platform-agnostic).
/// </summary>
public sealed class DiscordBotService : IHostedService
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const int DirectMessagesIntent = 1 << 12; // 4096 — DMs only, no privileged intents needed
    private const int MaxHistoryMessages = 20;
    private const string HttpClientName = "DiscordBot";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan LinkCodeTtl = TimeSpan.FromMinutes(10);

    private readonly TelegramAgentLoop _agent;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordBotService> _logger;

    private readonly ConcurrentDictionary<ulong, ConversationSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingDiscordLinkCode> _pendingCodes = new();

    // Cache userId → DM channelId so we don't re-open the channel on every proactive DM
    private readonly ConcurrentDictionary<ulong, ulong> _dmChannelCache = new();

    // Gateway session state — reset on fresh IDENTIFY, retained across RESUMEs
    private string? _sessionId;
    private string? _resumeGatewayUrl;
    private int _seq = -1;

    private CancellationTokenSource? _cts;
    private Task? _gatewayTask;

    public DiscordBotService(
        TelegramAgentLoop agent,
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordBotService> logger)
    {
        _agent = agent;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _gatewayTask = Task.Run(() => GatewayLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("DiscordBotService: started (gateway connects once a bot token is configured)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_gatewayTask is not null)
            await Task.WhenAny(_gatewayTask, Task.Delay(3000, cancellationToken)).ConfigureAwait(false);
    }

    // ── Link code management (called by controller) ───────────────────────────

    public string CreateLinkCode(ulong userId, string? username)
    {
        foreach (var kv in _pendingCodes.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow).ToList())
            _pendingCodes.TryRemove(kv.Key, out _);

        var code = Random.Shared.Next(100000, 1000000).ToString("D6");
        _pendingCodes[code] = new PendingDiscordLinkCode(code, userId, DateTime.UtcNow + LinkCodeTtl, username);
        return code;
    }

    public PendingDiscordLinkCode? ConsumePendingCode(string code)
    {
        if (!_pendingCodes.TryRemove(code, out var pending)) return null;
        return pending.ExpiresAt < DateTime.UtcNow ? null : pending;
    }

    // ── REST API helpers ──────────────────────────────────────────────────────

    private async Task<ulong?> GetOrCreateDmChannelAsync(ulong userId, CancellationToken ct)
    {
        if (_dmChannelCache.TryGetValue(userId, out var cached))
            return cached;

        var token = Plugin.Instance?.Configuration.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/users/@me/channels");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { recipient_id = userId.ToString() }),
                Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var idEl)
                && ulong.TryParse(idEl.GetString(), out var channelId))
            {
                _dmChannelCache[userId] = channelId;
                return channelId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DiscordBotService: failed to open DM channel for user {UserId}", userId);
        }

        return null;
    }

    /// <summary>Sends a DM to a Discord user by ID. Used by the download status poller and link confirmation.</summary>
    public async Task SendDmAsync(ulong userId, string text, CancellationToken ct = default)
    {
        var channelId = await GetOrCreateDmChannelAsync(userId, ct).ConfigureAwait(false);
        if (channelId is null) return;
        await SendToChannelAsync(channelId.Value, text, ct).ConfigureAwait(false);
    }

    public async Task SendToChannelAsync(ulong channelId, string text, CancellationToken ct = default)
    {
        var token = Plugin.Instance?.Configuration.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        // Discord caps messages at 2 000 characters; chunk if needed
        foreach (var chunk in SplitMessage(text, 2000))
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var req = new HttpRequestMessage(
                    HttpMethod.Post, $"{ApiBase}/channels/{channelId}/messages");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { content = chunk }),
                    Encoding.UTF8, "application/json");
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("DiscordBotService: send failed ({Status}): {Body}", resp.StatusCode, body.Length > 300 ? body[..300] : body);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "DiscordBotService: send error for channel {ChannelId}", channelId);
            }
        }
    }

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        if (text.Length <= maxLength) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text[i..Math.Min(i + maxLength, text.Length)];
    }

    private async Task SendTypingAsync(ulong channelId, CancellationToken ct)
    {
        var token = Plugin.Instance?.Configuration.DiscordBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{ApiBase}/channels/{channelId}/typing");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var _ = await client.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { }
    }

    // ── Gateway outer loop (reconnects on any error) ──────────────────────────

    private async Task GatewayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = Plugin.Instance?.Configuration.DiscordBotToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    continue;
                }

                await RunGatewaySessionAsync(token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DiscordBotService: gateway session ended, retrying in 5 s");
                try { await Task.Delay(5000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ── Single gateway session ────────────────────────────────────────────────

    private async Task RunGatewaySessionAsync(string token, CancellationToken ct)
    {
        var gatewayBaseUrl = await GetGatewayUrlAsync(token, ct).ConfigureAwait(false);
        var connectUrl = (_sessionId is not null && _resumeGatewayUrl is not null
            ? _resumeGatewayUrl
            : gatewayBaseUrl) + "?v=10&encoding=json";

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "JellyfinAIRecommendations/1.0");

        _logger.LogInformation("DiscordBotService: connecting to gateway");
        await ws.ConnectAsync(new Uri(connectUrl), ct).ConfigureAwait(false);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeatTask = null;
        var reconnect = false;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && !reconnect)
            {
                var payload = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
                if (payload is null) break; // WebSocket closed

                var op = payload.Value.TryGetProperty("op", out var opEl) ? opEl.GetInt32() : -1;

                if (payload.Value.TryGetProperty("s", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
                    _seq = seqEl.GetInt32();

                switch (op)
                {
                    case 10: // HELLO — start heartbeating, then identify or resume
                    {
                        var intervalMs = payload.Value.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ws, intervalMs, heartbeatCts.Token));

                        if (_sessionId is not null)
                        {
                            _logger.LogInformation("DiscordBotService: resuming session {SessionId}", _sessionId);
                            await SendGatewayPayloadAsync(ws, 6, new
                            {
                                token = $"Bot {token}",
                                session_id = _sessionId,
                                seq = _seq >= 0 ? (int?)_seq : null
                            }, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogInformation("DiscordBotService: identifying (DIRECT_MESSAGES intent)");
                            await SendGatewayPayloadAsync(ws, 2, new
                            {
                                token = $"Bot {token}",
                                intents = DirectMessagesIntent,
                                properties = new { os = "linux", browser = "disco", device = "disco" }
                            }, ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    case 0: // DISPATCH
                    {
                        var t = payload.Value.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;
                        if (payload.Value.TryGetProperty("d", out var d))
                        {
                            var dClone = d.Clone();
                            _ = Task.Run(() => HandleDispatchAsync(t, dClone, ct), ct);
                        }
                        break;
                    }

                    case 7: // RECONNECT — server asks us to reconnect; we'll RESUME
                        _logger.LogInformation("DiscordBotService: RECONNECT received, will RESUME");
                        reconnect = true;
                        break;

                    case 9: // INVALID_SESSION
                    {
                        var resumable = payload.Value.TryGetProperty("d", out var resEl) && resEl.GetBoolean();
                        _logger.LogInformation("DiscordBotService: INVALID_SESSION resumable={Resumable}", resumable);
                        if (!resumable)
                        {
                            _sessionId = null;
                            _seq = -1;
                            _resumeGatewayUrl = null;
                        }
                        await Task.Delay(resumable ? 1000 : 2000, ct).ConfigureAwait(false);
                        reconnect = true;
                        break;
                    }

                    case 11: // HEARTBEAT_ACK — acknowledged
                        break;
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeatTask is not null)
                try { await heartbeatTask.ConfigureAwait(false); } catch { }

            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, int intervalMs, CancellationToken ct)
    {
        // Initial jitter per Discord spec
        var jitter = Random.Shared.Next(0, intervalMs);
        try { await Task.Delay(jitter, ct).ConfigureAwait(false); } catch { return; }

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var seq = _seq >= 0 ? (object?)_seq : null;
                await SendGatewayPayloadAsync(ws, 1, seq, ct).ConfigureAwait(false);
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DiscordBotService: heartbeat error");
                break;
            }
        }
    }

    // ── Dispatch handling ─────────────────────────────────────────────────────

    private async Task HandleDispatchAsync(string? eventName, JsonElement data, CancellationToken ct)
    {
        try
        {
            switch (eventName)
            {
                case "READY":
                {
                    if (data.TryGetProperty("session_id", out var sid))
                        _sessionId = sid.GetString();
                    if (data.TryGetProperty("resume_gateway_url", out var rgw))
                        _resumeGatewayUrl = rgw.GetString();
                    var botName = data.TryGetProperty("user", out var u)
                        && u.TryGetProperty("username", out var un)
                        ? un.GetString() : "bot";
                    _logger.LogInformation("DiscordBotService: connected as {BotName}", botName);
                    break;
                }

                case "MESSAGE_CREATE":
                    await HandleMessageAsync(data, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscordBotService: error in dispatch handler for event {Event}", eventName);
        }
    }

    private async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        // Ignore messages from other bots (includes the bot's own messages)
        if (msg.TryGetProperty("author", out var author)
            && author.TryGetProperty("bot", out var botFlag)
            && botFlag.GetBoolean())
            return;

        if (!msg.TryGetProperty("channel_id", out var cidEl)
            || !ulong.TryParse(cidEl.GetString(), out var channelId))
            return;

        if (!msg.TryGetProperty("author", out var authEl)
            || !authEl.TryGetProperty("id", out var aidEl)
            || !ulong.TryParse(aidEl.GetString(), out var authorId))
            return;

        var username = authEl.TryGetProperty("username", out var unEl) ? unEl.GetString() : null;
        var text = (msg.TryGetProperty("content", out var contEl) ? contEl.GetString() ?? string.Empty : string.Empty).Trim();

        _logger.LogInformation("Discord DM from {UserId}: {Text}", authorId, text.Length > 80 ? text[..80] + "…" : text);

        var config = Plugin.Instance?.Configuration;
        if (config is null) return;

        // Cache DM channel (stable per user pair)
        _dmChannelCache[authorId] = channelId;

        // Evict idle sessions
        foreach (var kv in _sessions.Where(kv => kv.Value.LastActivity < DateTime.UtcNow - SessionTtl).ToList())
            _sessions.TryRemove(kv.Key, out _);

        // ── Commands ──────────────────────────────────────────────────────────

        if (text is "/help" or "/commands")
        {
            await SendToChannelAsync(channelId,
                "**AI Recommendations — Commands**\n\n" +
                "`/link` — connect your Jellyfin account (required before chatting)\n" +
                "`/unlink` — disconnect your Jellyfin account\n" +
                "`/profile` — show your personalised taste profile\n" +
                "`/reset` — clear the current conversation and start fresh\n" +
                "`/help` — show this message\n\n" +
                "Once linked, just talk to me naturally — ask for something to watch, a mood, a genre, or \"something like X\". " +
                "I can also request downloads for you if Radarr / Sonarr are configured.",
                ct).ConfigureAwait(false);
            return;
        }

        if (text is "/link" or "/start")
        {
            var code = CreateLinkCode(authorId, username);
            await SendToChannelAsync(channelId,
                "To link your Jellyfin account, go to:\n" +
                "**Dashboard → Plugins → AI Recommendations → Discord**\n\n" +
                $"Enter this code:\n```\n{code}\n```\n" +
                "*Code expires in 10 minutes.*",
                ct).ConfigureAwait(false);
            return;
        }

        if (text == "/unlink")
        {
            var removed = config.DiscordUserLinks.RemoveAll(l => l.DiscordUserId == authorId) > 0;
            if (removed)
            {
                Plugin.Instance!.SaveConfiguration();
                _sessions.TryRemove(authorId, out _);
                await SendToChannelAsync(channelId, "Your Discord account has been unlinked from Jellyfin.", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await SendToChannelAsync(channelId, "No linked Jellyfin account found.", ct).ConfigureAwait(false);
            }
            return;
        }

        if (text == "/reset")
        {
            _sessions.TryRemove(authorId, out _);
            await SendToChannelAsync(channelId, "Conversation reset. What would you like to find?", ct)
                .ConfigureAwait(false);
            return;
        }

        if (text == "/profile")
        {
            var profileLink = config.DiscordUserLinks.FirstOrDefault(l => l.DiscordUserId == authorId);
            if (profileLink is null)
            {
                await SendToChannelAsync(channelId, "Your Discord account isn't linked yet. Send `/link` to get started.", ct)
                    .ConfigureAwait(false);
                return;
            }
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == profileLink.JellyfinUserId);
            if (reg is null || string.IsNullOrWhiteSpace(reg.TasteProfileText))
            {
                await SendToChannelAsync(channelId, "No taste profile has been generated yet. It will be created on your next sync.", ct)
                    .ConfigureAwait(false);
                return;
            }
            await SendToChannelAsync(channelId, $"**Your Taste Profile**\n\n{reg.TasteProfileText}", ct)
                .ConfigureAwait(false);
            return;
        }

        if (text.StartsWith('/'))
        {
            await SendToChannelAsync(channelId,
                $"Unknown command `{text}`. Send `/help` to see what's available.",
                ct).ConfigureAwait(false);
            return;
        }

        // ── Must be linked ────────────────────────────────────────────────────

        var link = config.DiscordUserLinks.FirstOrDefault(l => l.DiscordUserId == authorId);
        if (link is null)
        {
            await SendToChannelAsync(channelId,
                "Your Discord account isn't linked to Jellyfin yet.\n\nSend `/link` to get started.",
                ct).ConfigureAwait(false);
            return;
        }

        // ── Agent dispatch ────────────────────────────────────────────────────

        var session = _sessions.GetOrAdd(authorId, _ => new ConversationSession { JellyfinUserId = link.JellyfinUserId });
        session.LastActivity = DateTime.UtcNow;

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            _logger.LogInformation("Discord: dispatching to agent for user {UserId}", link.JellyfinUserId);

            // Keep the Discord "typing..." indicator alive every 8 s (Discord clears it after ~10 s)
            _ = Task.Run(async () =>
            {
                while (!typingCts.Token.IsCancellationRequested)
                {
                    await SendTypingAsync(channelId, typingCts.Token).ConfigureAwait(false);
                    try { await Task.Delay(8000, typingCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, typingCts.Token);

            string reply;
            try
            {
                reply = await _agent.RunAsync(
                    link.JellyfinUserId,
                    text,
                    session.History,
                    async status => await SendToChannelAsync(channelId, StripHtml(status), ct).ConfigureAwait(false),
                    turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                typingCts.Cancel();
                _logger.LogWarning("DiscordBotService: agent timed out (90 s) for user {UserId}", authorId);
                await SendToChannelAsync(channelId, "The AI took too long to respond — please try again.", ct)
                    .ConfigureAwait(false);
                return;
            }

            typingCts.Cancel();

            while (session.History.Count > MaxHistoryMessages)
                session.History.RemoveAt(0);

            _logger.LogInformation("Discord: sending reply ({Len} chars) to channel {ChannelId}", reply.Length, channelId);
            await SendToChannelAsync(channelId, HtmlToMarkdown(reply), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            typingCts.Cancel();
            _logger.LogWarning(ex, "DiscordBotService: agent error for user {UserId}", authorId);
            await SendToChannelAsync(channelId, "Something went wrong. Please try again.", ct).ConfigureAwait(false);
        }
    }

    // ── Format conversion ─────────────────────────────────────────────────────

    // The agent produces Telegram HTML (<b>, <i>, etc.). Convert to Discord Markdown for final replies.
    private static string HtmlToMarkdown(string html)
    {
        html = html
            .Replace("<b>", "**").Replace("</b>", "**")
            .Replace("<strong>", "**").Replace("</strong>", "**")
            .Replace("<i>", "*").Replace("</i>", "*")
            .Replace("<em>", "*").Replace("</em>", "*")
            .Replace("<code>", "`").Replace("</code>", "`")
            .Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");

        // Strip any remaining tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", string.Empty);

        // Collapse 3+ newlines to 2
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    // Status messages from the agent contain Telegram HTML; strip tags for interim Discord status messages.
    private static string StripHtml(string html)
    {
        html = html.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        return System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", string.Empty).Trim();
    }

    // ── Gateway wire protocol ─────────────────────────────────────────────────

    private static async Task SendGatewayPayloadAsync(ClientWebSocket ws, int op, object? data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { op, d = data });
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private async Task<string> GetGatewayUrlAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gateway/bot");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var urlEl) && urlEl.GetString() is { } url)
                return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DiscordBotService: failed to fetch gateway URL, using fallback");
        }

        return "wss://gateway.discord.gg";
    }
}
