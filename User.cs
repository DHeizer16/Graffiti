namespace GlobalGraffitiWall.API;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
    public string Role { get; set; } = "User"; // "User", "Moderator", "Admin"
}

public class UserProfileDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string Role { get; set; } = "User";
    public int TotalPixelsPlaced { get; set; }
    public int ActiveReservationsCount { get; set; }
}

public class AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public UserProfileDto User { get; set; } = new();
    public int LinkedPixelsCount { get; set; }
    public int LinkedReservationsCount { get; set; }
}

public class RegisterRequestDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? GuestUserIdToLink { get; set; }
}

public class LoginRequestDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? GuestUserIdToLink { get; set; }
}

public class LinkGuestHistoryResult
{
    public int LinkedPixels { get; set; }
    public int LinkedReservations { get; set; }
}
