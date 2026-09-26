namespace GlobalGraffitiWall.API;

public class TokenBalanceDto
{
    public bool IsAuthenticated { get; set; }
    public double RegeneratingCharges { get; set; }
    public double MaxCapacity { get; set; }
    public double RefillRate { get; set; }
    public int BonusTokens { get; set; }
    public int TotalAvailableTokens => (int)Math.Floor(RegeneratingCharges) + BonusTokens;
    public bool CanClaimDaily { get; set; }
    public int SecondsUntilNextDailyClaim { get; set; }
}

public class ClaimDailyResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ClaimedTokens { get; set; }
    public int NewBonusBalance { get; set; }
    public DateTimeOffset NextClaimAt { get; set; }
    public int SecondsUntilNextClaim { get; set; }
}

public class RedeemCodeRequestDto
{
    public string Code { get; set; } = string.Empty;
}

public class RedeemCodeResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int GrantedTokens { get; set; }
    public int NewBonusBalance { get; set; }
}

public class TokenCostPreviewDto
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Area { get; set; }
    public int DurationMinutes { get; set; }
    public int TokenCost { get; set; }
    public bool CanAfford { get; set; }
    public int AvailableTokens { get; set; }
}

public class TokenTransactionDto
{
    public long Id { get; set; }
    public int Amount { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
