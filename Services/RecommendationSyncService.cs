using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
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
    private readonly JellyseerrService _jellyseerr;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryFilterService _libraryFilter;
    private readonly ILogger<RecommendationSyncService> _logger;

    public RecommendationSyncService(
        VirtualLibraryManager virtualLibraryManager,
        RecommendationEngine engine,
        VirtualItemWriter itemWriter,
        JellyseerrService jellyseerr,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        LibraryFilterService libraryFilter,
        ILogger<RecommendationSyncService> logger)
    {
        _virtualLibraryManager = virtualLibraryManager;
        _engine = engine;
        _itemWriter = itemWriter;
        _jellyseerr = jellyseerr;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _libraryFilter = libraryFilter;
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

        _logger.LogInformation("Triggering library scan to surface new AI recommendations...");
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SyncUserAsync(User user, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var registration = await _virtualLibraryManager.EnsureUserLibrariesAsync(user, cancellationToken)
            .ConfigureAwait(false);

        // In always-refresh mode, clear PlacedTmdbIds first so DetectAndRejectDeletedStubs
        // doesn't mistake the programmatic wipe for user deletions.
        if (config.AlwaysRefreshRecommendations)
        {
            registration.PlacedTmdbIds.Clear();
        }

        // Detect stubs the user deleted via Jellyfin's native delete — permanently reject them
        DetectAndRejectDeletedStubs(user, registration);

        // Process ❤️ feedback the user left on existing stubs
        await ProcessUserFeedbackAsync(user, registration, cancellationToken).ConfigureAwait(false);
        Plugin.Instance!.SaveConfiguration();

        // Exclude rejected + already-requested IDs from new recommendations
        var extraExcludeIds = registration.RejectedTmdbIds
            .Concat(registration.RequestedTmdbIds)
            .ToHashSet();

        var all = await _engine.GenerateForUserAsync(user, extraExcludeIds, cancellationToken)
            .ConfigureAwait(false);

        // Always remove rejected + requested stubs; in refresh mode also wipe everything on disk
        var removeIds = new HashSet<int>(registration.RejectedTmdbIds.Concat(registration.RequestedTmdbIds));
        if (config.AlwaysRefreshRecommendations)
        {
            removeIds.UnionWith(VirtualItemWriter.ScanTmdbIds(registration.MoviePath));
            removeIds.UnionWith(VirtualItemWriter.ScanTmdbIds(registration.ShowPath));
        }

        // Remove stubs for content now present in the real library (e.g. a download completed)
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        var nowOwned = registration.PlacedTmdbIds.Where(id => ownedIds.Contains(id)).ToList();
        if (nowOwned.Count > 0)
        {
            _logger.LogInformation(
                "Removing {Count} stub(s) for {User} — content now owned in library",
                nowOwned.Count, user.Username);
            removeIds.UnionWith(nowOwned);
        }

        var pending = all
            .Where(r => !registration.RequestedTmdbIds.Contains(r.TmdbId))
            .ToList();

        var placedIds = _itemWriter.SyncRecommendations(
            registration.MoviePath,
            registration.ShowPath,
            pending,
            removeIds,
            config.LimitShowsToSeasonOne);

        registration.PlacedTmdbIds = placedIds.ToList();
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation(
            "Synced {Total} stubs for {User} ({Movies} movies, {Shows} shows)",
            placedIds.Count,
            user.Username,
            VirtualItemWriter.ScanTmdbIds(registration.MoviePath).Count,
            VirtualItemWriter.ScanTmdbIds(registration.ShowPath).Count);
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

    /// <summary>
    /// Compares PlacedTmdbIds against stubs currently on disk.
    /// Anything that was placed but has since been deleted by the user is added
    /// to the permanent reject list so it never appears again.
    /// </summary>
    private void DetectAndRejectDeletedStubs(User user, Configuration.UserLibraryRegistration registration)
    {
        if (registration.PlacedTmdbIds.Count == 0)
        {
            return;
        }

        var onDisk = VirtualItemWriter.ScanTmdbIds(registration.MoviePath);
        onDisk.UnionWith(VirtualItemWriter.ScanTmdbIds(registration.ShowPath));

        var knownRemoved = new HashSet<int>(
            registration.RejectedTmdbIds.Concat(registration.RequestedTmdbIds));

        var deletedByUser = registration.PlacedTmdbIds
            .Where(id => !onDisk.Contains(id) && !knownRemoved.Contains(id))
            .ToList();

        if (deletedByUser.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "{User} deleted {Count} stub(s) via Jellyfin — adding to permanent reject list",
            user.Username, deletedByUser.Count);

        foreach (var id in deletedByUser)
        {
            registration.RejectedTmdbIds.Add(id);
        }
    }

    /// <summary>
    /// Reads ❤️ (favourite) and 👎 (dislike) signals from current stubs.
    /// Favourited items are submitted to Jellyseerr (if configured) and recorded as requested.
    /// Disliked items are added to the permanent reject list.
    /// </summary>
    private async Task ProcessUserFeedbackAsync(
        User user,
        Configuration.UserLibraryRegistration registration,
        CancellationToken cancellationToken)
    {
        await ProcessLibraryFeedbackAsync(
            user, registration, registration.MovieLibraryId,
            [BaseItemKind.Movie], isSeries: false, cancellationToken)
            .ConfigureAwait(false);

        await ProcessLibraryFeedbackAsync(
            user, registration, registration.ShowLibraryId,
            [BaseItemKind.Series], isSeries: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessLibraryFeedbackAsync(
        User user,
        Configuration.UserLibraryRegistration registration,
        Guid libraryId,
        BaseItemKind[] itemTypes,
        bool isSeries,
        CancellationToken cancellationToken)
    {
        if (libraryId == Guid.Empty)
        {
            return;
        }

        var stubs = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = libraryId,
            IncludeItemTypes = itemTypes,
            Recursive = true
        });

        var toRequest = new List<ResolvedRecommendation>();

        foreach (var stub in stubs)
        {
            if (!stub.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr)
                || !int.TryParse(tmdbIdStr, out var tmdbId))
            {
                continue;
            }

            var userData = _userDataManager.GetUserData(user, stub);
            if (userData is null) continue;

            if (userData.IsFavorite && !registration.RequestedTmdbIds.Contains(tmdbId))
            {
                _logger.LogInformation(
                    "{User} favourited \"{Title}\" — queuing Jellyseerr request",
                    user.Username, stub.Name);

                toRequest.Add(new ResolvedRecommendation
                {
                    TmdbId = tmdbId,
                    IsSeries = isSeries,
                    Title = stub.Name ?? string.Empty
                });

                registration.RequestedTmdbIds.Add(tmdbId);
            }
            else if (userData.Likes == false && !registration.RejectedTmdbIds.Contains(tmdbId))
            {
                _logger.LogInformation(
                    "{User} disliked \"{Title}\" — adding to permanent reject list",
                    user.Username, stub.Name);

                registration.RejectedTmdbIds.Add(tmdbId);
            }
        }

        if (toRequest.Count > 0)
        {
            var config = Plugin.Instance!.Configuration;
            if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl)
                && !string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
            {
                var submitted = await _jellyseerr
                    .RequestRecommendationsAsync(toRequest, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Submitted {Count} Jellyseerr request(s) for {User}",
                    submitted, user.Username);
            }
            else
            {
                _logger.LogWarning(
                    "{User} favourited {Count} item(s) but Jellyseerr is not configured — set JellyseerrBaseUrl and JellyseerrApiKey",
                    user.Username, toRequest.Count);
            }
        }
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
