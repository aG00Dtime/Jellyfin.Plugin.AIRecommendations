using Jellyfin.Plugin.AIRecommendations.Metadata;
using Jellyfin.Plugin.AIRecommendations.Providers;
using Jellyfin.Plugin.AIRecommendations.Services;
using Jellyfin.Plugin.AIRecommendations.Tasks;
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
        services.AddHttpClient(nameof(OpenAiProvider));
        services.AddHttpClient(nameof(OpenRouterProvider));
        services.AddHttpClient(nameof(OllamaProvider));
        services.AddHttpClient(nameof(TmdbMetadataService));

        services.AddSingleton<WatchHistoryService>();
        services.AddSingleton<LibraryFilterService>();
        services.AddSingleton<OpenAiProvider>();
        services.AddSingleton<OpenRouterProvider>();
        services.AddSingleton<OllamaProvider>();
        services.AddSingleton<LlmProviderFactory>();
        services.AddSingleton<TmdbMetadataService>();
        services.AddSingleton<RecommendationEngine>();
        services.AddSingleton<VirtualItemWriter>();
        services.AddSingleton<LibraryPermissionManager>();
        services.AddSingleton<VirtualLibraryManager>();
        services.AddSingleton<RecommendationSyncService>();
        services.AddSingleton<RecommendationSyncTask>();
        services.AddHostedService<PluginStartupService>();
    }
}
