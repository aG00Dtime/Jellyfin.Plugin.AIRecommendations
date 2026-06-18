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

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string OpenRouterApiKey { get; set; } = string.Empty;

    public string OpenRouterModel { get; set; } = "openai/gpt-4o-mini";

    /// <summary>
    /// Ollama deployment: Local or Cloud.
    /// </summary>
    public string OllamaDeployment { get; set; } = "Local";

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string OllamaApiKey { get; set; } = string.Empty;

    public string OllamaModel { get; set; } = "llama3.2";

    public string TmdbApiKey { get; set; } = string.Empty;

    public int MaxRecommendationsPerType { get; set; } = 25;

    public int MaxWatchedItems { get; set; } = 40;

    public int SyncIntervalHours { get; set; } = 24;

    public bool IncludeAdult { get; set; }

    public bool LimitShowsToSeasonOne { get; set; } = true;

    public List<UserLibraryRegistration> UserLibraries { get; set; } = new();

    public DateTime? LastSyncUtc { get; set; }

    public string LastSyncMessage { get; set; } = string.Empty;
}

/// <summary>
/// Tracks auto-provisioned libraries for a user.
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
}
