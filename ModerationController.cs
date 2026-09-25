using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/moderation")]
[Authorize(Roles = "Admin,Moderator")]
public class ModerationController : ControllerBase
{
    private readonly ModerationService _moderationService;
    private readonly CanvasRepository _repository;

    public ModerationController(
        ModerationService moderationService,
        CanvasRepository repository)
    {
        _moderationService = moderationService;
        _repository = repository;
    }

    /// <summary>
    /// Shadow-bans a user identifier or IP address (Admin or Moderator only).
    /// </summary>
    [HttpPost("shadow-ban")]
    public async Task<IActionResult> ShadowBan([FromBody] ShadowBanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            return BadRequest(new { error = "Identifier (IP address or User ID) is required." });
        }

        var callerName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? request.BannedBy ?? "Moderator";

        await _moderationService.ShadowBanAsync(
            request.Identifier.Trim(),
            request.BanType,
            request.Reason,
            callerName);

        return Ok(new
        {
            success = true,
            message = $"Entity '{request.Identifier}' has been shadow-banned ({request.BanType}) by {callerName}.",
            identifier = request.Identifier,
            banType = request.BanType
        });
    }

    /// <summary>
    /// Removes a shadow ban from a user identifier or IP address (Admin or Moderator only).
    /// </summary>
    [HttpPost("unban")]
    public async Task<IActionResult> Unban([FromBody] UnbanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            return BadRequest(new { error = "Identifier is required." });
        }

        await _moderationService.UnbanAsync(request.Identifier.Trim());

        return Ok(new
        {
            success = true,
            message = $"Shadow-ban removed for entity '{request.Identifier}'.",
            identifier = request.Identifier
        });
    }

    /// <summary>
    /// Returns all currently active shadow bans (Admin or Moderator only).
    /// </summary>
    [HttpGet("banned")]
    public async Task<IActionResult> GetActiveBans()
    {
        var bans = await _moderationService.GetActiveBansAsync();
        return Ok(bans);
    }

    /// <summary>
    /// Returns intercepted pixel placements from shadow-banned users for audit review (Admin or Moderator only).
    /// </summary>
    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50)
    {
        var logs = await _moderationService.GetShadowBannedPlacementsAsync(limit);
        return Ok(logs);
    }

    /// <summary>
    /// Checks whether an IP or User ID is currently shadow-banned.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> CheckStatus([FromQuery] string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return BadRequest(new { error = "Identifier is required." });
        }

        bool isBanned = await _moderationService.IsShadowBannedAsync(identifier, identifier);
        return Ok(new { identifier, isShadowBanned = isBanned });
    }

    /// <summary>
    /// Updates the role of a user (Admin only).
    /// </summary>
    [HttpPost("set-role")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetUserRole([FromBody] SetRoleRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Role))
        {
            return BadRequest(new { error = "Username and Role are required." });
        }

        var normalizedRole = request.Role.Trim();
        if (normalizedRole != "Admin" && normalizedRole != "Moderator" && normalizedRole != "User")
        {
            return BadRequest(new { error = "Role must be 'Admin', 'Moderator', or 'User'." });
        }

        var success = await _repository.SetUserRoleAsync(request.Username.Trim(), normalizedRole);
        if (!success)
        {
            return NotFound(new { error = $"User '{request.Username}' not found." });
        }

        return Ok(new
        {
            success = true,
            username = request.Username,
            role = normalizedRole,
            message = $"User '{request.Username}' role updated to {normalizedRole}."
        });
    }
}

public class SetRoleRequest
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
