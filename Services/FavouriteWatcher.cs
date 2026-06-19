using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AIRecommendations.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Watches for favourite toggles on AI recommendation stubs and immediately
/// submits a Jellyseerr request — no need to wait for the next scheduled sync.
/// </summary>
public class FavouriteWatcher : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly JellyseerrService _jellyseerr;
    private readonly ILogger<FavouriteWatcher> _logger;

    public FavouriteWatcher(
        IUserDataManager userDataManager,
        IUserManager userManager,
        JellyseerrService jellyseerr,
        ILogger<FavouriteWatcher> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _jellyseerr = jellyseerr;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // ❤️ Favourite → submit Jellyseerr request
        if (e.SaveReason == UserDataSaveReason.UpdateUserRating && e.UserData.IsFavorite)
        {
            var user = _userManager.GetUserById(e.UserId);
            if (user is not null)
            {
                _ = Task.Run(() => HandleFavouriteAsync(user, e.Item, CancellationToken.None));
            }

            return;
        }

        // ✅ Mark as watched → permanently dismiss
        if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData.Played)
        {
            var user = _userManager.GetUserById(e.UserId);
            if (user is not null)
            {
                _ = Task.Run(() => HandlePlayedAsync(user, e.Item));
            }
        }
    }

    private Task HandlePlayedAsync(User user, BaseItem item)
    {
        try
        {
            var config = Plugin.Instance!.Configuration;
            var userKey = user.Id.ToString("N");
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == userKey);
            if (reg is null)
            {
                return Task.CompletedTask;
            }

            var itemPath = item.Path ?? string.Empty;
            var inMovieLib = itemPath.StartsWith(reg.MoviePath, StringComparison.OrdinalIgnoreCase);
            var inShowLib = itemPath.StartsWith(reg.ShowPath, StringComparison.OrdinalIgnoreCase);

            if (!inMovieLib && !inShowLib)
            {
                return Task.CompletedTask;
            }

            // For show stubs the played item may be an Episode whose TMDB provider ID
            // is the episode's own ID, not the show's. Always pull the show TMDB ID
            // from the show folder name (first component under ShowPath).
            // For movies the item's own TMDB ID is correct.
            int tmdbId;
            if (inShowLib)
            {
                var resolved = ResolveShowTmdbIdFromPath(itemPath, reg.ShowPath);
                if (resolved is null)
                {
                    return Task.CompletedTask;
                }

                tmdbId = resolved.Value;
            }
            else
            {
                if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr)
                    || !int.TryParse(tmdbIdStr, out tmdbId))
                {
                    var fromFile = VirtualItemWriter.ParseTmdbId(
                        Path.GetFileNameWithoutExtension(itemPath));
                    var fromFolder = VirtualItemWriter.ParseTmdbId(
                        Path.GetFileName(Path.GetDirectoryName(itemPath) ?? string.Empty));
                    var resolved = fromFile ?? fromFolder;
                    if (resolved is null)
                    {
                        return Task.CompletedTask;
                    }

                    tmdbId = resolved.Value;
                }
            }

            if (reg.RejectedTmdbIds.Contains(tmdbId))
            {
                return Task.CompletedTask;
            }

            _logger.LogInformation(
                "{User} marked \"{Title}\" as watched in AI library — dismissing permanently",
                user.Username, item.Name);

            reg.RejectedTmdbIds.Add(tmdbId);
            reg.PlacedTmdbIds.Remove(tmdbId);

            // Reset the item's played state BEFORE deleting the folder so Jellyfin stores
            // Played=false under this item's UserDataKey. If a future stub for the same
            // TMDB ID is ever created, it won't inherit the "already watched" state.
            try
            {
                var ud = _userDataManager.GetUserData(user, item);
                if (ud is not null && ud.Played)
                {
                    ud.Played = false;
                    ud.PlayCount = 0;
                    ud.LastPlayedDate = null;
                    _userDataManager.SaveUserData(user, item, ud, UserDataSaveReason.Import, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FavouriteWatcher: could not reset play state for \"{Title}\"", item.Name);
            }

            DeleteStubFolder(reg.MoviePath, tmdbId);
            DeleteStubFolder(reg.ShowPath, tmdbId);

            Plugin.Instance!.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserActionWatcher: failed to dismiss \"{Title}\"", item.Name);
        }

        return Task.CompletedTask;
    }

    private static void DeleteStubFolder(string path, int tmdbId)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(path))
        {
            if (VirtualItemWriter.ParseTmdbId(Path.GetFileName(dir)) == tmdbId)
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
        }
    }

    private async Task HandleFavouriteAsync(
        User user,
        BaseItem item,
        CancellationToken ct)
    {
        try
        {
            var config = Plugin.Instance!.Configuration;
            var userKey = user.Id.ToString("N");
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == userKey);
            if (reg is null)
            {
                return;
            }

            var itemPath = item.Path ?? string.Empty;
            var inMovieLib = itemPath.StartsWith(reg.MoviePath, StringComparison.OrdinalIgnoreCase);
            var inShowLib = itemPath.StartsWith(reg.ShowPath, StringComparison.OrdinalIgnoreCase);

            if (!inMovieLib && !inShowLib)
            {
                return;
            }

            // For show stubs the favourited item may be an Episode whose TMDB provider
            // ID is the episode's own ID, not the show's. Pull the show TMDB ID from
            // the show folder name (first component under ShowPath) to be safe.
            int tmdbId;
            if (inShowLib)
            {
                var resolved = ResolveShowTmdbIdFromPath(itemPath, reg.ShowPath);
                if (resolved is null)
                {
                    _logger.LogDebug(
                        "FavouriteWatcher: could not resolve TMDB ID for \"{Title}\" — skipping",
                        item.Name);
                    return;
                }

                tmdbId = resolved.Value;
            }
            else
            {
                if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr)
                    || !int.TryParse(tmdbIdStr, out tmdbId))
                {
                    var fromFile = VirtualItemWriter.ParseTmdbId(
                        Path.GetFileNameWithoutExtension(itemPath));
                    var fromFolder = VirtualItemWriter.ParseTmdbId(
                        Path.GetFileName(Path.GetDirectoryName(itemPath) ?? string.Empty));
                    var resolved = fromFile ?? fromFolder;
                    if (resolved is null)
                    {
                        _logger.LogDebug(
                            "FavouriteWatcher: could not resolve TMDB ID for \"{Title}\" — skipping",
                            item.Name);
                        return;
                    }

                    tmdbId = resolved.Value;
                }
            }

            // Always remove the heart from AI recommendation stubs so they never
            // linger in the user's Favourites section, regardless of whether a
            // Jellyseerr request is new, already queued, or partially failed.
            try
            {
                var userData = _userDataManager.GetUserData(user, item);
                if (userData is not null && userData.IsFavorite)
                {
                    userData.IsFavorite = false;
                    _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating, ct);
                }
            }
            catch (Exception unfavEx)
            {
                _logger.LogWarning(unfavEx, "FavouriteWatcher: could not un-favourite \"{Title}\"", item.Name);
            }

            if (reg.RequestedTmdbIds.Contains(tmdbId))
            {
                _logger.LogDebug(
                    "FavouriteWatcher: \"{Title}\" already requested — heart cleared, Jellyseerr skipped",
                    item.Name);
                return;
            }

            _logger.LogInformation(
                "{User} favourited \"{Title}\" in AI library — submitting Jellyseerr request immediately",
                user.Username, item.Name);

            var rec = new ResolvedRecommendation
            {
                TmdbId = tmdbId,
                IsSeries = inShowLib,
                Title = item.Name ?? string.Empty
            };

            var submitted = await _jellyseerr
                .RequestRecommendationsAsync([rec], ct)
                .ConfigureAwait(false);

            if (submitted > 0)
            {
                reg.RequestedTmdbIds.Add(tmdbId);
                Plugin.Instance!.SaveConfiguration();

                _logger.LogInformation(
                    "Jellyseerr request sent for \"{Title}\" (tmdb {Id}) on behalf of {User}",
                    item.Name, tmdbId, user.Username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FavouriteWatcher: Jellyseerr request failed for \"{Title}\"", item.Name);
        }
    }

    // Episode items carry the episode's own TMDB ID, not the show's.
    // The show's TMDB ID is encoded in the show folder name under ShowPath.
    private static int? ResolveShowTmdbIdFromPath(string itemPath, string showPath)
    {
        var relative = itemPath
            .Substring(showPath.Length)
            .TrimStart(Path.DirectorySeparatorChar, '/');
        var showFolderName = relative.Split([Path.DirectorySeparatorChar, '/'], 2)[0];
        return VirtualItemWriter.ParseTmdbId(showFolderName);
    }
}
