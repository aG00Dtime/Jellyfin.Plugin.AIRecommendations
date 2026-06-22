using Jellyfin.Plugin.AIRecommendations.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Telegram;

/// <summary>
/// Periodically polls all configured download services (Jellyseerr, Radarr, Sonarr)
/// and sends a Telegram notification when a requested title becomes available.
/// Confirms availability against Jellyfin's own library before notifying.
/// No-ops completely if the bot token or all download services are unconfigured.
/// </summary>
public sealed class DownloadStatusPoller : IHostedService
{
    private readonly ArrRequestService _arr;
    private readonly TelegramBotService _bot;
    private readonly LibraryFilterService _libraryFilter;
    private readonly ILogger<DownloadStatusPoller> _logger;
    private Timer? _timer;
    public DownloadStatusPoller(
        ArrRequestService arr,
        TelegramBotService bot,
        LibraryFilterService libraryFilter,
        ILogger<DownloadStatusPoller> logger)
    {
        _arr           = arr;
        _bot           = bot;
        _libraryFilter = libraryFilter;
        _logger        = logger;
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
        if (config is null || string.IsNullOrWhiteSpace(config.TelegramBotToken)) return;

        var hasDownloadService =
            !string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl) ||
            !string.IsNullOrWhiteSpace(config.RadarrBaseUrl)     ||
            !string.IsNullOrWhiteSpace(config.SonarrBaseUrl);

        if (!hasDownloadService) return;
        if (config.TelegramUserLinks.Count == 0) return;

        var changed = false;

        foreach (var link in config.TelegramUserLinks)
        {
            var reg = config.UserLibraries.FirstOrDefault(r => r.UserId == link.JellyfinUserId);
            if (reg is null) continue;

            foreach (var tmdbId in reg.RequestedTmdbIds.ToList())
            {
                if (link.NotifiedAvailableTmdbIds.Contains(tmdbId)) continue;

                try
                {
                    // 30-second cap per item — prevents a hung Jellyseerr/Radarr/Sonarr
                    // from blocking the entire poll round with CancellationToken.None.
                    using var checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var (available, title) = await CheckAvailableAsync(config, tmdbId, checkCts.Token).ConfigureAwait(false);
                    if (!available) continue;

                    var displayTitle = string.IsNullOrWhiteSpace(title) ? $"TMDB #{tmdbId}" : title;
                    await _bot.SendMessageAsync(
                        link.ChatId,
                        $"✅ <b>{EscapeHtml(displayTitle)}</b> is now available in Jellyfin!",
                        ct).ConfigureAwait(false);

                    link.NotifiedAvailableTmdbIds.Add(tmdbId);
                    changed = true;

                    _logger.LogInformation(
                        "Notified Telegram {ChatId}: '{Title}' (TMDB {TmdbId}) is available",
                        link.ChatId, displayTitle, tmdbId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("DownloadStatusPoller: check timed out for TMDB {TmdbId}, skipping", tmdbId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DownloadStatusPoller: error checking TMDB {TmdbId}", tmdbId);
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
        // Ground truth: item must be in Jellyfin's real library (not just in a download service)
        var ownedIds = _libraryFilter.GetOwnedTmdbIds();
        if (!ownedIds.Contains(tmdbId))
            return (false, null);

        // It's in Jellyfin — retrieve the display title from the first download service that knows it
        string? title = null;

        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            var (movieCode, _, movieTitle) = await _arr
                .CheckJellyseerrStatusAsync(tmdbId, isSeries: false, ct).ConfigureAwait(false);
            if (movieCode > 0 && movieTitle is not null) { title = movieTitle; }
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

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
