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
    private readonly TasteProfileService _tasteProfile;
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
        TasteProfileService tasteProfile,
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
        _tasteProfile = tasteProfile;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _libraryFilter = libraryFilter;
        _logger = logger;
    }

    public async Task SyncAllUsersAsync(IProgress<double>? progress, CancellationToken cancellationToken, bool force = false)
    {
        var config = Plugin.Instance!.Configuration;

        // Guard: if the sync interval hasn't elapsed since the last successful run, skip entirely.
        // This prevents the task from regenerating recommendations on every Jellyfin restart when
        // the startup trigger is still configured (e.g. from a previous install).
        if (!force && config.LastSyncUtc.HasValue)
        {
            var hoursSinceLast = (DateTime.UtcNow - config.LastSyncUtc.Value).TotalHours;
            if (hoursSinceLast < config.SyncIntervalHours)
            {
                _logger.LogInformation(
                    "AI Recommendations: last sync was {Hours:F1}h ago (interval {Interval}h) — skipping. Use manual sync to force.",
                    hoursSinceLast, config.SyncIntervalHours);
                progress?.Report(100);
                return;
            }
        }

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
                await SyncUserAsync(user, cancellationToken, force).ConfigureAwait(false);
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
        using (NtfyNotifierSuppressor.Suppress(_logger))
        {
            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken)
                .ConfigureAwait(false);
        }

        // Post-scan pass: virtual episodes for brand-new stubs are created by the library scan
        // above, not by writing stub files. The per-user cleanup inside SyncUserAsync cannot see
        // them yet, so we run a second pass here now that they exist in Jellyfin's database.
        var pluginConfig = Plugin.Instance!.Configuration;
        foreach (var user in users)
        {
            var userKey = user.Id.ToString("N");
            var reg = pluginConfig.UserLibraries.FirstOrDefault(r => r.UserId == userKey);
            if (reg is not null)
            {
                ClearStubShowEpisodePlayedStates(user, reg);
            }
        }
    }

    public async Task SyncUserAsync(User user, CancellationToken cancellationToken, bool force = false)
    {
        var config = Plugin.Instance!.Configuration;
        var registration = await _virtualLibraryManager.EnsureUserLibrariesAsync(user, cancellationToken)
            .ConfigureAwait(false);

        // Clear played states on virtual episodes inside AI show stubs.
        // Jellyfin auto-generates virtual episode entries (keyed by TMDB episode ID) for any
        // show that has a TMDB ID in its tvshow.nfo. Those virtual episodes inherit Played=true
        // from prior UserData — either from the user's real library or from stubs that were
        // auto-marked as watched by an earlier bug. The series-level played state is not
        // affected here (users need that mark to dismiss shows).
        ClearStubShowEpisodePlayedStates(user, registration);

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

        // Generate or refresh taste profile (skips if profile is fresh per the configured interval)
        if (await _tasteProfile.RefreshIfNeededAsync(user, registration, config, cancellationToken).ConfigureAwait(false))
            Plugin.Instance!.SaveConfiguration();

        // Exclude rejected + already-requested IDs from new recommendations.
        // In accumulate mode, also exclude stubs already on disk so the LLM generates
        // genuinely new titles rather than re-recommending what's already placed.
        var extraExcludeIds = registration.RejectedTmdbIds
            .Concat(registration.RequestedTmdbIds)
            .ToHashSet();

        if (!config.AlwaysRefreshRecommendations)
        {
            var onDiskIds = VirtualItemWriter.ScanTmdbIds(registration.MoviePath);
            onDiskIds.UnionWith(VirtualItemWriter.ScanTmdbIds(registration.ShowPath));
            extraExcludeIds.UnionWith(onDiskIds);
        }

        // Skip LLM generation if stubs already exist and the sync interval hasn't elapsed.
        // Check PlacedTmdbIds first; fall back to scanning actual disk files in case the config
        // list got cleared (e.g. deserialization issue or plugin reinstall).
        var hasExistingStubs = registration.PlacedTmdbIds.Count > 0
            || VirtualItemWriter.ScanTmdbIds(registration.MoviePath).Count > 0
            || VirtualItemWriter.ScanTmdbIds(registration.ShowPath).Count > 0;
        var syncAgeHours = config.LastSyncUtc.HasValue
            ? (DateTime.UtcNow - config.LastSyncUtc.Value).TotalHours
            : double.MaxValue;
        var shouldGenerate = force
            || config.AlwaysRefreshRecommendations
            || !hasExistingStubs
            || syncAgeHours >= config.SyncIntervalHours;

        IReadOnlyList<ResolvedRecommendation> all;
        if (shouldGenerate)
        {
            all = await _engine.GenerateForUserAsync(user, extraExcludeIds, cancellationToken,
                    string.IsNullOrWhiteSpace(registration.TasteProfileText) ? null : registration.TasteProfileText)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "Skipping recommendation generation for {User} — stubs exist and last sync was {Hours:F1}h ago (interval: {Interval}h)",
                user.Username, syncAgeHours, config.SyncIntervalHours);
            all = [];
        }

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

    /// <summary>
    /// Resets Played=true on virtual episodes inside the user's AI show stubs.
    /// Virtual episodes share TMDB episode IDs with any previously dismissed stub for the same
    /// show, so they inherit the old played state and appear as already-watched even when the
    /// stub is brand new. Only virtual episodes are touched — the S01E01.strm stub episode
    /// (non-virtual) retains its state so the dismiss-via-played mechanism still works.
    /// </summary>
    private void ClearStubShowEpisodePlayedStates(
        User user,
        Configuration.UserLibraryRegistration registration)
    {
        if (registration.ShowLibraryId == Guid.Empty)
        {
            return;
        }

        var seriesList = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = registration.ShowLibraryId,
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = false
        });

        var resetCount = 0;

        foreach (var series in seriesList)
        {
            var playedVirtualEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = series.Id,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsPlayed = true,
                IsVirtualItem = true,
                Recursive = true
            });

            foreach (var ep in playedVirtualEpisodes)
            {
                try
                {
                    var ud = _userDataManager.GetUserData(user, ep);
                    if (ud is not null && ud.Played)
                    {
                        ud.Played = false;
                        ud.PlayCount = 0;
                        ud.LastPlayedDate = null;
                        _userDataManager.SaveUserData(
                            user, ep, ud, UserDataSaveReason.Import, CancellationToken.None);
                        resetCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ClearStubShowEpisodePlayedStates: episode {Id}", ep.Id);
                }
            }
        }

        if (resetCount > 0)
        {
            _logger.LogInformation(
                "Reset {Count} virtual episode(s) to unplayed in {User}'s AI show stubs",
                resetCount, user.Username);
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
