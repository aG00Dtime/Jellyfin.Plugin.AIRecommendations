using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Temporarily disables NtfyNotifier's movie/series notifications via reflection so
/// the AI-stub library scan doesn't spam the user's ntfy channel.
/// NtfyNotifier has no per-library exclusion; the only knob is the global
/// EnableMovieNotifications / EnableSeriesNotifications flags on its config object.
/// We flip them to false around ValidateMediaLibrary and restore afterward.
/// </summary>
internal sealed class NtfyNotifierSuppressor : IDisposable
{
    // Try several known property names used across different Ntfy plugin versions
    private static readonly string[] NotificationFlags =
    [
        "EnableMovieNotifications",
        "EnableSeriesNotifications",
        "NotifyOnMovieAdd",
        "NotifyOnSeriesAdd",
        "MovieAdded",
        "SeriesAdded"
    ];

    private readonly object? _ntfyConfig;
    private readonly Dictionary<string, object?> _saved = new();
    private readonly ILogger _logger;

    private NtfyNotifierSuppressor(object? ntfyConfig, ILogger logger)
    {
        _ntfyConfig = ntfyConfig;
        _logger = logger;
    }

    /// <summary>
    /// Locates the NtfyNotifier plugin instance in the loaded assemblies, saves its
    /// current notification flags and sets them all to false. Returns a disposable
    /// that restores the flags when disposed.
    /// </summary>
    public static NtfyNotifierSuppressor Suppress(ILogger logger)
    {
        var ntfyConfig = FindNtfyConfig(logger);
        var suppressor = new NtfyNotifierSuppressor(ntfyConfig, logger);

        if (ntfyConfig is null)
        {
            return suppressor;
        }

        var configType = ntfyConfig.GetType();
        foreach (var flag in NotificationFlags)
        {
            var prop = configType.GetProperty(flag, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;

            suppressor._saved[flag] = prop.GetValue(ntfyConfig);
            try
            {
                prop.SetValue(ntfyConfig, false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "NtfyNotifierSuppressor: could not set {Flag}", flag);
            }
        }

        if (suppressor._saved.Count > 0)
        {
            logger.LogDebug(
                "NtfyNotifierSuppressor: suppressed NtfyNotifier notifications for library scan");
        }

        return suppressor;
    }

    public void Dispose()
    {
        if (_ntfyConfig is null || _saved.Count == 0) return;

        var configType = _ntfyConfig.GetType();
        foreach (var (flag, value) in _saved)
        {
            var prop = configType.GetProperty(flag, BindingFlags.Public | BindingFlags.Instance);
            try
            {
                prop?.SetValue(_ntfyConfig, value);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NtfyNotifierSuppressor: could not restore {Flag}", flag);
            }
        }

        _logger.LogDebug("NtfyNotifierSuppressor: restored NtfyNotifier notification settings");
    }

    private static object? FindNtfyConfig(ILogger logger)
    {
        try
        {
            // Look for the NtfyNotifier plugin type across all loaded assemblies
            var ntfyPluginType = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .FirstOrDefault(t =>
                    t.Name == "Plugin"
                    && t.Namespace?.IndexOf("Ntfy", StringComparison.OrdinalIgnoreCase) >= 0);

            if (ntfyPluginType is null)
            {
                logger.LogDebug("NtfyNotifierSuppressor: no Ntfy plugin found in loaded assemblies");
                return null;
            }

            logger.LogDebug(
                "NtfyNotifierSuppressor: found Ntfy plugin type {Type}",
                ntfyPluginType.FullName);

            // BasePlugin<TConfiguration> exposes a static Instance property
            var instanceProp = ntfyPluginType.GetProperty(
                "Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            if (instance is null)
            {
                return null;
            }

            var configProp = instance.GetType()
                .GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);

            return configProp?.GetValue(instance);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "NtfyNotifierSuppressor: failed to locate NtfyNotifier plugin");
            return null;
        }
    }
}
