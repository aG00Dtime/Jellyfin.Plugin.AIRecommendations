namespace Jellyfin.Plugin.AIRecommendations.Telegram;

/// <summary>Persisted link between a Telegram chat and a Jellyfin user.</summary>
public sealed class TelegramUserLink
{
    public long ChatId { get; set; }

    public string JellyfinUserId { get; set; } = string.Empty;

    public string? TelegramUsername { get; set; }

    public DateTime LinkedAt { get; set; }

    /// <summary>TMDB IDs for which an availability notification has already been sent.</summary>
    public List<int> NotifiedAvailableTmdbIds { get; set; } = new();
}

/// <summary>In-memory pending link code — never persisted, intentionally lost on restart.</summary>
public sealed record PendingLinkCode(string Code, long ChatId, DateTime ExpiresAt, string? Username);

/// <summary>One turn in a conversation. Role is "user", "assistant", or "tool".</summary>
public sealed record ConversationMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolCallsJson = null);

/// <summary>Per-chat conversation session held in memory.</summary>
public sealed class ConversationSession
{
    public string JellyfinUserId { get; init; } = string.Empty;
    public List<ConversationMessage> History { get; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

// ── Minimal Telegram Bot API DTOs (only what we actually use) ──────────────

public sealed class TgUpdate
{
    public int UpdateId { get; set; }
    public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    public TgChat Chat { get; set; } = null!;
    public TgFrom? From { get; set; }
    public string? Text { get; set; }
}

public sealed class TgChat
{
    public long Id { get; set; }
}

public sealed class TgFrom
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
}
