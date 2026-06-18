using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AIRecommendations.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Coordinates provisioning, generation, and virtual file sync for all users.
/// </summary>
public class RecommendationSyncService
{
    private readonly VirtualLibraryManager _virtualLibraryManager;
    private readonly RecommendationEngine _engine;
    private readonly VirtualItemWriter _itemWriter;
    private readonly IUserManager _userManager;
    private readonly ILogger<RecommendationSyncService> _logger;

    public RecommendationSyncService(
        VirtualLibraryManager virtualLibraryManager,
        RecommendationEngine engine,
        VirtualItemWriter itemWriter,
        IUserManager userManager,
        ILogger<RecommendationSyncService> logger)
    {
        _virtualLibraryManager = virtualLibraryManager;
        _engine = engine;
        _itemWriter = itemWriter;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task SyncAllUsersAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var users = _userManager.GetUsers()
            .Where(u => !u.HasPermission(PermissionKind.IsDisabled))
            .ToList();

        if (users.Count == 0)
        {
            UpdateStatus("No users to sync.");
            return;
        }

        await _virtualLibraryManager.ProvisionAllUsersAsync(cancellationToken).ConfigureAwait(false);

        var config = Plugin.Instance!.Configuration;
        var completed = 0;

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await SyncUserAsync(user, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync recommendations for user {User}", user.Username);
            }

            completed++;
            progress?.Report(100d * completed / users.Count);
        }

        UpdateStatus($"Synced {completed} user(s) at {DateTime.UtcNow:u}");
    }

    public async Task SyncUserAsync(User user, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var registration = await _virtualLibraryManager.EnsureUserLibrariesAsync(user, cancellationToken)
            .ConfigureAwait(false);

        var all = await _engine.GenerateForUserAsync(user, cancellationToken).ConfigureAwait(false);
        var movies = all.Where(r => !r.IsSeries).Take(config.MaxRecommendationsPerType).ToList();
        var shows = all.Where(r => r.IsSeries).Take(config.MaxRecommendationsPerType).ToList();

        _itemWriter.SyncRecommendations(
            registration.MoviePath,
            registration.ShowPath,
            movies.Concat(shows).ToList(),
            config.LimitShowsToSeasonOne);

        _logger.LogInformation(
            "Synced {Movies} movies and {Shows} shows for {User}",
            movies.Count,
            shows.Count,
            user.Username);
    }

    public PluginStatusDto GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        return new PluginStatusDto
        {
            ActiveProvider = config.ActiveProvider,
            LastSyncUtc = config.LastSyncUtc,
            LastSyncMessage = config.LastSyncMessage,
            RegisteredUsers = config.UserLibraries.Count
        };
    }

    private void UpdateStatus(string message)
    {
        var config = Plugin.Instance!.Configuration;
        config.LastSyncUtc = DateTime.UtcNow;
        config.LastSyncMessage = message;
        Plugin.Instance.SaveConfiguration();
        _logger.LogInformation("{Message}", message);
    }
}
