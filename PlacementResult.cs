namespace GlobalGraffitiWall.API;

public class PlacementResult
{
    public bool Success { get; set; }
    public double RemainingTokens { get; set; }
    public int BonusBalance { get; set; }
    public double MaxCapacity { get; set; }
    public double RefillRate { get; set; }
    public int WaitSeconds { get; set; }
}
