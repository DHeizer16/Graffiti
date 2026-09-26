using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using GlobalGraffitiWall.API.Telemetry;

namespace GlobalGraffitiWall.API;

public class CanvasHub : Hub
{
    private readonly IConnectionMultiplexer _redis;
    private readonly PixelPlacementQueue _queue;
    private readonly ILogger<CanvasHub> _logger;
    private readonly double _maxCapacity;
    private readonly double _refillRate;

    private const string CanvasRedisKey = "canvas:global_state";
    private const string CanvasMinimapRedisKey = "canvas:minimap_overview";
    private const int Width = 10000;

    // Atomic Token Bucket Lua Script
    private const string RateLimitLuaScript = """
        local max_capacity = tonumber(ARGV[1]) or 16
        local refill_rate = tonumber(ARGV[2]) or 0.1
        local now = tonumber(ARGV[3])
        local requested = tonumber(ARGV[4]) or 1

        local current_tokens = tonumber(redis.call('GET', KEYS[1])) or max_capacity
        local last_update = tonumber(redis.call('GET', KEYS[2])) or now
        local bonus_balance = tonumber(redis.call('GET', KEYS[3])) or 0

        local elapsed = math.max(0, now - last_update)
        local regenerated = elapsed * refill_rate
        local new_tokens = math.min(max_capacity, current_tokens + regenerated)

        if new_tokens >= requested then
            new_tokens = new_tokens - requested
            redis.call('SET', KEYS[1], string.format("%.6f", new_tokens))
            redis.call('SET', KEYS[2], tostring(math.floor(now)))
            return {1, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), 0, 0}
        elseif bonus_balance >= requested then
            bonus_balance = bonus_balance - requested
            redis.call('SET', KEYS[1], string.format("%.6f", new_tokens))
            redis.call('SET', KEYS[2], tostring(math.floor(now)))
            redis.call('SET', KEYS[3], tostring(math.floor(bonus_balance)))
            return {1, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), 0, 1}
        else
            local missing = requested - new_tokens
            local wait_seconds = math.ceil(missing / refill_rate)
            return {0, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), wait_seconds, 0}
        end
        """;

    private readonly ModerationService _moderationService;
    private readonly ReservationService _reservationService;
    private readonly CanvasRepository _repository;
    private readonly WallService _wallService;
    private readonly PaletteService _paletteService;
    private readonly CanvasMetrics _metrics;

    public CanvasHub(
        IConnectionMultiplexer redis,
        PixelPlacementQueue queue,
        ModerationService moderationService,
        ReservationService reservationService,
        CanvasRepository repository,
        WallService wallService,
        PaletteService paletteService,
        CanvasMetrics metrics,
        ILogger<CanvasHub> logger,
        IConfiguration configuration)
    {
        _redis = redis;
        _queue = queue;
        _moderationService = moderationService;
        _reservationService = reservationService;
        _repository = repository;
        _wallService = wallService;
        _paletteService = paletteService;
        _metrics = metrics;
        _logger = logger;
        _maxCapacity = configuration.GetValue<double>("CanvasSettings:MaxCapacity", 16);
        _refillRate = configuration.GetValue<double>("CanvasSettings:RefillRatePerSecond", 0.2);
    }

