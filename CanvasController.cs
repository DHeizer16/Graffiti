using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/canvas")]
public class CanvasController : ControllerBase
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CanvasRepository _repository;
    private readonly WallService _wallService;
    private readonly PaletteService _paletteService;
    private readonly GlobalGraffitiWall.API.Telemetry.CanvasMetrics _metrics;

    private const string CanvasRedisKey = "canvas:global_state";
    private const string CanvasMinimapRedisKey = "canvas:minimap_overview";
    private const int Width = 10000;
    private const int TileSize = 256;
    private const int TilesPerRow = 40; // Math.Ceiling(10000 / 256.0)

    // Atomic 2D Tile Extraction Lua Script (8-bit 1-byte per pixel, 64 KB per 256x256 tile)
    private const string GetTileLuaScript = """
        local key = KEYS[1]
        local tx = tonumber(ARGV[1])
        local ty = tonumber(ARGV[2])
        local tile_size = 256
        local row_bytes = 256
        local world_width = tonumber(ARGV[3]) or 10000
        local world_height = tonumber(ARGV[4]) or 10000
        local world_row_bytes = world_width

        local chunks = {}
        local has_content = false

        for r = 0, tile_size - 1 do
            local y = (ty * tile_size) + r
            if y < world_height then
                local start_byte = (y * world_row_bytes) + (tx * row_bytes)
                local row_end_byte = (y * world_row_bytes) + world_row_bytes - 1
                local fetch_end_byte = math.min(start_byte + row_bytes - 1, row_end_byte)

                if start_byte <= row_end_byte then
                    local row_data = redis.call('GETRANGE', key, start_byte, fetch_end_byte)
                    local fetched_len = #row_data
                    if fetched_len < row_bytes then
                        row_data = row_data .. string.rep("\0", row_bytes - fetched_len)
                    end
                    table.insert(chunks, row_data)
                    if not has_content and row_data ~= string.rep("\0", row_bytes) then
                        has_content = true
                    end
                else
                    table.insert(chunks, string.rep("\0", row_bytes))
                end
            else
                table.insert(chunks, string.rep("\0", row_bytes))
            end
        end

        if not has_content then
            return ""
        end

        return table.concat(chunks)
        """;

    public CanvasController(
        IConnectionMultiplexer redis,
        CanvasRepository repository,
        WallService wallService,
        PaletteService paletteService,
        GlobalGraffitiWall.API.Telemetry.CanvasMetrics metrics)
    {
        _redis = redis;
        _repository = repository;
        _wallService = wallService;
        _paletteService = paletteService;
        _metrics = metrics;
    }

    /// <summary>
    /// Retrieves live server telemetry metrics (active SignalR connections, placements, flushes, uptime).
    /// </summary>
    [HttpGet("telemetry")]
    public IActionResult GetTelemetry()
    {
        return Ok(_metrics.GetSummary());
    }

    /// <summary>
    /// Retrieves the canvas color palette.
    /// By default returns all 256 colors, or set activeOnly=true for current painting palette.
    /// </summary>
    [HttpGet("palette")]
    public async Task<IActionResult> GetPalette([FromQuery] bool activeOnly = false)
    {
        var colors = await _paletteService.GetPaletteAsync(activeOnly);
        return Ok(colors);
    }

    /// <summary>
    /// Retrieves a 256x256 pixel tile (64 KB at 8 bits / 1 byte per pixel) by tile indices (tx, ty).
    /// Supports both global wall and private custom walls via optional wallId parameter.
    /// Returns 204 No Content if the entire tile contains only unpainted (0) pixels.
    /// </summary>
    [HttpGet("tile")]
    public async Task<IActionResult> GetTile([FromQuery] int tx, [FromQuery] int ty, [FromQuery] Guid? wallId = null)
    {
        int worldWidth = Width;
        int worldHeight = Width;
        string key = CanvasRedisKey;

        if (wallId != null)
        {
            var wall = await _repository.GetWallByIdAsync(wallId.Value);
            if (wall == null) return NotFound(new { message = "Wall not found." });
            worldWidth = wall.Width;
            worldHeight = wall.Height;
            key = _wallService.GetStateKey(wallId);
            await _wallService.EnsureWallBufferAsync(wall);
        }

        int maxTilesX = (int)Math.Ceiling(worldWidth / 256.0);
        int maxTilesY = (int)Math.Ceiling(worldHeight / 256.0);

        if (tx < 0 || tx >= maxTilesX || ty < 0 || ty >= maxTilesY)
        {
            return BadRequest($"Invalid tile coordinates ({tx}, {ty}). Range must be 0..{maxTilesX - 1}, 0..{maxTilesY - 1}.");
        }

        var db = _redis.GetDatabase();
        RedisKey[] keys = [key];
        RedisValue[] values = [tx.ToString(), ty.ToString(), worldWidth.ToString(), worldHeight.ToString()];

        var rawResult = await db.ScriptEvaluateAsync(GetTileLuaScript, keys, values);
        byte[]? tileBuffer = (byte[]?)rawResult;

        if (tileBuffer == null || tileBuffer.Length == 0)
        {
            return NoContent();
        }

        return File(tileBuffer, "application/octet-stream");
    }

    /// <summary>
    /// Retrieves a 160x160 overview bitmap (25.6 KB @ 8-bit) representing the canvas for the Minimap.
    /// </summary>
    [HttpGet("minimap")]
    public async Task<IActionResult> GetMinimapOverview([FromQuery] Guid? wallId = null)
    {
        var db = _redis.GetDatabase();
        string key = _wallService.GetMinimapKey(wallId);

        if (wallId != null)
        {
            var wall = await _repository.GetWallByIdAsync(wallId.Value);
            if (wall != null)
            {
                await _wallService.EnsureWallBufferAsync(wall);
            }
        }

        byte[]? buffer = (byte[]?)await db.StringGetAsync(key);
        if (buffer == null || buffer.Length != 25600)
        {
            buffer = new byte[25600];
        }

        return File(buffer, "application/octet-stream");
    }

    [HttpGet("buffer")]
    public async Task<IActionResult> GetCanvasBuffer([FromQuery] int startByte = 0, [FromQuery] int length = 100_000_000, [FromQuery] Guid? wallId = null)
    {
        int maxBytes = 100_000_000;
        string key = CanvasRedisKey;

        if (wallId != null)
        {
            var wall = await _repository.GetWallByIdAsync(wallId.Value);
            if (wall == null) return NotFound(new { message = "Wall not found." });
            maxBytes = wall.Width * wall.Height;
            key = _wallService.GetStateKey(wallId);
            await _wallService.EnsureWallBufferAsync(wall);
        }

        if (startByte < 0 || startByte >= maxBytes || length <= 0)
        {
            return BadRequest("Invalid startByte or length parameters.");
        }

        int endByte = Math.Min(startByte + length - 1, maxBytes - 1);
        if (endByte < startByte)
        {
            return File(Array.Empty<byte>(), "application/octet-stream");
        }

        var db = _redis.GetDatabase();
        byte[] buffer = (byte[]?)await db.StringGetRangeAsync(key, startByte, endByte) ?? Array.Empty<byte>();

        return File(buffer, "application/octet-stream");
    }

    /// <summary>
    /// Returns inspection details for a pixel coordinate on the global canvas or a private wall.
    /// </summary>
    [HttpGet("pixel-info")]
    public async Task<IActionResult> GetPixelInfo([FromQuery] int x, [FromQuery] int y, [FromQuery] Guid? wallId = null)
    {
        int width = Width;
        int height = Width;
        string key = CanvasRedisKey;

        if (wallId != null)
        {
            var wall = await _repository.GetWallByIdAsync(wallId.Value);
            if (wall == null) return NotFound(new { message = "Wall not found." });
            width = wall.Width;
            height = wall.Height;
            key = _wallService.GetStateKey(wallId);
            await _wallService.EnsureWallBufferAsync(wall);
        }

        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            return BadRequest("Coordinates out of bounds.");
        }

        // 1. Fetch current color from Redis (8-bit direct byte read)
        var db = _redis.GetDatabase();
        int pixelIndex = (y * width) + x;

        byte[]? slice = (byte[]?)await db.StringGetRangeAsync(key, pixelIndex, pixelIndex);
        byte currentColorId = (slice != null && slice.Length > 0) ? slice[0] : (byte)0;

        // 2. Fetch history and placement count from SQL Server
        var (placements, totalCount) = await _repository.GetPixelHistoryAsync(x, y, limit: 10, wallId: wallId);

        var latest = placements.FirstOrDefault();
        var history = placements.Skip(1).ToList();

        var result = new PixelInfoDto
        {
            X = x,
            Y = y,
            CurrentColorId = currentColorId,
            TotalPlacements = totalCount,
            LatestPlacement = latest,
            History = history
        };

        return Ok(result);
    }
}

public class PixelInfoDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public byte CurrentColorId { get; set; }
    public int TotalPlacements { get; set; }
    public PixelPlacementHistoryDto? LatestPlacement { get; set; }
    public List<PixelPlacementHistoryDto> History { get; set; } = new();
}