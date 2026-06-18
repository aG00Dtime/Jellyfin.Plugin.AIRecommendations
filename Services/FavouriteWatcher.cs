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
        // UpdateUserRating fires for both favourite and like/dislike toggles
        if (e.SaveReason != UserDataSaveReason.UpdateUserRating)
        {
            return;
        }

        if (!e.UserData.IsFavorite)
        {
            return;
        }

        var user = _userManager.GetUserById(e.UserId);
        if (user is null)
        {
            return;
        }

        // Fire-and-forget — don't block the event dispatcher
        _ = Task.Run(() => HandleFavouriteAsync(user.Username, user.Id, e.Item, CancellationToken.None));
    }

    private async Task HandleFavouriteAsync(
        string username,
        Guid userId,
        BaseItem item,
        CancellationToken ct)
    {
        try
        {
            var config = Plugin.Instance!.Configuration;
            var userKey = userId.ToString("N");
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

            // Resolve TMDB ID: try the item's provider data first, then parse from the path
            if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr)
                || !int.TryParse(tmdbIdStr, out var tmdbId))
            {
                // Episode STRM filename contains [tmdbid-N] for the show
                var fromFile = VirtualItemWriter.ParseTmdbId(
                    Path.GetFileNameWithoutExtension(itemPath));

                // Show/movie folder name also contains [tmdbid-N]
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

            if (reg.RequestedTmdbIds.Contains(tmdbId))
            {
                return;
            }

            _logger.LogInformation(
                "{User} favourited \"{Title}\" in AI library — submitting Jellyseerr request immediately",
                username, item.Name);

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
                    item.Name, tmdbId, username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FavouriteWatcher: Jellyseerr request failed for \"{Title}\"", item.Name);
        }
    }
}
