namespace GlobalGraffitiWall.API;

public record PixelPlacementItem(
    int X,
    int Y,
    byte ColorId,
    Guid UserId,
    string? IpAddress,
    DateTimeOffset PlacedAt,
    bool IsShadowBanned = false,
    Guid? WallId = null);
