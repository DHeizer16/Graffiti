using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

public class CanvasInitializerService : IHostedService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CanvasRepository _repository;
    private readonly ModerationService _moderationService;
    private readonly ReservationService _reservationService;
    private readonly PaletteService _paletteService;
    private readonly ILogger<CanvasInitializerService> _logger;

    private const string CanvasRedisKey = "canvas:global_state";
    private const string CanvasMinimapRedisKey = "canvas:minimap_overview";
    private const int TotalBytes = 100_000_000; // 10,000 x 10,000 @ 8 bits (1 byte) per pixel
    private const int MinimapBytes = 25_600;    // 160 x 160 @ 1 byte per pixel
    private const int Width = 10000;

    public CanvasInitializerService(
        IConnectionMultiplexer redis,
        CanvasRepository repository,
        ModerationService moderationService,
        ReservationService reservationService,
        PaletteService paletteService,
        ILogger<CanvasInitializerService> logger)
    {
        _redis = redis;
        _repository = repository;
        _moderationService = moderationService;
        _reservationService = reservationService;
        _paletteService = paletteService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 0. Ensure complete database schema exists and load active services
        await _repository.EnsureBaseSchemaAsync();
        await _repository.EnsureShadowBanSchemaAsync();
        await _repository.EnsureReservationSchemaAsync();
        await _repository.EnsureUserSchemaAsync();
        await _repository.EnsureTokenSchemaAsync();
        await _repository.EnsureWallSchemaAsync();
        await _repository.EnsurePaletteSchemaAsync();
        await _paletteService.InitializeAsync();
        await _moderationService.InitializeAsync();
        await _reservationService.InitializeAsync();

        var db = _redis.GetDatabase();

        // 1. Allocate or retrieve existing 100MB buffer in memory
        byte[] buffer;
        if (await db.KeyExistsAsync(CanvasRedisKey))
        {
            long len = await db.StringLengthAsync(CanvasRedisKey);
            if (len != TotalBytes)
            {
                _logger.LogInformation("Redis canvas buffer size ({Len} bytes) differs from 100MB target. Allocating 100MB and rehydrating...", len);
                buffer = new byte[TotalBytes];
            }
            else
            {
                _logger.LogInformation("Reading existing 100MB Redis canvas buffer...");
                buffer = (byte[]?)await db.StringGetAsync(CanvasRedisKey) ?? new byte[TotalBytes];
            }
        }
        else
        {
            _logger.LogInformation("Allocating 100MB empty Redis canvas buffer...");
            buffer = new byte[TotalBytes];
        }

        // 2. Rehydrate in-memory buffer with SQL Server pixel history
        _logger.LogInformation("Syncing Redis buffer with SQL Server pixel history (8-bit)...");
        var placements = await _repository.GetAllPixelPlacementsAsync();

        int rehydratedCount = 0;
        foreach (var pixel in placements)
        {
            if (pixel.X < 0 || pixel.X >= Width || pixel.Y < 0 || pixel.Y >= Width)
                continue;

            int pixelIndex = (pixel.Y * Width) + pixel.X;
            buffer[pixelIndex] = pixel.ColorId;
            rehydratedCount++;
        }

        // 3. Persist the rehydrated state to Redis in one single operation
        await db.StringSetAsync(CanvasRedisKey, buffer);
        _logger.LogInformation("Redis canvas rehydrated successfully with {Count} historical pixels (8-bit 100MB).", rehydratedCount);

        // 4. Rehydrate 160x160 Minimap Overview Buffer (25,600 bytes)
        byte[] minimapBuffer = new byte[MinimapBytes];
        foreach (var pixel in placements)
        {
            if (pixel.X < 0 || pixel.X >= Width || pixel.Y < 0 || pixel.Y >= Width)
                continue;

            int mx = Math.Clamp((int)(pixel.X * 160.0 / Width), 0, 159);
            int my = Math.Clamp((int)(pixel.Y * 160.0 / Width), 0, 159);
            int mIndex = (my * 160) + mx;
            minimapBuffer[mIndex] = pixel.ColorId;
        }

        await db.StringSetAsync(CanvasMinimapRedisKey, minimapBuffer);
        _logger.LogInformation("Minimap overview buffer (160x160, 25.6KB) initialized in Redis.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}