using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Tasks;

/// <summary>
/// Scheduled task to refresh AI recommendations for all users.
/// </summary>
public class RecommendationSyncTask : IScheduledTask
{
    private readonly Services.RecommendationSyncService _syncService;
    private readonly ILogger<RecommendationSyncTask> _logger;

    public RecommendationSyncTask(
        Services.RecommendationSyncService syncService,
        ILogger<RecommendationSyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AI Recommendations Sync";

    /// <inheritdoc />
    public string Key => "AIRecommendationsSync";

    /// <inheritdoc />
    public string Description => "Generate per-user AI recommendations and sync to virtual libraries.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting AI recommendations sync");
        await _syncService.SyncAllUsersAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Plugin.Instance?.Configuration.SyncIntervalHours ?? 24;
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger,
                MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
            }
        ];
    }
}
