using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/moderation")]
[Authorize(Roles = "Admin,Moderator")]
public class ModerationController : ControllerBase
{
    private readonly ModerationService _moderationService;
    private readonly CanvasRepository _repository;
    private readonly IHubContext<CanvasHub> _hubContext;

    public ModerationController(
        ModerationService moderationService,
        CanvasRepository repository,
        IHubContext<CanvasHub> hubContext)
    {
        _moderationService = moderationService;
        _repository = repository;
        _hubContext = hubContext;
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

    /// <summary>
    /// Schedules a future global canvas reset with a live countdown banner (Admin only).
    /// </summary>
    [HttpPost("schedule-reset")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ScheduleReset(
        [FromBody] ScheduleResetRequest request,
        [FromServices] CanvasResetService resetService)
    {
        if (request.ScheduledResetUtc <= DateTimeOffset.UtcNow)
        {
            return BadRequest(new { error = "Scheduled reset time must be in the future." });
        }

        var adminName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Admin";

        try
        {
            var record = await resetService.ScheduleResetAsync(request.ScheduledResetUtc, adminName, request.AnnouncementMessage);
            return Ok(new
            {
                success = true,
                message = $"Global canvas reset scheduled for {request.ScheduledResetUtc:u} by {adminName}.",
                scheduledResetUtc = record.ScheduledResetUtc,
                announcementMessage = record.AnnouncementMessage,
                scheduledBy = record.ScheduledBy
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancels a pending scheduled global canvas reset (Admin only).
    /// </summary>
    [HttpPost("cancel-reset")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CancelReset([FromServices] CanvasResetService resetService)
    {
        var adminName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Admin";
        await resetService.CancelResetAsync(adminName);

        return Ok(new
        {
            success = true,
            message = $"Scheduled canvas reset cancelled by {adminName}."
        });
    }

    /// <summary>
    /// Executes an immediate emergency clean slate wipe of the global canvas (Admin only).
    /// </summary>
    [HttpPost("execute-reset-now")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ExecuteResetNow(
        [FromBody] ExecuteResetNowRequest? request,
        [FromServices] CanvasResetService resetService)
    {
        var adminName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Admin";
        var reason = !string.IsNullOrWhiteSpace(request?.Reason)
            ? request.Reason.Trim()
            : "Immediate administrative emergency wipe";

        var newSeason = await resetService.ExecuteResetAsync(adminName, reason);

        return Ok(new
        {
            success = true,
            message = $"Global canvas wiped cleanly to blank white. Started Season #{newSeason.SeasonNumber}: '{newSeason.Name}'.",
            seasonNumber = newSeason.SeasonNumber,
            seasonName = newSeason.Name,
            startedAt = newSeason.StartedAt,
            resetBy = adminName
        });
    }

    /// <summary>
    /// Toggles live multi-user cursors on or off across all connected clients (Admin or Moderator only).
    /// </summary>
    [HttpPost("toggle-cursors")]
    public async Task<IActionResult> ToggleCursors([FromBody] ToggleCursorsRequest request)
    {
        await _moderationService.SetLiveCursorsEnabledAsync(request.Enabled);
        await _hubContext.Clients.All.SendAsync("LiveCursorsToggled", new { enabled = request.Enabled });
        return Ok(new
        {
            success = true,
            enabled = request.Enabled,
            message = request.Enabled ? "Live cursors enabled globally." : "Live cursors disabled globally."
        });
    }
}


public class SetRoleRequest
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class ToggleCursorsRequest
{
    public bool Enabled { get; set; }
}
