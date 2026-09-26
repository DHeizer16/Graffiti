using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

public class ReservationCheckResult
{
    public bool IsReserved { get; set; }
    public bool IsAuthorized { get; set; }
    public CanvasReservation? Reservation { get; set; }
    public string? Reason { get; set; }

    public static ReservationCheckResult NotReserved() => new() { IsReserved = false, IsAuthorized = true };
    public static ReservationCheckResult Allowed(CanvasReservation r) => new() { IsReserved = true, IsAuthorized = true, Reservation = r };
    public static ReservationCheckResult Blocked(CanvasReservation r, string reason) => new() { IsReserved = true, IsAuthorized = false, Reservation = r, Reason = reason };
}

public class ReservationService
{
    private readonly CanvasRepository _repository;
    private readonly IHubContext<CanvasHub> _hubContext;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ReservationService> _logger;
    private readonly ConcurrentDictionary<Guid, CanvasReservation> _activeReservations = new();

    private readonly double _maxCapacity;
    private readonly double _refillRate;

    private const int MinDimension = 5;
    private const int MaxDimension = 128;
    private const int MaxDurationMinutes = 1440; // 24 hours
    private const int MinDurationMinutes = 5;
    private const int MaxActivePerOwner = 1;
    private const int CanvasWidth = 10000;

    // Atomic Reservation Token Deduction Lua Script
    private const string ReservationDeductLuaScript = """
        local max_capacity = tonumber(ARGV[1]) or 16
        local refill_rate = tonumber(ARGV[2]) or 0.2
        local now = tonumber(ARGV[3])
        local cost = tonumber(ARGV[4]) or 0

        local current_tokens = tonumber(redis.call('GET', KEYS[1])) or max_capacity
        local last_update = tonumber(redis.call('GET', KEYS[2])) or now
        local bonus_balance = tonumber(redis.call('GET', KEYS[3])) or 0

        local elapsed = math.max(0, now - last_update)
        local regenerated = elapsed * refill_rate
        local new_tokens = math.min(max_capacity, current_tokens + regenerated)

        local total_available = math.floor(new_tokens) + bonus_balance
        if total_available < cost then
            return {0, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), 0}
        end

        local deduct_normal = math.min(math.floor(new_tokens), cost)
        new_tokens = new_tokens - deduct_normal
        local remaining_cost = cost - deduct_normal

        if remaining_cost > 0 then
            bonus_balance = bonus_balance - remaining_cost
        end

        redis.call('SET', KEYS[1], string.format("%.6f", new_tokens))
        redis.call('SET', KEYS[2], tostring(math.floor(now)))
        redis.call('SET', KEYS[3], tostring(math.floor(bonus_balance)))

        return {1, string.format("%.2f", new_tokens), tostring(math.floor(bonus_balance)), tostring(math.floor(remaining_cost))}
        """;

    public ReservationService(
        CanvasRepository repository,
        IHubContext<CanvasHub> hubContext,
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        ILogger<ReservationService> logger)
    {
        _repository = repository;
        _hubContext = hubContext;
        _redis = redis;
        _logger = logger;
        _maxCapacity = configuration.GetValue<double>("CanvasSettings:MaxCapacity", 16);
        _refillRate = configuration.GetValue<double>("CanvasSettings:RefillRatePerSecond", 0.2);
    }

    /// <summary>
    /// Calculates the token cost for a territory reservation lease based on area and duration.
    /// </summary>
    public static int CalculateCost(int width, int height, int durationMinutes)
    {
        int area = Math.Max(25, width * height);
        double durationMultiplier = durationMinutes switch
        {
            <= 15 => 0.4,
            <= 60 => 1.0,
            <= 240 => 2.4,
            <= 720 => 3.8,
            _ => 5.0
        };
        double areaBase = 4.0 + (area / 500.0);
        return (int)Math.Max(2, Math.Round(areaBase * durationMultiplier));
    }

    /// <summary>
    /// Ensures schema exists and loads active reservations into memory.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing Canvas Area Reservations...");
        await _repository.EnsureReservationSchemaAsync();

        var activeList = await _repository.GetActiveReservationsAsync();
        var now = DateTimeOffset.UtcNow;
        int loaded = 0;

        foreach (var res in activeList)
        {
            if (!res.IsExpired(now))
            {
                _activeReservations[res.ReservationId] = res;
                loaded++;
            }
        }