    public override async Task OnConnectedAsync()
    {
        _metrics.IncrementConnection();
        string? wallIdStr = Context.GetHttpContext()?.Request.Query["wallId"].ToString();
        string groupName = GetGroupName(wallIdStr);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _metrics.DecrementConnection();
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinWall(string? wallIdStr)
    {
        string groupName = GetGroupName(wallIdStr);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task LeaveWall(string? wallIdStr)
    {
        string groupName = GetGroupName(wallIdStr);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    private static string GetGroupName(string? wallIdStr)
    {
        return (Guid.TryParse(wallIdStr, out var id) && id != Guid.Empty)
            ? $"wall:{id}"
            : "wall:global";
    }

    public Task<PlacementResult> PlacePixel(int x, int y, byte colorId)
    {
        return PlacePixelCore(x, y, colorId, null, null, null);
    }

    public Task<PlacementResult> PlacePixelWithKey(int x, int y, byte colorId, string? reservationKey)
    {
        return PlacePixelCore(x, y, colorId, reservationKey, null, null);
    }

    public Task<PlacementResult> PlacePixelOnWall(int x, int y, byte colorId, string? wallIdStr, string? wallAccessKey, string? reservationKey)
    {
        return PlacePixelCore(x, y, colorId, reservationKey, wallIdStr, wallAccessKey);
    }

    private async Task<PlacementResult> PlacePixelCore(int x, int y, byte colorId, string? reservationKey, string? wallIdStr, string? wallAccessKey)
    {
        Guid? wallGuid = null;
        CanvasWall? wall = null;
        int wallWidth = Width;
        int wallHeight = Width;
        string stateKey = CanvasRedisKey;
        string minimapKey = CanvasMinimapRedisKey;

        string clientUserId = GetEffectiveUserId();
        bool isOwner = false;

        if (Guid.TryParse(wallIdStr, out var parsedWallId) && parsedWallId != Guid.Empty)
        {
            wall = await _repository.GetWallByIdAsync(parsedWallId);
            if (wall == null) throw new HubException("Canvas wall not found.");

            wallGuid = wall.Id;
            wallWidth = wall.Width;
            wallHeight = wall.Height;
            stateKey = _wallService.GetStateKey(wall.Id);
            minimapKey = _wallService.GetMinimapKey(wall.Id);

            isOwner = !string.IsNullOrEmpty(clientUserId) && 
                      (wall.OwnerId.ToString().Equals(clientUserId, StringComparison.OrdinalIgnoreCase) ||
                       Context.User?.IsInRole("Admin") == true);

            // Access permission checks
            if (wall.AccessType == "Private" && !isOwner)
            {
                throw new HubException("This wall is private. Only the wall owner can paint here.");
            }

            if (wall.AccessType == "Password" && !isOwner)
            {
                if (string.IsNullOrWhiteSpace(wallAccessKey) || !string.Equals(wall.AccessKey, wallAccessKey.Trim(), StringComparison.Ordinal))
                {
                    throw new HubException("Password-protected wall. Incorrect access key.");
                }
            }

            await _wallService.EnsureWallBufferAsync(wall);
        }

        // 1. Boundary & Palette Validation
        if (x < 0 || x >= wallWidth || y < 0 || y >= wallHeight)
            throw new HubException($"Coordinates out of bounds for canvas (Max: {wallWidth - 1}x{wallHeight - 1}).");

        if (!await _paletteService.IsColorActiveAsync(colorId))
            throw new HubException($"Color ID {colorId} is not an active palette color.");

        // 2. Identify User Connection / IP / Client User ID
        string userKeyIdentifier = clientUserId;
        string ipAddress = Context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        // 3. Check Territory Reservation Permission (only on global wall or where reservations apply)
        if (wallGuid == null)
        {
            var reservationCheck = _reservationService.CheckPlacementPermission(x, y, clientUserId, reservationKey);
            if (reservationCheck.IsReserved && !reservationCheck.IsAuthorized)
            {
                throw new HubException(reservationCheck.Reason ?? "This area is reserved.");
            }
        }

        // 4. Rate Limit (Wall owners painting on their own wall get free painting!)
        var db = _redis.GetDatabase();
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double remainingTokens = _maxCapacity;
        int bonusBalance = 0;
        bool usedBonus = false;

        if (!isOwner)
        {
            RedisKey[] rateLimitKeys = new RedisKey[]
            {
                $"user:{userKeyIdentifier}:tokens",
                $"user:{userKeyIdentifier}:last_update",
                $"user:{userKeyIdentifier}:bonus_balance"
            };

            RedisValue[] rateLimitValues = new RedisValue[]
            {
                _maxCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _refillRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nowUnix.ToString(),
                "1"
            };

            var rawResult = await db.ScriptEvaluateAsync(RateLimitLuaScript, rateLimitKeys, rateLimitValues);
            var result = (RedisResult[]?)rawResult;

            if (result != null && result.Length >= 4 && (int)result[0] == 0)
            {
                int waitSeconds = (int)result[3];
                throw new HubException($"Rate limit reached! You must wait {waitSeconds} seconds for your next charge.");
            }

            if (result != null && result.Length >= 5)
            {
                usedBonus = (int)result[4] == 1;
            }

            if (result != null && result.Length >= 3)
            {
                if (double.TryParse((string?)result[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                    remainingTokens = t;
                if (int.TryParse((string?)result[2], out var b))
                    bonusBalance = b;
            }
        }

        // 5. Check Shadow Ban Status & IP Abuse Tripwire
        bool isShadowBanned = await _moderationService.IsShadowBannedAsync(userKeyIdentifier, ipAddress);
        if (!isShadowBanned && await _moderationService.CheckIpAbuseAndAutoQuarantineAsync(ipAddress))
        {
            isShadowBanned = true;
        }

        Guid userGuid = Guid.TryParse(clientUserId, out var parsedGuid) ? parsedGuid : Guid.NewGuid();
        var placedAt = DateTimeOffset.UtcNow;
        var item = new PixelPlacementItem(x, y, colorId, userGuid, ipAddress, placedAt, isShadowBanned, wallGuid, usedBonus);

        if (isShadowBanned)
        {
            _logger.LogWarning("Shadow-banned pixel placement intercepted at ({X}, {Y}) on wall {Wall} from {User}",
                x, y, wallGuid?.ToString() ?? "Global", userKeyIdentifier);

            if (!_queue.TryEnqueue(item))
            {
                await _queue.EnqueueAsync(item);
            }

            await Clients.Caller.SendAsync("PixelUpdated", new
            {
                x,
                y,
                colorId,
                timestamp = placedAt,
                wallId = wallGuid
            });
        }
        else
        {
            // Mutate Redis Byte Buffer atomically via Lua
            await UpdateRedisPixelBufferAsync(db, stateKey, minimapKey, wallWidth, x, y, colorId);
            _metrics.RecordPixelPlaced(wallGuid?.ToString() ?? "Global");

            if (!_queue.TryEnqueue(item))
            {
                await _queue.EnqueueAsync(item);
            }

            // Broadcast to the specific wall group (wall:global or wall:{wallId})
            string groupName = wallGuid != null ? $"wall:{wallGuid.Value}" : "wall:global";
            await Clients.Group(groupName).SendAsync("PixelUpdated", new
            {
                x,
                y,
                colorId,
                timestamp = placedAt,
                wallId = wallGuid
            });


            _logger.LogInformation("Pixel placed at ({X}, {Y}) on wall {Wall} by {User}", x, y, wallGuid?.ToString() ?? "Global", userKeyIdentifier);
        }

        return new PlacementResult
        {
            Success = true,
            RemainingTokens = remainingTokens,
            BonusBalance = bonusBalance,
            MaxCapacity = _maxCapacity,
            RefillRate = _refillRate,
            WaitSeconds = 0
        };
    }

    private string GetEffectiveUserId()
    {
        var authUserId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(authUserId))
            return authUserId;

        string queryUserId = Context.GetHttpContext()?.Request.Query["userId"].ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(queryUserId))
            return queryUserId;

        return Context.ConnectionId;
    }

    public async Task<PlacementResult> GetChargeStatus()
    {
        string userKeyIdentifier = GetEffectiveUserId();
        var db = _redis.GetDatabase();
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        RedisKey[] rateLimitKeys = new RedisKey[]
        {
            $"user:{userKeyIdentifier}:tokens",
            $"user:{userKeyIdentifier}:last_update",
            $"user:{userKeyIdentifier}:bonus_balance"
        };

        RedisValue[] rateLimitValues = new RedisValue[]
        {
            _maxCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _refillRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nowUnix.ToString(),
            "0" // requested = 0 (read current token state without deduction)
        };

        var rawResult = await db.ScriptEvaluateAsync(RateLimitLuaScript, rateLimitKeys, rateLimitValues);
        var result = (RedisResult[]?)rawResult;

        double remainingTokens = _maxCapacity;
        int bonusBalance = 0;
        if (result != null && result.Length >= 3)
        {
            if (double.TryParse((string?)result[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                remainingTokens = t;
            if (int.TryParse((string?)result[2], out var b))
                bonusBalance = b;
        }

        return new PlacementResult
        {
            Success = true,
            RemainingTokens = remainingTokens,
            BonusBalance = bonusBalance,
            MaxCapacity = _maxCapacity,
            RefillRate = _refillRate,
            WaitSeconds = 0
        };
    }

    private async Task UpdateRedisPixelBufferAsync(IDatabase db, string stateKey, string minimapKey, int width, int x, int y, byte colorId)
    {
        int pixelIndex = (y * width) + x;
        byte[] colorBytes = [colorId];

        await db.StringSetRangeAsync(stateKey, pixelIndex, colorBytes);

        // Update 160x160 Minimap Overview in Redis (8-bit)
        int mx = Math.Clamp((int)(x * 160.0 / width), 0, 159);
        int my = Math.Clamp((int)(y * 160.0 / width), 0, 159);
        int mIndex = (my * 160) + mx;

        await db.StringSetRangeAsync(minimapKey, mIndex, colorBytes);
    }
}