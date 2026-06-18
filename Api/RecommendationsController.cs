using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    private readonly IHttpClientFactory _httpClientFactory;

    public RecommendationsController(
        RecommendationSyncService syncService,
        IUserManager userManager,
        IHttpClientFactory httpClientFactory)
    {
        _syncService = syncService;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
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

    /// <summary>
    /// Tests connectivity for a given provider using supplied credentials.
    /// </summary>
    [HttpPost("TestProvider")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestProviderResult>> TestProvider(
        [FromBody] TestProviderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await RunProviderTestAsync(request, cancellationToken).ConfigureAwait(false);
            return Ok(new TestProviderResult { Success = true, Message = message });
        }
        catch (Exception ex)
        {
            return Ok(new TestProviderResult { Success = false, Message = ex.Message });
        }
    }

    private async Task<string> RunProviderTestAsync(TestProviderRequest req, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ProviderTest");

        switch (req.Provider)
        {
            case "Ollama":
            {
                var isCloud = req.OllamaDeployment.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? (isCloud ? "https://ollama.com" : "http://localhost:11434")
                    : req.BaseUrl.TrimEnd('/');

                using var tagsReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/tags");
                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                {
                    tagsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.ApiKey);
                }

                using var tagsResp = await client.SendAsync(tagsReq, cancellationToken).ConfigureAwait(false);
                tagsResp.EnsureSuccessStatusCode();
                return $"Connected to Ollama at {baseUrl}";
            }

            case "OpenRouter":
            {
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? "https://openrouter.ai/api/v1"
                    : req.BaseUrl.TrimEnd('/');
                return await TestOpenAiCompatibleAsync(client, baseUrl, req.ApiKey, req.Model, cancellationToken)
                    .ConfigureAwait(false);
            }

            default: // OpenAI
            {
                var baseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                    ? "https://api.openai.com/v1"
                    : req.BaseUrl.TrimEnd('/');
                return await TestOpenAiCompatibleAsync(client, baseUrl, req.ApiKey, req.Model, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> TestOpenAiCompatibleAsync(
        HttpClient client,
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = "Hi" } },
            max_tokens = 1
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return $"Connected successfully (model: {model})";
    }
}

public class TestProviderRequest
{
    public string Provider { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string OllamaDeployment { get; set; } = "Local";
}

public class TestProviderResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
