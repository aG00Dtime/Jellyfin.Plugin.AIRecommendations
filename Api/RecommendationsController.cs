using Jellyfin.Plugin.AIRecommendations.Models;
using Jellyfin.Plugin.AIRecommendations.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AIRecommendations.Api;

/// <summary>
/// Admin API for AI recommendations sync and status.
/// </summary>
[ApiController]
[Route("AIRecommendations")]
[Authorize(Policy = "RequiresElevation")]
[ApiExplorerSettings(IgnoreApi = true)]
public class RecommendationsController : ControllerBase
{
    private readonly RecommendationSyncService _syncService;
    private readonly IUserManager _userManager;

    public RecommendationsController(
        RecommendationSyncService syncService,
        IUserManager userManager)
    {
        _syncService = syncService;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets plugin sync status.
    /// </summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginStatusDto> GetStatus()
        => Ok(_syncService.GetStatus());

    /// <summary>
    /// Triggers a full sync for all users (admin only).
    /// </summary>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SyncAll(CancellationToken cancellationToken)
    {
        await _syncService.SyncAllUsersAsync(null, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Triggers sync for a single user.
    /// </summary>
    [HttpPost("Sync/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SyncUser([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        await _syncService.SyncUserAsync(user, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
