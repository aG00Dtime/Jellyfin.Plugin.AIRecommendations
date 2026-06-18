using Jellyfin.Plugin.AIRecommendations.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations;

/// <summary>
/// Delays initial library provisioning until Jellyfin finishes starting.
/// </summary>
public class PluginStartupService : IHostedService
{
    private readonly RecommendationSyncService _syncService;
    private readonly ILogger<PluginStartupService> _logger;

    public PluginStartupService(
        RecommendationSyncService syncService,
        ILogger<PluginStartupService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("AI Recommendations: provisioning virtual libraries");
                await _syncService.SyncAllUsersAsync(null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI Recommendations startup sync failed");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
