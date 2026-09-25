using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

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
    private readonly ILogger<ReservationService> _logger;
    private readonly ConcurrentDictionary<Guid, CanvasReservation> _activeReservations = new();

    private const int MinDimension = 5;
    private const int MaxDimension = 128;
    private const int MaxDurationMinutes = 1440; // 24 hours
    private const int MinDurationMinutes = 5;
    private const int MaxActivePerOwner = 1;
    private const int CanvasWidth = 10000;

    public ReservationService(
        CanvasRepository repository,
        IHubContext<CanvasHub> hubContext,
        ILogger<ReservationService> logger)
    {
        _repository = repository;
        _hubContext = hubContext;
        _logger = logger;
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

        // 7. Generate collaborator secret key
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
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(duration),
            IsActive = true
        };

        // 8. Persist to SQL Server
        await _repository.InsertReservationAsync(reservation);

        // 9. Store in active memory
        _activeReservations[reservation.ReservationId] = reservation;

        var dto = reservation.ToDto(now);

        // 10. Broadcast to all SignalR clients
        await _hubContext.Clients.All.SendAsync("ZoneReserved", dto);

        _logger.LogInformation("Territory reservation '{Label}' ({W}x{H}) created at ({X1},{Y1}) by {Owner}. Expires at {Exp}.",
            reservation.Label ?? "Unnamed", width, height, x1, y1, ownerName, reservation.ExpiresAt);

        return new ReservationCreatedResponse
        {
            Success = true,
            Message = "Territory successfully reserved! Share the secret key with collaborators so they can paint in this zone.",
            Reservation = dto,
            SecretKey = secretKey
        };
    }

    /// <summary>
    /// Cancels or releases an active reservation early.
    /// </summary>
    public async Task<(bool Success, string Message)> CancelReservationAsync(Guid reservationId, string? ownerId, string? secretKey)
    {
        CanvasReservation? reservation;
        if (!_activeReservations.TryGetValue(reservationId, out reservation))
        {
            // Fallback to database check in case memory cache doesn't hold it
            reservation = await _repository.GetReservationByIdAsync(reservationId);
            if (reservation == null || !reservation.IsActive)
            {
                return (false, "Reservation not found or has already expired/been released.");
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
            return (false, "Invalid credentials. You must provide the owner identifier or collaborator secret key.");
        }

        await _repository.DeactivateReservationAsync(reservationId);
        _activeReservations.TryRemove(reservationId, out _);

        await _hubContext.Clients.All.SendAsync("ZoneExpired", reservationId);

        _logger.LogInformation("Reservation {Id} released early by owner/collaborator.", reservationId);
        return (true, "Territory reservation released.");
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
}
