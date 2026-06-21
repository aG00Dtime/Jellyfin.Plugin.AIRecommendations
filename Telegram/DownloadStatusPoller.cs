using Jellyfin.Plugin.AIRecommendations.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Telegram;

/// <summary>
/// Periodically polls all configured download services (Jellyseerr, Radarr, Sonarr)
/// and sends a Telegram notification when a requested title becomes available.
/// No-ops completely if the bot token or all download services are unconfigured.
/// </summary>
public sealed class DownloadStatusPoller : IHostedService
{
    private readonly ArrRequestService _arr;
    private readonly TelegramBotService _bot;
    private readonly ILogger<DownloadStatusPoller> _logger;
    private Timer? _timer;

    public DownloadStatusPoller(
        ArrRequestService arr,
        TelegramBotService bot,
        ILogger<DownloadStatusPoller> logger)
    {
        _arr    = arr;
        _bot    = bot;
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
                    var (available, title) = await CheckAvailableAsync(config, tmdbId, ct).ConfigureAwait(false);
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
        // Jellyseerr: try movie, then TV (we don't know the type from the TMDB ID alone)
        if (!string.IsNullOrWhiteSpace(config.JellyseerrBaseUrl))
        {
            var (movieCode, _, movieTitle) = await _arr
                .CheckJellyseerrStatusAsync(tmdbId, isSeries: false, ct).ConfigureAwait(false);
            if (movieCode == 5) return (true, movieTitle);

            var (tvCode, _, tvTitle) = await _arr
                .CheckJellyseerrStatusAsync(tmdbId, isSeries: true, ct).ConfigureAwait(false);
            if (tvCode == 5) return (true, tvTitle);
        }

        // Radarr (movies)
        if (!string.IsNullOrWhiteSpace(config.RadarrBaseUrl))
        {
            var (exists, hasFile, radarrTitle) = await _arr
                .CheckRadarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            if (exists && hasFile) return (true, radarrTitle);
        }

        // Sonarr (TV shows, 100% downloaded)
        if (!string.IsNullOrWhiteSpace(config.SonarrBaseUrl))
        {
            var (exists, pct, sonarrTitle) = await _arr
                .CheckSonarrStatusAsync(tmdbId, ct).ConfigureAwait(false);
            if (exists && pct >= 100) return (true, sonarrTitle);
        }

        return (false, null);
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
