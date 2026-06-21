using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Telegram;

/// <summary>
/// Background service that long-polls the Telegram Bot API, routes commands,
/// manages conversation sessions and pending link codes, and dispatches chat
/// messages to <see cref="TelegramAgentLoop"/>.
/// </summary>
public sealed class TelegramBotService : IHostedService
{
    private const int LongPollTimeoutSeconds = 25;
    private const int MaxHistoryMessages = 20;
    private const string ClientName = "TelegramBot";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan LinkCodeTtl = TimeSpan.FromMinutes(10);

    private readonly TelegramAgentLoop _agent;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramBotService> _logger;

    private readonly ConcurrentDictionary<long, ConversationSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingLinkCode> _pendingCodes = new();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private int _lastUpdateId;

    public TelegramBotService(
        TelegramAgentLoop agent,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramBotService> logger)
    {
        _agent = agent;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Always start the loop — it waits internally until a token is configured,
        // so adding/changing the token in settings takes effect without a restart.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("TelegramBotService: started (polling begins once a bot token is configured)");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_pollTask is not null)
            await Task.WhenAny(_pollTask, Task.Delay(3000, cancellationToken)).ConfigureAwait(false);
    }

    // ── Link code management (called by controller) ───────────────────────────

    public string CreateLinkCode(long chatId, string? username)
    {
        // Evict stale codes
        foreach (var kv in _pendingCodes.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow).ToList())
            _pendingCodes.TryRemove(kv.Key, out _);

