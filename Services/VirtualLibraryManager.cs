using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AIRecommendations.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommendations.Services;

/// <summary>
/// Manages per-user library folder permissions.
/// </summary>
public class LibraryPermissionManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LibraryPermissionManager> _logger;

    public LibraryPermissionManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<LibraryPermissionManager> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task GrantUserAccessAsync(User user, Guid movieLibraryId, Guid showLibraryId, CancellationToken cancellationToken)
    {
        var dto = _userManager.GetUserDto(user);
        var policy = dto.Policy;

        var aiIds = new[] { movieLibraryId, showLibraryId };
        var enabled = policy.EnabledFolders?.ToList() ?? new List<Guid>();

        if (policy.EnableAllFolders)
        {
            var allFolderIds = _libraryManager.GetVirtualFolders()
                .Where(v => Guid.TryParse(v.ItemId, out _))
                .Select(v => Guid.Parse(v.ItemId))
                .ToList();

            policy.EnableAllFolders = false;
            enabled = allFolderIds;
        }

        foreach (var id in aiIds)
        {
            if (!enabled.Contains(id))
            {
                enabled.Add(id);
            }
        }

        policy.EnabledFolders = enabled.ToArray();
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        _logger.LogInformation("Granted AI library access to user {User}", user.Username);
    }
}

/// <summary>
/// Auto-provisions per-user virtual Jellyfin libraries.
/// </summary>
public class VirtualLibraryManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly LibraryPermissionManager _permissionManager;
    private readonly ILogger<VirtualLibraryManager> _logger;

    public VirtualLibraryManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        LibraryPermissionManager permissionManager,
        ILogger<VirtualLibraryManager> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _permissionManager = permissionManager;
        _logger = logger;
    }

    public string GetVirtualRoot()
    {
        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("Plugin not initialized");
        return Path.Combine(plugin.DataFolderPath, "virtual");
    }

    public async Task<UserLibraryRegistration> EnsureUserLibrariesAsync(User user, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var userKey = user.Id.ToString("N");

        var existing = config.UserLibraries.FirstOrDefault(r => r.UserId == userKey);
        if (existing is not null
            && Directory.Exists(existing.MoviePath)
            && Directory.Exists(existing.ShowPath))
        {
            return existing;
        }

        var root = GetVirtualRoot();
        var moviePath = Path.Combine(root, userKey, "movies");
        var showPath = Path.Combine(root, userKey, "shows");
        Directory.CreateDirectory(moviePath);
        Directory.CreateDirectory(showPath);

        var movieName = $"{user.Username}'s AI Movie Picks";
        var showName = $"{user.Username}'s AI Show Picks";

        await EnsureVirtualFolderAsync(movieName, CollectionTypeOptions.movies, moviePath, cancellationToken)
            .ConfigureAwait(false);
        await EnsureVirtualFolderAsync(showName, CollectionTypeOptions.tvshows, showPath, cancellationToken)
            .ConfigureAwait(false);

        var movieId = FindLibraryId(movieName, moviePath);
        var showId = FindLibraryId(showName, showPath);

        var registration = new UserLibraryRegistration
        {
            UserId = userKey,
            MovieLibraryName = movieName,
            ShowLibraryName = showName,
            MoviePath = moviePath,
            ShowPath = showPath,
            MovieLibraryId = movieId,
            ShowLibraryId = showId
        };

        config.UserLibraries.RemoveAll(r => r.UserId == userKey);
        config.UserLibraries.Add(registration);
        Plugin.Instance!.SaveConfiguration();

        if (movieId != Guid.Empty && showId != Guid.Empty)
        {
            await _permissionManager.GrantUserAccessAsync(user, movieId, showId, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Provisioned AI libraries for {User}: movies={MoviePath}, shows={ShowPath}",
            user.Username,
            moviePath,
            showPath);

        return registration;
    }

    public async Task ProvisionAllUsersAsync(CancellationToken cancellationToken)
    {
        foreach (var user in _userManager.GetUsers())
        {
            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            await EnsureUserLibrariesAsync(user, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureVirtualFolderAsync(
        string name,
        CollectionTypeOptions collectionType,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var folders = _libraryManager.GetVirtualFolders();
        if (folders.Any(f => f.Locations.Any(l => string.Equals(l, mediaPath, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        var options = new LibraryOptions
        {
            PathInfos = [new MediaPathInfo(mediaPath)]
        };

        await _libraryManager.AddVirtualFolder(name, collectionType, options, refreshLibrary: true)
            .ConfigureAwait(false);
    }

    private Guid FindLibraryId(string name, string path)
    {
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                || f.Locations.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)));

        if (folder is not null && Guid.TryParse(folder.ItemId, out var id))
        {
            return id;
        }

        var item = _libraryManager.RootFolder.Children
            .OfType<Folder>()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        return item?.Id ?? Guid.Empty;
    }
}
