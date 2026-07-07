namespace Jellyfin.Plugin.AIRecommendations.Discord;

/// <summary>Persisted link between a Discord user and a Jellyfin user.</summary>
public sealed class DiscordUserLink
{
    public ulong DiscordUserId { get; set; }

    public string JellyfinUserId { get; set; } = string.Empty;

    public string? DiscordUsername { get; set; }

    public DateTime LinkedAt { get; set; }

    /// <summary>TMDB IDs for which an availability notification has already been sent.</summary>
    public List<int> NotifiedAvailableTmdbIds { get; set; } = new();
}

/// <summary>In-memory pending link code — never persisted, intentionally lost on restart.</summary>
public sealed record PendingDiscordLinkCode(string Code, ulong UserId, DateTime ExpiresAt, string? Username);