        _logger.LogInformation("Loaded {Count} active canvas reservations into memory.", loaded);
    }

    /// <summary>
    /// Fast in-memory check to determine if a pixel coordinate is inside a reserved zone,
    /// and if the caller is authorized to paint there.
    /// Sub-millisecond execution complexity.
    /// </summary>
    public ReservationCheckResult CheckPlacementPermission(int x, int y, string userId, string? secretKey)
    {
        if (_activeReservations.IsEmpty)
            return ReservationCheckResult.NotReserved();

        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _activeReservations)
        {
            var res = kvp.Value;
            if (res.IsExpired(now))
                continue;

            if (res.Contains(x, y))
            {
                // Check if caller is owner
                bool isOwner = !string.IsNullOrEmpty(userId) && string.Equals(res.OwnerId, userId, StringComparison.OrdinalIgnoreCase);

                // Check if caller has matching secret key (single key or candidate list)
                bool hasValidKey = false;
                if (!string.IsNullOrWhiteSpace(secretKey))
                {
                    var keys = secretKey.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var k in keys)
                    {
                        if (string.Equals(res.SecretKey, k.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            hasValidKey = true;
                            break;
                        }
                    }
                }

                if (isOwner || hasValidKey)
                {
                    return ReservationCheckResult.Allowed(res);
                }

                string zoneLabel = string.IsNullOrWhiteSpace(res.Label) ? "Reserved Zone" : $"'{res.Label}'";
                int minsLeft = Math.Max(1, (int)(res.ExpiresAt - now).TotalMinutes);
                string reason = $"This area is part of {zoneLabel} reserved by {res.OwnerName} (expires in {minsLeft}m). You cannot paint here without the collaborator key.";

                return ReservationCheckResult.Blocked(res, reason);
            }
        }

        return ReservationCheckResult.NotReserved();
    }

    /// <summary>
    /// Creates a new canvas territory reservation with dimension and conflict validation.
    /// </summary>
    public async Task<ReservationCreatedResponse> CreateReservationAsync(CreateReservationRequest request)
    {
        // 1. Normalize coordinates
        int x1 = Math.Min(request.X1, request.X2);
        int x2 = Math.Max(request.X1, request.X2);
        int y1 = Math.Min(request.Y1, request.Y2);
        int y2 = Math.Max(request.Y1, request.Y2);

        int width = x2 - x1 + 1;
        int height = y2 - y1 + 1;

        // 2. Validate boundaries
        if (x1 < 0 || x2 >= CanvasWidth || y1 < 0 || y2 >= CanvasWidth)
        {
            return new ReservationCreatedResponse { Success = false, Message = "Coordinates are outside canvas boundaries (0-9999)." };
        }

        // 3. Validate dimensions
        if (width < MinDimension || height < MinDimension)
        {
            return new ReservationCreatedResponse { Success = false, Message = $"Minimum reservation size is {MinDimension}x{MinDimension} pixels." };
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            return new ReservationCreatedResponse { Success = false, Message = $"Maximum reservation size is {MaxDimension}x{MaxDimension} pixels (requested {width}x{height})." };
        }

        // 4. Validate duration
        int duration = Math.Clamp(request.DurationMinutes, MinDurationMinutes, MaxDurationMinutes);

        // 5. Check owner quota
        string ownerId = string.IsNullOrWhiteSpace(request.OwnerId) ? Guid.NewGuid().ToString() : request.OwnerId.Trim();
        string ownerName = string.IsNullOrWhiteSpace(request.OwnerName) ? "Anonymous" : request.OwnerName.Trim();

        int currentCount = await _repository.GetActiveReservationCountForOwnerAsync(ownerId);
        if (currentCount >= MaxActivePerOwner)
        {
            return new ReservationCreatedResponse
            {
                Success = false,
                Message = $"You already have an active reservation. You can only maintain {MaxActivePerOwner} active territory at a time."
            };
        }

        // 6. Check for overlaps with existing active reservations
        var now = DateTimeOffset.UtcNow;
        foreach (var existing in _activeReservations.Values)
        {
            if (!existing.IsExpired(now) && existing.Overlaps(x1, y1, x2, y2))
            {
                string existingLabel = string.IsNullOrWhiteSpace(existing.Label) ? "Another territory" : $"Zone '{existing.Label}'";
                return new ReservationCreatedResponse
                {
                    Success = false,
                    Message = $"Your selection overlaps with {existingLabel} held by {existing.OwnerName} until {existing.ExpiresAt:HH:mm UTC}."
                };
            }
        }

        // 7. Check Wall Exemption & Token Cost
        int tokenCost = 0;
        bool isWallOwner = false;
        if (!string.IsNullOrEmpty(request.WallId) && Guid.TryParse(request.WallId, out var wallGuid))
        {
            var wall = await _repository.GetWallByIdAsync(wallGuid);
            if (wall != null && string.Equals(wall.OwnerId.ToString(), ownerId, StringComparison.OrdinalIgnoreCase))
            {
                isWallOwner = true;
            }
        }

        if (!isWallOwner)
        {
            tokenCost = CalculateCost(width, height, duration);
        }

        int remainingBonusTokens = 0;
        if (tokenCost > 0)
        {
            var db = _redis.GetDatabase();
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            RedisKey[] keys = new RedisKey[]
            {
                $"user:{ownerId}:tokens",
                $"user:{ownerId}:last_update",
                $"user:{ownerId}:bonus_balance"
            };

            RedisValue[] values = new RedisValue[]
            {
                _maxCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _refillRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nowUnix.ToString(),
                tokenCost.ToString()
            };

            var rawResult = await db.ScriptEvaluateAsync(ReservationDeductLuaScript, keys, values);
            var result = (RedisResult[]?)rawResult;

            if (result == null || result.Length < 4 || (int)result[0] == 0)
            {
                int availableNormal = (result != null && result.Length >= 2 && double.TryParse((string?)result[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) ? (int)Math.Floor(t) : 0;
                int availableBonus = (result != null && result.Length >= 3 && int.TryParse((string?)result[2], out var b)) ? b : 0;
                int totalAvailable = availableNormal + availableBonus;

                return new ReservationCreatedResponse
                {
                    Success = false,
                    TokenCost = tokenCost,
                    Message = $"Insufficient tokens! This territory lease requires {tokenCost} tokens, but you only have {totalAvailable} ({availableNormal} charges + {availableBonus} bonus). Claim your daily supply drop or choose a smaller area / shorter duration."
                };
            }

            if (int.TryParse((string?)result[2], out var newBonus))
            {
                remainingBonusTokens = newBonus;
            }

            if (int.TryParse((string?)result[3], out var bonusDeducted) && bonusDeducted > 0)
            {
                if (Guid.TryParse(ownerId, out var userGuid))
                {
                    await _repository.DeductBonusTokensAsync(userGuid, bonusDeducted, "ZONE_RESERVE_DEBIT", $"Territory lease for '{request.Label ?? "Zone"}' ({width}x{height}, {duration}m)");
                }
            }
        }

        // 8. Generate collaborator secret key
        string secretKey = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        var reservation = new CanvasReservation
        {
            ReservationId = Guid.NewGuid(),
            OwnerId = ownerId,
            OwnerName = ownerName,
            SecretKey = secretKey,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            TokenCost = tokenCost,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(duration),
            IsActive = true
        };

        // 9. Persist to SQL Server
        await _repository.InsertReservationAsync(reservation);

        // 10. Store in active memory
        _activeReservations[reservation.ReservationId] = reservation;

        var dto = reservation.ToDto(now);

        // 11. Broadcast to all SignalR clients
        await _hubContext.Clients.All.SendAsync("ZoneReserved", dto);

        _logger.LogInformation("Territory reservation '{Label}' ({W}x{H}) created at ({X1},{Y1}) by {Owner} for {Cost} tokens. Expires at {Exp}.",
            reservation.Label ?? "Unnamed", width, height, x1, y1, ownerName, tokenCost, reservation.ExpiresAt);

        return new ReservationCreatedResponse
        {
            Success = true,
            Message = tokenCost > 0
                ? $"Territory successfully leased for {tokenCost} tokens! Share the secret key with collaborators so they can paint in this zone."
                : "Territory successfully reserved! Share the secret key with collaborators so they can paint in this zone.",
            Reservation = dto,
            SecretKey = secretKey,
            TokenCost = tokenCost,
            RemainingBonusTokens = remainingBonusTokens
        };
    }

    /// <summary>
    /// Cancels or releases an active reservation early, issuing a 50% prorated refund of remaining time.
    /// </summary>
    public async Task<(bool Success, string Message, int RefundedTokens, int NewBonusBalance)> CancelReservationAsync(Guid reservationId, string? ownerId, string? secretKey)
    {
        CanvasReservation? reservation;
        if (!_activeReservations.TryGetValue(reservationId, out reservation))
        {
            // Fallback to database check in case memory cache doesn't hold it
            reservation = await _repository.GetReservationByIdAsync(reservationId);
            if (reservation == null || !reservation.IsActive)
            {
                return (false, "Reservation not found or has already expired/been released.", 0, 0);
            }
        }

        string cleanOwner = ownerId?.Trim() ?? string.Empty;
        string cleanKey = secretKey?.Trim() ?? string.Empty;

        bool isOwner = !string.IsNullOrEmpty(cleanOwner) && string.Equals(reservation.OwnerId, cleanOwner, StringComparison.OrdinalIgnoreCase);

        bool hasKey = false;
        if (!string.IsNullOrEmpty(cleanKey))
        {
            var keys = cleanKey.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var k in keys)
            {
                if (string.Equals(reservation.SecretKey, k.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    hasKey = true;
                    break;
                }
            }
        }

        if (!isOwner && !hasKey)
        {
            return (false, "Invalid credentials. You must provide the owner identifier or collaborator secret key.", 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        int refundedTokens = 0;
        int newBonusBalance = 0;

        // Calculate 50% prorated refund for early release if tokens were spent
        if (reservation.TokenCost > 0 && reservation.ExpiresAt > now)
        {
            var totalDuration = (reservation.ExpiresAt - reservation.CreatedAt).TotalSeconds;
            var remainingDuration = (reservation.ExpiresAt - now).TotalSeconds;
            if (totalDuration > 0 && remainingDuration > 0)
            {
                double unusedRatio = remainingDuration / totalDuration;
                refundedTokens = (int)Math.Floor(reservation.TokenCost * unusedRatio * 0.5); // 50% prorated refund
            }
        }

        if (refundedTokens > 0)
        {
            var db = _redis.GetDatabase();
            long updatedBonus = await db.StringIncrementAsync($"user:{reservation.OwnerId}:bonus_balance", refundedTokens);
            newBonusBalance = (int)updatedBonus;

            if (Guid.TryParse(reservation.OwnerId, out var ownerGuid))
            {
                await _repository.AddBonusTokensAsync(ownerGuid, refundedTokens, "ZONE_RELEASE_REFUND", $"Early release refund for territory '{reservation.Label ?? "Zone"}'", reservation.ReservationId.ToString());
            }
        }

        await _repository.DeactivateReservationWithRefundAsync(reservationId, refundedTokens);
        _activeReservations.TryRemove(reservationId, out _);

        await _hubContext.Clients.All.SendAsync("ZoneExpired", reservationId);

        _logger.LogInformation("Reservation {Id} released early by owner/collaborator. Refunded {Refund} tokens.", reservationId, refundedTokens);
        string message = refundedTokens > 0
            ? $"Territory released early! {refundedTokens} bonus tokens refunded to your bank. ⚡"
            : "Territory reservation released.";

        return (true, message, refundedTokens, newBonusBalance);
    }

    /// <summary>
    /// Validates a collaborator secret key against active reservations and returns the corresponding reservation details.
    /// </summary>
    public async Task<(bool Success, string Message, ReservationDto? Reservation)> LinkKeyAsync(string secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            return (false, "Please provide a valid collaborator key.", null);
        }

        string cleanKey = secretKey.Trim().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;

        // 1. Check in-memory active reservations first
        foreach (var res in _activeReservations.Values)
        {
            if (!res.IsExpired(now) && string.Equals(res.SecretKey, cleanKey, StringComparison.OrdinalIgnoreCase))
            {
                return (true, "Collaborator key validated and territory linked successfully!", res.ToDto(now));
            }
        }

        // 2. Fallback to SQL database if memory cache missed
        var dbRes = await _repository.GetActiveReservationBySecretKeyAsync(cleanKey);
        if (dbRes != null && !dbRes.IsExpired(now))
        {
            _activeReservations[dbRes.ReservationId] = dbRes;
            return (true, "Collaborator key validated and territory linked successfully!", dbRes.ToDto(now));
        }

        return (false, "Collaborator key is invalid or this territory reservation has already expired.", null);
    }

    /// <summary>
    /// Retrieves all currently active, non-expired reservations for public consumption.
    /// </summary>
    public IEnumerable<ReservationDto> GetActiveReservations()
    {
        var now = DateTimeOffset.UtcNow;
        return _activeReservations.Values
            .Where(r => !r.IsExpired(now))
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.ToDto(now));
    }

    /// <summary>
    /// Background check to prune expired reservations from memory and broadcast expiration events.
    /// </summary>
    public async Task PruneExpiredReservationsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredIds = new List<Guid>();

        foreach (var kvp in _activeReservations)
        {
            if (kvp.Value.IsExpired(now))
            {
                expiredIds.Add(kvp.Key);
            }
        }

        if (expiredIds.Count > 0)
        {
            foreach (var id in expiredIds)
            {
                if (_activeReservations.TryRemove(id, out var res))
                {
                    _logger.LogInformation("Reservation {Id} ('{Label}') has expired. Removing from active zones.", id, res.Label);
                    await _hubContext.Clients.All.SendAsync("ZoneExpired", id);
                }
            }

            await _repository.ExpireOldReservationsAsync(now);
        }
    }

    /// <summary>
    /// Releases and expires all active reservations on the primary global wall (WallId == null).
    /// Used during canvas season resets.
    /// </summary>
    public async Task ReleaseAllGlobalWallReservationsAsync()
    {
        var globalReservations = _activeReservations.Values.Where(r => r.WallId == null).ToList();
        foreach (var res in globalReservations)
        {
            if (_activeReservations.TryRemove(res.ReservationId, out _))
            {
                _logger.LogInformation("Global wall reservation {Id} ('{Label}') released due to canvas reset.", res.ReservationId, res.Label);
                await _hubContext.Clients.All.SendAsync("ZoneExpired", res.ReservationId);
            }
        }
        await _repository.DeactivateAllGlobalReservationsAsync();
    }
}

