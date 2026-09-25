using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

public class WallService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CanvasRepository _repository;
    private readonly ILogger<WallService> _logger;

    public const string GlobalCanvasKey = "canvas:global_state";
    public const string GlobalMinimapKey = "canvas:minimap_overview";
    public const int GlobalWidth = 10000;

    public WallService(
        IConnectionMultiplexer redis,
        CanvasRepository repository,
        ILogger<WallService> logger)
    {
        _redis = redis;
        _repository = repository;
        _logger = logger;
    }

    public string GetStateKey(Guid? wallId)
    {
        if (wallId == null || wallId == Guid.Empty)
            return GlobalCanvasKey;
        return $"canvas:wall:{wallId.Value}:state";
    }

    public string GetMinimapKey(Guid? wallId)
    {
        if (wallId == null || wallId == Guid.Empty)
            return GlobalMinimapKey;
        return $"canvas:wall:{wallId.Value}:minimap";
    }

    /// <summary>
    /// Ensures that a private wall's Redis buffer and minimap overview exist in 8-bit format.
    /// If not in Redis or size mismatch, rehydrates from SQL Server pixel history.
    /// </summary>
    public async Task EnsureWallBufferAsync(CanvasWall wall)
    {
        var db = _redis.GetDatabase();
        string stateKey = GetStateKey(wall.Id);
        string minimapKey = GetMinimapKey(wall.Id);

        int totalBytes = wall.Width * wall.Height;
        const int minimapBytes = 25600; // 160x160 @ 8-bit

        if (await db.KeyExistsAsync(stateKey))
        {
            long len = await db.StringLengthAsync(stateKey);
            if (len == totalBytes)
            {
                return;
            }
            _logger.LogInformation("Wall {WallId} buffer size mismatch ({Len} != {TotalBytes}). Rehydrating to 8-bit...", wall.Id, len, totalBytes);
        }

        _logger.LogInformation("Initializing 8-bit Redis buffer for private wall {WallId} '{WallName}' ({Width}x{Height})...",
            wall.Id, wall.Name, wall.Width, wall.Height);

        byte[] buffer = new byte[totalBytes];
        byte[] minimapBuffer = new byte[minimapBytes];

        var placements = await _repository.GetAllPixelPlacementsAsync(wall.Id);
        int count = 0;

        foreach (var p in placements)
        {
            if (p.X < 0 || p.X >= wall.Width || p.Y < 0 || p.Y >= wall.Height)
                continue;

            int pixelIndex = (p.Y * wall.Width) + p.X;
            buffer[pixelIndex] = p.ColorId;

            // Minimap (160x160)
            int mx = Math.Clamp((int)(p.X * 160.0 / wall.Width), 0, 159);
            int my = Math.Clamp((int)(p.Y * 160.0 / wall.Height), 0, 159);
            int mIndex = (my * 160) + mx;
            minimapBuffer[mIndex] = p.ColorId;

            count++;
        }

        await db.StringSetAsync(stateKey, buffer);
        await db.StringSetAsync(minimapKey, minimapBuffer);

        _logger.LogInformation("Private wall {WallId} initialized in Redis with {Count} historical pixels.", wall.Id, count);
    }
}
