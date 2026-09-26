namespace GlobalGraffitiWall.API;

public class CanvasReservation
{
    public int Id { get; set; }
    public Guid ReservationId { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public int TokenCost { get; set; } = 0;
    public int RefundedTokens { get; set; } = 0;

    public int Width => Math.Abs(X2 - X1) + 1;
    public int Height => Math.Abs(Y2 - Y1) + 1;
    public int Area => Width * Height;

    public bool Contains(int x, int y)
    {
        return x >= X1 && x <= X2 && y >= Y1 && y <= Y2;
    }

    public bool Overlaps(int ox1, int oy1, int ox2, int oy2)
    {
        return !(ox2 < X1 || ox1 > X2 || oy2 < Y1 || oy1 > Y2);
    }

    public bool IsExpired(DateTimeOffset now)
    {
        return ExpiresAt <= now;
    }

    public ReservationDto ToDto(DateTimeOffset now)
    {
        int remaining = Math.Max(0, (int)(ExpiresAt - now).TotalSeconds);
        return new ReservationDto
        {
            Id = ReservationId,
            OwnerId = OwnerId,
            OwnerName = OwnerName,
            X1 = X1,
            Y1 = Y1,
            X2 = X2,
            Y2 = Y2,
            Width = Width,
            Height = Height,
            Area = Area,
            Label = Label,
            TokenCost = TokenCost,
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt,
            SecondsRemaining = remaining
        };
    }
}

public class CreateReservationRequest
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public string? Label { get; set; }
    public string OwnerName { get; set; } = "Anonymous";
    public string OwnerId { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 60; // Default 1 hour
    public string? WallId { get; set; }
}

public class ReservationDto
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Area { get; set; }
    public string? Label { get; set; }
    public int TokenCost { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int SecondsRemaining { get; set; }
}

public class ReservationCreatedResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ReservationDto? Reservation { get; set; }
    public string? SecretKey { get; set; }
    public int TokenCost { get; set; }
    public int RemainingBonusTokens { get; set; }
}

public class CancelReservationRequest
{
    public Guid ReservationId { get; set; }
    public string? SecretKey { get; set; }
    public string? OwnerId { get; set; }
}

public class CancelReservationResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int RefundedTokens { get; set; }
    public int NewBonusBalance { get; set; }
}

public class LinkKeyRequest
{
    public string Key { get; set; } = string.Empty;
}
