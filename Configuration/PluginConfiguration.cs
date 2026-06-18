using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AIRecommendations.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Active LLM provider: OpenAI, OpenRouter, or Ollama.
    /// </summary>
    public string ActiveProvider { get; set; } = "OpenAI";

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string OpenRouterApiKey { get; set; } = string.Empty;

    public string OpenRouterModel { get; set; } = "openai/gpt-4o-mini";

    /// <summary>
    /// Ollama deployment: Local or Cloud.
    /// </summary>
    public string OllamaDeployment { get; set; } = "Local";

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string OllamaApiKey { get; set; } = string.Empty;

    public string OllamaModel { get; set; } = "gemma3:27b";

    public string TmdbApiKey { get; set; } = string.Empty;

    public int MaxRecommendationsPerType { get; set; } = 10;

    public int MaxWatchedItems { get; set; } = 30;

    public int SyncIntervalHours { get; set; } = 24;

    public bool IncludeAdult { get; set; }

    public bool LimitShowsToSeasonOne { get; set; } = true;

    /// <summary>
    /// Jellyseerr base URL, e.g. http://jellyseerr:5055
    /// Leave blank to disable Jellyseerr integration.
    /// </summary>
    public string JellyseerrBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Jellyseerr API key (from your Jellyseerr profile → API Key).
    /// </summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// When true, each recommendation is automatically submitted as a
    /// Jellyseerr request during sync. Items already requested or
    /// available in Jellyseerr are skipped.
    /// </summary>
    public bool JellyseerrAutoRequest { get; set; }

    public List<UserLibraryRegistration> UserLibraries { get; set; } = new();

    public DateTime? LastSyncUtc { get; set; }

    public string LastSyncMessage { get; set; } = string.Empty;
}

/// <summary>
/// Tracks auto-provisioned libraries and user feedback for a single user.
/// </summary>
public class UserLibraryRegistration
{
    public string UserId { get; set; } = string.Empty;

    public Guid MovieLibraryId { get; set; }

    public Guid ShowLibraryId { get; set; }

    public string MoviePath { get; set; } = string.Empty;

    public string ShowPath { get; set; } = string.Empty;

    public string MovieLibraryName { get; set; } = string.Empty;

    public string ShowLibraryName { get; set; } = string.Empty;

    /// <summary>TMDB IDs the user thumbed-down — never recommend again.</summary>
    public List<int> RejectedTmdbIds { get; set; } = new();

    /// <summary>TMDB IDs already submitted to Jellyseerr — skip until actually owned.</summary>
    public List<int> RequestedTmdbIds { get; set; } = new();
}
