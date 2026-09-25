using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly CanvasRepository _repository;
    private readonly TokenService _tokenService;
    private readonly ILogger<AuthController> _logger;

    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_]{3,30}$", RegexOptions.Compiled);

    public AuthController(
        CanvasRepository repository,
        TokenService tokenService,
        ILogger<AuthController> logger)
    {
        _repository = repository;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// Registers a new user account. Optionally links any guest placement history.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto req)
    {
        if (req == null)
            return BadRequest("Invalid request payload.");

        string username = req.Username?.Trim() ?? string.Empty;
        if (!UsernameRegex.IsMatch(username))
        {
            return BadRequest("Username must be between 3 and 30 characters and contain only letters, numbers, and underscores.");
        }

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
        {
            return BadRequest("Password must be at least 6 characters long.");
        }

        var existing = await _repository.GetUserByUsernameAsync(username);
        if (existing != null)
        {
            return Conflict($"Username '{username}' is already taken. Please choose another.");
        }

        string displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim();
        var (hash, salt) = PasswordSecurity.HashPassword(req.Password);

        var userCount = await _repository.GetUserCountAsync();
        string role = (userCount == 0) ? "Admin" : "User";

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            NormalizedUsername = username.ToUpperInvariant(),
            DisplayName = displayName,
            PasswordHash = hash,
            PasswordSalt = salt,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = DateTimeOffset.UtcNow,
            IsActive = true,
            Role = role
        };

        await _repository.CreateUserAsync(newUser);
        _logger.LogInformation("New user registered: {Username} (ID: {UserId})", newUser.Username, newUser.Id);

        // Optionally link guest history if provided
        int linkedPixels = 0;
        int linkedReservations = 0;
        if (!string.IsNullOrWhiteSpace(req.GuestUserIdToLink) && Guid.TryParse(req.GuestUserIdToLink, out var guestGuid))
        {
            var linkResult = await _repository.LinkGuestHistoryToUserAsync(guestGuid, newUser.Id, newUser.DisplayName);
            linkedPixels = linkResult.LinkedPixels;
            linkedReservations = linkResult.LinkedReservations;
            if (linkedPixels > 0 || linkedReservations > 0)
            {
                _logger.LogInformation("Linked {Pixels} pixels and {Res} reservations from guest {GuestId} to {UserId}",
                    linkedPixels, linkedReservations, guestGuid, newUser.Id);
            }
        }

        var token = _tokenService.GenerateJwtToken(newUser);
        var totalPlaced = await _repository.GetUserPlacementCountAsync(newUser.Id);
        var activeResCount = await _repository.GetActiveReservationCountForOwnerAsync(newUser.Id.ToString());

        var profile = new UserProfileDto
        {
            Id = newUser.Id,
            Username = newUser.Username,
            DisplayName = newUser.DisplayName,
            CreatedAt = newUser.CreatedAt,
            LastLoginAt = newUser.LastLoginAt,
            Role = newUser.Role,
            TotalPixelsPlaced = totalPlaced,
            ActiveReservationsCount = activeResCount
        };

        return Created($"/api/auth/user/{newUser.Username}", new AuthResponseDto
        {
            Token = token,
            User = profile,
            LinkedPixelsCount = linkedPixels,
            LinkedReservationsCount = linkedReservations
        });
    }

    /// <summary>
    /// Authenticates a user with username and password. Optionally links guest history.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("Username and password are required.");

        var user = await _repository.GetUserByUsernameAsync(req.Username);
        if (user == null || !user.IsActive)
        {
            return Unauthorized("Invalid username or password.");
        }

        if (!PasswordSecurity.VerifyPassword(req.Password, user.PasswordHash, user.PasswordSalt))
        {
            return Unauthorized("Invalid username or password.");
        }

        await _repository.UpdateUserLastLoginAsync(user.Id, DateTimeOffset.UtcNow);

        // Link guest history if specified and different
        int linkedPixels = 0;
        int linkedReservations = 0;
        if (!string.IsNullOrWhiteSpace(req.GuestUserIdToLink) && Guid.TryParse(req.GuestUserIdToLink, out var guestGuid))
        {
            if (guestGuid != user.Id)
            {
                var linkResult = await _repository.LinkGuestHistoryToUserAsync(guestGuid, user.Id, user.DisplayName);
                linkedPixels = linkResult.LinkedPixels;
                linkedReservations = linkResult.LinkedReservations;
            }
        }

        var token = _tokenService.GenerateJwtToken(user);
        var totalPlaced = await _repository.GetUserPlacementCountAsync(user.Id);
        var activeResCount = await _repository.GetActiveReservationCountForOwnerAsync(user.Id.ToString());

        var profile = new UserProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = DateTimeOffset.UtcNow,
            Role = user.Role,
            TotalPixelsPlaced = totalPlaced,
            ActiveReservationsCount = activeResCount
        };

        return Ok(new AuthResponseDto
        {
            Token = token,
            User = profile,
            LinkedPixelsCount = linkedPixels,
            LinkedReservationsCount = linkedReservations
        });
    }

    /// <summary>
    /// Returns the currently authenticated user's profile and stats.
    /// Supports Bearer token or optional userId query fallback.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser([FromQuery] string? userId)
    {
        Guid targetUserId = Guid.Empty;

        // 1. Try to read from Claims (JWT Bearer)
        var claimId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(claimId) && Guid.TryParse(claimId, out var parsedClaimId))
        {
            targetUserId = parsedClaimId;
        }
        else if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var parsedQueryId))
        {
            targetUserId = parsedQueryId;
        }

        if (targetUserId == Guid.Empty)
        {
            return Unauthorized("Authentication token or valid user ID required.");
        }

        var user = await _repository.GetUserByIdAsync(targetUserId);
        if (user == null)
        {
            return NotFound("User profile not found.");
        }

        var totalPlaced = await _repository.GetUserPlacementCountAsync(user.Id);
        var activeResCount = await _repository.GetActiveReservationCountForOwnerAsync(user.Id.ToString());

        var profile = new UserProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Role = user.Role,
            TotalPixelsPlaced = totalPlaced,
            ActiveReservationsCount = activeResCount
        };

        return Ok(profile);
    }

    /// <summary>
    /// Public lookup for a user profile by username.
    /// </summary>
    [HttpGet("user/{username}")]
    public async Task<IActionResult> GetUserProfile(string username)
    {
        var user = await _repository.GetUserByUsernameAsync(username);
        if (user == null || !user.IsActive)
        {
            return NotFound($"User '@{username}' was not found.");
        }

        var totalPlaced = await _repository.GetUserPlacementCountAsync(user.Id);
        var activeResCount = await _repository.GetActiveReservationCountForOwnerAsync(user.Id.ToString());

        var profile = new UserProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Role = user.Role,
            TotalPixelsPlaced = totalPlaced,
            ActiveReservationsCount = activeResCount
        };

        return Ok(profile);
    }

    /// <summary>
    /// Explicitly links guest placement history to an authenticated user account.
    /// </summary>
    [HttpPost("link-guest")]
    public async Task<IActionResult> LinkGuestSession([FromBody] LinkGuestRequestDto req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.GuestUserId))
            return BadRequest("Guest user ID is required.");

        Guid authUserId = Guid.Empty;
        var claimId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(claimId) && Guid.TryParse(claimId, out var parsedClaimId))
        {
            authUserId = parsedClaimId;
        }
        else if (!string.IsNullOrEmpty(req.AuthenticatedUserId) && Guid.TryParse(req.AuthenticatedUserId, out var parsedReqId))
        {
            authUserId = parsedReqId;
        }

        if (authUserId == Guid.Empty)
            return Unauthorized("Valid authenticated user ID required.");

        if (!Guid.TryParse(req.GuestUserId, out var guestGuid))
            return BadRequest("Invalid guest user ID format.");

        var user = await _repository.GetUserByIdAsync(authUserId);
        if (user == null)
            return NotFound("Authenticated user not found.");

        var result = await _repository.LinkGuestHistoryToUserAsync(guestGuid, authUserId, user.DisplayName);
        return Ok(new
        {
            Message = $"Successfully linked {result.LinkedPixels} pixels and {result.LinkedReservations} territory reservations to @{user.Username}.",
            LinkedPixels = result.LinkedPixels,
            LinkedReservations = result.LinkedReservations
        });
    }
}

public class LinkGuestRequestDto
{
    public string GuestUserId { get; set; } = string.Empty;
    public string? AuthenticatedUserId { get; set; }
}
