using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/history")]
public class HistoryController : ControllerBase
{
    private readonly string _connectionString;

    public HistoryController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServerConnection")!;
    }

    /// <summary>
    /// Returns historical pixel placement deltas up to a specified target time.
    /// </summary>
    [HttpGet("deltas")]
    public async Task<IActionResult> GetPixelDeltas(
        [FromQuery] string? fromTime, 
        [FromQuery] string? toTime,
        [FromQuery] int limit = 100_000,
        [FromQuery] Guid? wallId = null)
    {
        using IDbConnection db = new SqlConnection(_connectionString);

        // Fall back to year 2000 if fromTime is null/empty
        DateTimeOffset parsedFrom = DateTimeOffset.TryParse(fromTime, out var f)
            ? f.ToUniversalTime()
            : new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset parsedTo = DateTimeOffset.TryParse(toTime, out var t)
            ? t.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        int safeLimit = Math.Clamp(limit, 1, 200_000);
        string wallFilter = wallId == null ? "AND wall_id IS NULL" : "AND wall_id = @WallId";

        string sql = $"""
            SELECT TOP (@Limit) x AS X, y AS Y, color_id AS ColorId, placed_at AS PlacedAt
            FROM pixel_placements WITH (NOLOCK)
            WHERE placed_at >= @FromTime AND placed_at <= @ToTime AND is_shadow_banned = 0 {wallFilter}
            ORDER BY placed_at ASC;
            """;

        var deltas = await db.QueryAsync<PixelDeltaDto>(sql, new { FromTime = parsedFrom, ToTime = parsedTo, Limit = safeLimit, WallId = wallId });
        return Ok(deltas);
    }

    /// <summary>
    /// Returns the historical time range from the very first pixel placement to now for a canvas wall.
    /// </summary>
    [HttpGet("range")]
    public async Task<IActionResult> GetTimeRange([FromQuery] Guid? wallId = null)
    {
        using IDbConnection db = new SqlConnection(_connectionString);
        string wallFilter = wallId == null ? "WHERE is_shadow_banned = 0 AND wall_id IS NULL" : "WHERE is_shadow_banned = 0 AND wall_id = @WallId";

        string sql = $"""
            SELECT 
                ISNULL(MIN(placed_at), GETUTCDATE()) AS EarliestTime, 
                ISNULL(MAX(placed_at), GETUTCDATE()) AS LatestTime, 
                COUNT(*) AS TotalPixels 
            FROM pixel_placements WITH (NOLOCK)
            {wallFilter};
            """;

        var result = await db.QuerySingleOrDefaultAsync<TimeRangeDto>(sql, new { WallId = wallId });
        return Ok(result ?? new TimeRangeDto
        {
            EarliestTime = DateTimeOffset.UtcNow,
            LatestTime = DateTimeOffset.UtcNow,
            TotalPixels = 0
        });
    }

    public class PixelDeltaDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public byte ColorId { get; set; }
        public DateTimeOffset PlacedAt { get; set; }
    }

    public class TimeRangeDto
    {
        public DateTimeOffset EarliestTime { get; set; }
        public DateTimeOffset LatestTime { get; set; }
        public long TotalPixels { get; set; }
    }
}