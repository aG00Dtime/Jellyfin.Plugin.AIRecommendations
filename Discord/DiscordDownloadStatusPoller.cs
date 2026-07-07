using Jellyfin.Plugin.AIRecommendations.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Discord;

/// <summary>
/// Periodically polls download services (Jellyseerr, Radarr, Sonarr) and sends a
/// Discord DM notification when a requested title becomes available in Jellyfin.
/// No-ops if no Discord token or download services are configured.
/// </summary>
public sealed class DiscordDownloadStatusPoller : IHostedService
{
    private readonly ArrRequestService _arr;
    private readonly DiscordBotService _bot;
    private readonly LibraryFilterService _libraryFilter;
    private readonly ILogger<DiscordDownloadStatusPoller> _logger;
    private Timer? _timer;

    public DiscordDownloadStatusPoller(
        ArrRequestService arr,
        DiscordBotService bot,
        LibraryFilterService libraryFilter,
        ILogger<DiscordDownloadStatusPoller> logger)
    {
        _arr = arr;
        _bot = bot;
        _libraryFilter = libraryFilter;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var minutes = Plugin.Instance?.Configuration.TelegramDownloadPollIntervalMinutes ?? 15;
        _timer = new Timer(OnTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(minutes));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void OnTick(object? state) => _ = Task.Run(() => PollAsync(CancellationToken.None));

    private async Task PollAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.DiscordBotToken)) return;

        var hasDownloadService =
            !string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl) ||
            !string.IsNullOrWhiteSpace(config.RadarrBaseUrl) ||
            !string.IsNullOrWhiteSpace(config.SonarrBaseUrl);

        if (!hasDownloadService) return;
        if (config.DiscordUserLinks.Count == 0) return;

        var changed = false;

        foreach (var link in config.DiscordUserLinks)
        {
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == link.JellyfinUserId);
            if (reg is null) continue;

            foreach (var tmdbId in reg.RequestedTmdbIds.ToList())
            {
                if (link.NotifiedAvailableTmdbIds.Contains(tmdbId)) continue;

                try
                {
                    using var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var (available, title) = await CheckAvailableAsync(config, tmdbId, checkCts.Token)
                        .ConfigureAwait(false);
                    if (!available) continue;

                    var displayTitle = string.IsNullOrWhiteSpace(title) ? $"TMDB #{tmdbId}" : title;
                    await _bot.SendDmAsync(
                        link.DiscordUserId,
                        $"✅ **{displayTitle}** is now available in Jellyfin!",
                        ct).ConfigureAwait(false);

                    link.NotifiedAvailableTmdbIds.Add(tmdbId);
                    changed = true;

                    _logger.LogInformation(
                        "Notified Discord user {UserId}: '{Title}' (TMDB {TmdbId}) is available",
                        link.DiscordUserId, displayTitle, tmdbId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("DiscordDownloadStatusPoller: check timed out for TMDB {TmdbId}", tmdbId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DiscordDownloadStatusPoller: error checking TMDB {TmdbId}", tmdbId);
                }
            }
        }

        if (changed)
            Plugin.Instance!.SaveConfiguration();
    }

    private async Task<(bool Available, string? Title)> CheckAvailableAsync(
        Configuration.PluginConfiguration config,
        int tmdbId,
        CancellationToken ct)
    {
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        if (!ownedIds.Contains(tmdbId)) return (false, null);

        string? title = null;

        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            var (movieCode, _, movieTitle) = await _arr
                .CheckJellyseerrStatusAsync(tmdbId, isSeries: false, ct).ConfigureAwait(false);
            if (movieCode > 0 && movieTitle is not null)
            {
                title = movieTitle;
            }
            else
            {
                var (tvCode, _, tvTitle) = await _arr
                    .CheckJellyseerrStatusAsync(tmdbId, isSeries: true, ct).ConfigureAwait(false);
                if (tvCode > 0 && tvTitle is not null) title = tvTitle;
            }
        }

        if (title is null && !string.IsNullOrWhiteSpace(config.RadarrBaseUrl))
        {
            var (_, _, radarrTitle) = await _arr.CheckRadarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            if (radarrTitle is not null) title = radarrTitle;
        }

        if (title is null && !string.IsNullOrWhiteSpace(config.SonarrBaseUrl))
        {
            var (_, _, sonarrTitle) = await _arr.CheckSonarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            if (sonarrTitle is not null) title = sonarrTitle;
        }

        return (true, title);
    }
}
