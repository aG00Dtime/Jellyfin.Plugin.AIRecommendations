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

    public async Task SendMessageAsync(long chatId, string html, CancellationToken ct = default)
    {
        var token = Plugin.Instance?.Configuration.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token)) return;

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

        try
        {
            var reply = await _agent.RunAsync(link.JellyfinUserId, text, session.History, ct)
                .ConfigureAwait(false);

            // Trim history window
            while (session.History.Count > MaxHistoryMessages)
                session.History.RemoveAt(0);

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