        var code = Random.Shared.Next(100000, 1000000).ToString("D6");
        _pendingCodes[code] = new PendingLinkCode(code, chatId, DateTime.UtcNow + LinkCodeTtl, username);
        return code;
    }

    public PendingLinkCode? ConsumePendingCode(string code)
    {
        if (!_pendingCodes.TryRemove(code, out var pending)) return null;
        return pending.ExpiresAt < DateTime.UtcNow ? null : pending;
    }

    // ── Outbound messaging ────────────────────────────────────────────────────

    private async Task SendChatActionAsync(long chatId, CancellationToken ct)
    {
        var token = Plugin.Instance?.Configuration.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        var payload = new { chat_id = chatId, action = "typing" };
        var client = _httpClientFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendChatAction");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try { using var _ = await client.SendAsync(req, ct).ConfigureAwait(false); }
        catch { }
    }

    // Telegram HTML only supports: <b> <i> <u> <s> <code> <pre> <a href="...">
    // Strip/replace everything else so Telegram doesn't reject the message.
    private static string SanitizeTelegramHtml(string html)
    {
        // Convert common semantic tags to Telegram equivalents
        html = html.Replace("<strong>", "<b>", StringComparison.OrdinalIgnoreCase)
                   .Replace("</strong>", "</b>", StringComparison.OrdinalIgnoreCase)
                   .Replace("<em>", "<i>", StringComparison.OrdinalIgnoreCase)
                   .Replace("</em>", "</i>", StringComparison.OrdinalIgnoreCase);

        // Convert block tags to newlines
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"</p>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"</li>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Strip attributes from allowed tags — Telegram only accepts bare tags (e.g. <b> not <b style="...">)
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(b|i|u|s|code|pre)\s[^>]*>", "<$1>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // For <a>, preserve only href
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<a\s+[^>]*href=""([^""]*)""[^>]*>", "<a href=\"$1\">", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<a(?!\s+href=)[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Strip all remaining tags except Telegram's allowed set
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<(?!/?(?:b|i|u|s|code|pre|a)(?:\s[^>]*)?>)[^>]+>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Close any dangling open tags (e.g. <b> without </b>) to prevent Telegram parse errors
        html = CloseDanglingTags(html);

        // Collapse runs of 3+ newlines to 2
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    private static readonly string[] _balancedTags = ["b", "i", "u", "s", "code", "pre"];

    private static string CloseDanglingTags(string html)
    {
        var open = new Stack<string>();
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) break;
            var gt = html.IndexOf('>', lt);
            if (gt < 0) break;

            var inner = html[(lt + 1)..gt].Trim();
            var isClose = inner.StartsWith('/');
            var name = (isClose ? inner[1..] : inner.Split(' ', 2)[0]).ToLowerInvariant();

            if (Array.Exists(_balancedTags, t => t == name))
            {
                if (isClose) { if (open.Count > 0 && open.Peek() == name) open.Pop(); }
                else open.Push(name);
            }

            i = gt + 1;
        }

        if (open.Count == 0) return html;
        var sb = new System.Text.StringBuilder(html);
        while (open.Count > 0) sb.Append($"</{open.Pop()}>");
        return sb.ToString();
    }

    public async Task SendMessageAsync(long chatId, string html, CancellationToken ct = default)
    {
        var token = Plugin.Instance?.Configuration.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        html = SanitizeTelegramHtml(html);
        var payload = new { chat_id = chatId, text = html, parse_mode = "HTML" };
        var client = _httpClientFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendMessage");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Telegram sendMessage failed ({Status}): {Body}", resp.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Telegram sendMessage error for chatId {ChatId}", chatId);
        }
    }

    // ── Long-poll loop ────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(ClientName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = Plugin.Instance?.Configuration.TelegramBotToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    continue;
                }

                var url = $"https://api.telegram.org/bot{token}/getUpdates" +
                          $"?offset={_lastUpdateId + 1}&timeout={LongPollTimeoutSeconds}&allowed_updates=[\"message\"]";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Telegram getUpdates: {Status}", resp.StatusCode);
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    continue;
                }

                var json    = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var updates = ParseUpdates(json);

                foreach (var update in updates)
                {
                    _lastUpdateId = Math.Max(_lastUpdateId, update.UpdateId);
                    if (update.Message is { } msg)
                        _ = Task.Run(() => HandleUpdateAsync(msg, ct), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelegramBotService: poll error, backing off 5 s");
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleUpdateAsync(TgMessage message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text   = (message.Text ?? string.Empty).Trim();

        _logger.LogInformation("Telegram message from chat {ChatId}: {Text}", chatId, text.Length > 80 ? text[..80] + "…" : text);

        var config = Plugin.Instance?.Configuration;
        if (config is null) return;

        // Evict idle sessions
        foreach (var kv in _sessions.Where(kv => kv.Value.LastActivity < DateTime.UtcNow - SessionTtl).ToList())
            _sessions.TryRemove(kv.Key, out _);

        // ── Commands ──────────────────────────────────────────────────────────

        if (text is "/link" or "/start")
        {
            var code = CreateLinkCode(chatId, message.From?.Username);
            await SendMessageAsync(chatId,
                "To link your Jellyfin account, go to:\n" +
                "<b>Dashboard → Plugins → AI Recommendations → Telegram</b>\n\n" +
                $"Enter this code:\n\n<b>{code}</b>\n\n" +
                "<i>Code expires in 10 minutes.</i>",
                ct).ConfigureAwait(false);
            return;
        }

        if (text == "/unlink")
        {
            var removed = config.TelegramUserLinks.RemoveAll(l => l.ChatId == chatId) > 0;
            if (removed)
            {
                Plugin.Instance!.SaveConfiguration();
                _sessions.TryRemove(chatId, out _);
                await SendMessageAsync(chatId, "Your Telegram account has been unlinked from Jellyfin.", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await SendMessageAsync(chatId, "No linked Jellyfin account found.", ct).ConfigureAwait(false);
            }
            return;
        }

        if (text == "/reset")
        {
            _sessions.TryRemove(chatId, out _);
            await SendMessageAsync(chatId, "Conversation reset. What would you like to find?", ct)
                .ConfigureAwait(false);
            return;
        }

        if (text == "/profile")
        {
            var profileLink = config.TelegramUserLinks.FirstOrDefault(l => l.ChatId == chatId);
            if (profileLink is null)
            {
                await SendMessageAsync(chatId, "Your Telegram account isn't linked yet. Send /link to get started.", ct).ConfigureAwait(false);
                return;
            }
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == profileLink.JellyfinUserId);
            if (reg is null || string.IsNullOrWhiteSpace(reg.TasteProfileText))
            {
                await SendMessageAsync(chatId, "No taste profile has been generated yet. It will be created on your next sync.", ct).ConfigureAwait(false);
                return;
            }
            await SendMessageAsync(chatId, $"<b>Your Taste Profile</b>\n\n{reg.TasteProfileText}", ct).ConfigureAwait(false);
            return;
        }

        // ── Must be linked ────────────────────────────────────────────────────

        var link = config.TelegramUserLinks.FirstOrDefault(l => l.ChatId == chatId);
        if (link is null)
        {
            await SendMessageAsync(chatId,
                "Your Telegram account isn't linked to Jellyfin yet.\n\nSend /link to get started.",
                ct).ConfigureAwait(false);
            return;
        }

        // ── Agent dispatch ────────────────────────────────────────────────────

        var session = _sessions.GetOrAdd(chatId, _ => new ConversationSession { JellyfinUserId = link.JellyfinUserId });
        session.LastActivity = DateTime.UtcNow;

        // 90-second hard timeout per user turn — prevents the agent from hanging silently
        // if the LLM provider is slow or unresponsive.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            _logger.LogInformation("Telegram: dispatching to agent for user {UserId}", link.JellyfinUserId);

            // Keep the Telegram "typing..." indicator alive every 4 s while the agent works.
            // Telegram auto-clears it after 5 s, so we refresh before it expires.
            using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () =>
            {
                while (!typingCts.Token.IsCancellationRequested)
                {
                    await SendChatActionAsync(chatId, typingCts.Token).ConfigureAwait(false);
                    try { await Task.Delay(4000, typingCts.Token).ConfigureAwait(false); }
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
                    async status => await SendMessageAsync(chatId, status, ct).ConfigureAwait(false),
                    turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                typingCts.Cancel();
                _logger.LogWarning("TelegramBotService: agent timed out (90 s) for chat {ChatId}", chatId);
                await SendMessageAsync(chatId, "The AI took too long to respond — please try again.", ct)
                    .ConfigureAwait(false);
                return;
            }

            typingCts.Cancel();

            // Trim history window
            while (session.History.Count > MaxHistoryMessages)
                session.History.RemoveAt(0);

            _logger.LogInformation("Telegram: sending reply ({Len} chars) to chat {ChatId}", reply.Length, chatId);
            await SendMessageAsync(chatId, reply, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TelegramBotService: agent error for chat {ChatId}", chatId);
            await SendMessageAsync(chatId, "Something went wrong. Please try again.", ct).ConfigureAwait(false);
        }
    }

    // ── Telegram update parsing ───────────────────────────────────────────────

    private static List<TgUpdate> ParseUpdates(string json)
    {
        var list = new List<TgUpdate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return list;
            if (!doc.RootElement.TryGetProperty("result", out var results)) return list;

            foreach (var el in results.EnumerateArray())
            {
                var updateId = el.TryGetProperty("update_id", out var uid) ? uid.GetInt32() : 0;
                if (!el.TryGetProperty("message", out var msgEl)) continue;

                var chatId = msgEl.TryGetProperty("chat", out var chat)
                    && chat.TryGetProperty("id", out var cid) ? cid.GetInt64() : 0;
                var msgText = msgEl.TryGetProperty("text", out var t) ? t.GetString() : null;

                TgFrom? from = null;
                if (msgEl.TryGetProperty("from", out var fromEl))
                {
                    from = new TgFrom
                    {
                        Id        = fromEl.TryGetProperty("id", out var fid) ? fid.GetInt64() : 0,
                        Username  = fromEl.TryGetProperty("username", out var un) ? un.GetString() : null,
                        FirstName = fromEl.TryGetProperty("first_name", out var fn) ? fn.GetString() : null
                    };
                }

                list.Add(new TgUpdate
                {
                    UpdateId = updateId,
                    Message  = new TgMessage
                    {
                        Chat = new TgChat { Id = chatId },
                        From = from,
                        Text = msgText
                    }
                });
            }
        }
        catch
        {
            // Swallow parse errors — malformed updates are dropped
        }
        return list;
    }
}
