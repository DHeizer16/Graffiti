using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/walls")]
public class WallsController : ControllerBase
{
    private readonly CanvasRepository _repository;
    private readonly WallService _wallService;
    private readonly ILogger<WallsController> _logger;

    public WallsController(
        CanvasRepository repository,
        WallService wallService,
        ILogger<WallsController> logger)
    {
        _repository = repository;
        _wallService = wallService;
        _logger = logger;
    }

    /// <summary>
    /// Lists active public and discoverable community walls.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPublicWalls([FromQuery] string? search = null, [FromQuery] int limit = 50)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        var walls = await _repository.GetPublicWallsAsync(search, safeLimit);

        Guid currentUserId = GetCurrentUserId();
        var dtos = walls.Select(w => MapToDto(w, currentUserId));
        return Ok(dtos);
    }

    /// <summary>
    /// Lists all walls owned by the currently authenticated user.
    /// </summary>
    [Authorize]
    [HttpGet("my-walls")]
    public async Task<IActionResult> GetMyWalls()
    {
        Guid userId = GetCurrentUserId();
        if (userId == Guid.Empty)
            return Unauthorized();

        var walls = await _repository.GetUserWallsAsync(userId);
        var dtos = walls.Select(w => MapToDto(w, userId));
        return Ok(dtos);
    }

    /// <summary>
    /// Retrieves metadata for a specific canvas wall.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetWall(Guid id)
    {
        var wall = await _repository.GetWallByIdAsync(id);
        if (wall == null)
            return NotFound(new { message = "Canvas wall not found." });

        Guid currentUserId = GetCurrentUserId();

        // Ensure Redis buffer is warmed up
        await _wallService.EnsureWallBufferAsync(wall);

        return Ok(MapToDto(wall, currentUserId));
    }

    /// <summary>
    /// Creates a new custom private canvas wall for the authenticated user.
    /// </summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> CreateWall([FromBody] CreateWallRequest request)
    {
        Guid userId = GetCurrentUserId();
        string username = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Painter";
        string role = User.FindFirstValue(ClaimTypes.Role) ?? "User";

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Wall name is required." });

        if (request.Name.Trim().Length < 3 || request.Name.Trim().Length > 100)
            return BadRequest(new { message = "Wall name must be between 3 and 100 characters." });

        // Enforce quota: 5 walls per user (Admins/Moderators unlimited)
        int currentWallCount = await _repository.GetUserWallCountAsync(userId);
        if (role != "Admin" && role != "Moderator" && currentWallCount >= 5)
        {
            return BadRequest(new { message = "You have reached the maximum quota of 5 private walls." });
        }

        // Validate dimensions (between 100 and 4000)
        int width = Math.Clamp(request.Width, 100, 4000);
        int height = Math.Clamp(request.Height, 100, 4000);

        string accessType = request.AccessType switch
        {
            "Unlisted" => "Unlisted",
            "Password" => "Password",
            "Private" => "Private",
            _ => "Public"
        };

        var wall = new CanvasWall
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            OwnerUsername = username,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Width = width,
            Height = height,
            AccessType = accessType,
            AccessKey = accessType == "Password" ? request.AccessKey?.Trim() : null,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        await _repository.CreateWallAsync(wall);
        await _wallService.EnsureWallBufferAsync(wall);

        _logger.LogInformation("User {Username} ({UserId}) created canvas wall '{WallName}' ({Width}x{Height})",
            username, userId, wall.Name, wall.Width, wall.Height);

        return CreatedAtAction(nameof(GetWall), new { id = wall.Id }, MapToDto(wall, userId));
    }

    /// <summary>
    /// Deletes (deactivates) a wall owned by the caller or by an Administrator.
    /// </summary>
    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteWall(Guid id)
    {
        Guid userId = GetCurrentUserId();
        string role = User.FindFirstValue(ClaimTypes.Role) ?? "User";
        bool isAdmin = role == "Admin";

        bool deleted = await _repository.DeleteWallAsync(id, userId, isAdmin);
        if (!deleted)
            return NotFound(new { message = "Wall not found or you do not have permission to delete it." });

        _logger.LogInformation("Wall {WallId} deactivated by user {UserId}", id, userId);
        return Ok(new { message = "Wall deactivated successfully." });
    }

    private Guid GetCurrentUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    private static CanvasWallDto MapToDto(CanvasWall wall, Guid currentUserId)
    {
        return new CanvasWallDto
        {
            Id = wall.Id,
            OwnerId = wall.OwnerId,
            OwnerUsername = wall.OwnerUsername,
            Name = wall.Name,
            Description = wall.Description,
            Width = wall.Width,
            Height = wall.Height,
            AccessType = wall.AccessType,
            HasAccessKey = !string.IsNullOrEmpty(wall.AccessKey),
            CreatedAt = wall.CreatedAt,
            IsOwner = currentUserId != Guid.Empty && wall.OwnerId == currentUserId,
            PixelCount = wall.PixelCount
        };
    }
}
