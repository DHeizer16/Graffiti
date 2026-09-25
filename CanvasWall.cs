namespace GlobalGraffitiWall.API;

public class CanvasWall
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string OwnerUsername { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 1000;
    public string AccessType { get; set; } = "Public"; // Public, Unlisted, Password, Private
    public string? AccessKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
    public long PixelCount { get; set; } = 0;
}

public class CreateWallRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 1000;
    public string AccessType { get; set; } = "Public"; // Public, Unlisted, Password, Private
    public string? AccessKey { get; set; }
}

public class CanvasWallDto
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string OwnerUsername { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string AccessType { get; set; } = "Public";
    public bool HasAccessKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsOwner { get; set; }
    public long PixelCount { get; set; }
}
