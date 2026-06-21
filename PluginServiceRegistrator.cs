using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Providers;
using Jellyfin.Plugin.AIRecommendations.Services;
using Jellyfin.Plugin.AIRecommendations.Tasks;
using Jellyfin.Plugin.AIRecommendations.Telegram;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AIRecommendations;

/// <summary>
/// Registers plugin services with DI.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient(nameof(OpenAiProvider))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(nameof(OpenRouterProvider))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(nameof(OllamaProvider))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(nameof(TmdbMetadataService));
        services.AddHttpClient(nameof(JellyseerrService));

        // Telegram / Arr HTTP clients
        services.AddHttpClient("TelegramBot")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(35)); // > poll timeout of 25 s
        services.AddHttpClient("TelegramAgent")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddHttpClient("ArrService")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("TasteProfile")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(3));

        services.AddSingleton<WatchHistoryService>();
        services.AddSingleton<LibraryFilterService>();
        services.AddSingleton<OpenAiProvider>();
        services.AddSingleton<OpenRouterProvider>();
        services.AddSingleton<OllamaProvider>();
        services.AddSingleton<LlmProviderFactory>();
        services.AddSingleton<TmdbMetadataService>();
        services.AddSingleton<RecommendationEngine>();
        services.AddSingleton<TasteProfileService>();
        services.AddSingleton<VirtualItemWriter>();
        services.AddSingleton<LibraryPermissionManager>();
        services.AddSingleton<VirtualLibraryManager>();
        services.AddSingleton<JellyseerrService>();
        services.AddSingleton<RecommendationSyncService>();
        services.AddSingleton<RecommendationSyncTask>();
        services.AddHostedService<PluginStartupService>();
        services.AddHostedService<FavouriteWatcher>();

        // Telegram + Arr services
        services.AddSingleton<ArrRequestService>();
        services.AddSingleton<TelegramAgentLoop>();
        // Register as singleton so the controller can inject it, and also wire it up as IHostedService
        services.AddSingleton<TelegramBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
        services.AddSingleton<DownloadStatusPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<DownloadStatusPoller>());
    }
}
