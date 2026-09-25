namespace GlobalGraffitiWall.API;

public class ShadowBanRequest
{
    public string Identifier { get; set; } = string.Empty;
    public string BanType { get; set; } = "ip"; // "ip" or "user"
    public string? Reason { get; set; }
    public string? BannedBy { get; set; } = "Admin";
}

public class UnbanRequest
{
    public string Identifier { get; set; } = string.Empty;
}

public class ShadowBanRecord
{
    public int Id { get; set; }
    public string Identifier { get; set; } = string.Empty;
    public string BanType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? BannedBy { get; set; }
    public DateTimeOffset BannedAt { get; set; }
    public bool IsActive { get; set; }
}

public class ShadowPlacementAuditDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public byte ColorId { get; set; }
    public Guid UserId { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
}
