using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

public class CanvasResetService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CanvasRepository _repository;
    private readonly ReservationService _reservationService;
    private readonly IHubContext<CanvasHub> _hubContext;
    private readonly ILogger<CanvasResetService> _logger;

    private const string RedisKeyScheduledUtc = "canvas:reset:scheduled_utc";
    private const string RedisKeyMessage = "canvas:reset:message";
    private const string RedisKeyScheduledBy = "canvas:reset:scheduled_by";
    private const string RedisKeySeasonNumber = "canvas:global_season_number";
    private const string RedisKeySeasonName = "canvas:global_season_name";
    private const string RedisKeySeasonEpoch = "canvas:global_season_epoch_utc";

    private const string CanvasRedisKey = "canvas:global_state";
    private const string CanvasMinimapRedisKey = "canvas:minimap_overview";
    private const int TotalBytes = 100_000_000;
    private const int MinimapBytes = 25_600;

    private readonly SemaphoreSlim _resetLock = new(1, 1);

    public CanvasResetService(
        IConnectionMultiplexer redis,
        CanvasRepository repository,
        ReservationService reservationService,
        IHubContext<CanvasHub> hubContext,
        ILogger<CanvasResetService> logger)
    {
        _redis = redis;
        _repository = repository;
        _reservationService = reservationService;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes Redis state with active season and scheduled resets from SQL Server on boot.
    /// </summary>
    public async Task InitializeAsync()
    {
        var db = _redis.GetDatabase();

        // 1. Sync Current Season
        var currentSeason = await _repository.GetCurrentSeasonAsync();
        await db.StringSetAsync(RedisKeySeasonNumber, currentSeason.SeasonNumber);
        await db.StringSetAsync(RedisKeySeasonName, currentSeason.Name);
        await db.StringSetAsync(RedisKeySeasonEpoch, currentSeason.StartedAt.ToString("o"));

        _logger.LogInformation("Canvas Season initialized: #{Number} '{Name}' (Started: {StartedAt:u})",
            currentSeason.SeasonNumber, currentSeason.Name, currentSeason.StartedAt);

        // 2. Sync Active Scheduled Reset if one is still pending in SQL
        var scheduled = await _repository.GetActiveScheduledResetAsync();
        if (scheduled != null && scheduled.ScheduledResetUtc > DateTimeOffset.UtcNow)
        {
            await db.StringSetAsync(RedisKeyScheduledUtc, scheduled.ScheduledResetUtc.ToString("o"));
            await db.StringSetAsync(RedisKeyMessage, scheduled.AnnouncementMessage ?? string.Empty);
            await db.StringSetAsync(RedisKeyScheduledBy, scheduled.ScheduledBy);

            _logger.LogInformation("Active scheduled reset synchronized: Target {Target:u} by {Admin}",
                scheduled.ScheduledResetUtc, scheduled.ScheduledBy);
        }
        else if (scheduled != null && scheduled.ScheduledResetUtc <= DateTimeOffset.UtcNow)
        {
            // Wipe was scheduled for the past while server was down -> execute now
            _logger.LogWarning("Scheduled reset time {Target:u} passed while server was offline. Executing reset now...",
                scheduled.ScheduledResetUtc);
            await ExecuteResetAsync(scheduled.ScheduledBy, "Executing pending reset from server offline recovery.");
        }
    }

    /// <summary>
    /// Returns the current reset countdown status and active season details.
    /// </summary>
    public async Task<CanvasResetStatusDto> GetResetStatusAsync()
    {
        var db = _redis.GetDatabase();

        var scheduledUtcStr = await db.StringGetAsync(RedisKeyScheduledUtc);
        var message = await db.StringGetAsync(RedisKeyMessage);
        var scheduledBy = await db.StringGetAsync(RedisKeyScheduledBy);
        var seasonNumVal = await db.StringGetAsync(RedisKeySeasonNumber);
        var seasonNameVal = await db.StringGetAsync(RedisKeySeasonName);
        var seasonEpochVal = await db.StringGetAsync(RedisKeySeasonEpoch);

        int seasonNumber = seasonNumVal.HasValue && int.TryParse(seasonNumVal, out var s) ? s : 1;
        string seasonName = seasonNameVal.HasValue ? seasonNameVal.ToString() : "Season 1: Genesis";
        DateTimeOffset seasonEpoch = seasonEpochVal.HasValue && DateTimeOffset.TryParse(seasonEpochVal, out var e)
            ? e
            : new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        bool isScheduled = false;
        DateTimeOffset? scheduledUtc = null;
        int remainingSeconds = 0;

        if (scheduledUtcStr.HasValue && DateTimeOffset.TryParse(scheduledUtcStr, out var parsedUtc))
        {
            var now = DateTimeOffset.UtcNow;
            if (parsedUtc > now)
            {
                isScheduled = true;
                scheduledUtc = parsedUtc;
                remainingSeconds = (int)(parsedUtc - now).TotalSeconds;
            }
        }

        return new CanvasResetStatusDto
        {
            IsResetScheduled = isScheduled,
            ScheduledResetUtc = scheduledUtc,
            RemainingSeconds = remainingSeconds,
            Message = message.HasValue ? message.ToString() : null,
            ScheduledBy = scheduledBy.HasValue ? scheduledBy.ToString() : null,
            CurrentSeasonNumber = seasonNumber,
            CurrentSeasonName = seasonName,
            CurrentSeasonStartedAt = seasonEpoch
        };
    }

    /// <summary>
    /// Schedules a future global canvas reset countdown (Admin only).
    /// </summary>
    public async Task<CanvasScheduledReset> ScheduleResetAsync(DateTimeOffset scheduledResetUtc, string scheduledBy, string? message)
    {
        if (scheduledResetUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Scheduled reset timestamp must be in the future.");
        }

        // Persist to SQL
        var record = await _repository.SaveScheduledResetAsync(scheduledResetUtc, scheduledBy, message);

        // Update Redis
        var db = _redis.GetDatabase();
        await db.StringSetAsync(RedisKeyScheduledUtc, scheduledResetUtc.ToString("o"));
        await db.StringSetAsync(RedisKeyMessage, message ?? string.Empty);
        await db.StringSetAsync(RedisKeyScheduledBy, scheduledBy);

        int remainingSeconds = (int)(scheduledResetUtc - DateTimeOffset.UtcNow).TotalSeconds;

        _logger.LogInformation("Global wall reset scheduled for {Target:u} by {Admin}. Remaining: {Secs}s",
            scheduledResetUtc, scheduledBy, remainingSeconds);

        // SignalR Broadcast
        await _hubContext.Clients.All.SendAsync("CanvasResetScheduled", new
        {
            scheduledResetUtc,
            remainingSeconds,
            message,
            scheduledBy
        });

        return record;
    }

    /// <summary>
    /// Cancels a pending scheduled global canvas reset (Admin only).
    /// </summary>
    public async Task<bool> CancelResetAsync(string cancelledBy)
    {
        await _repository.CancelScheduledResetAsync(cancelledBy);

        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(new RedisKey[] { RedisKeyScheduledUtc, RedisKeyMessage, RedisKeyScheduledBy });

        _logger.LogInformation("Scheduled global wall reset cancelled by {Admin}", cancelledBy);

        // SignalR Broadcast
        await _hubContext.Clients.All.SendAsync("CanvasResetCancelled", new
        {
            cancelledBy,
            cancelledAt = DateTimeOffset.UtcNow
        });

        return true;
    }

    /// <summary>
    /// Atomically wipes the global canvas buffer back to blank white (0x00),
    /// resets the minimap, clears global reservations, advances the season,
    /// and broadcasts CanvasResetExecuted to all connected clients.
    /// </summary>
    public async Task<CanvasSeason> ExecuteResetAsync(string executedBy, string reason)
    {
        await _resetLock.WaitAsync();
        try
        {
            _logger.LogWarning("Executing Global Wall Reset! Initiated by {Admin}. Reason: {Reason}", executedBy, reason);

            var db = _redis.GetDatabase();

            // 1. Wipe 100MB Redis Canvas Buffer with 0x00 (pure blank white)
            byte[] blankCanvas = new byte[TotalBytes];
            await db.StringSetAsync(CanvasRedisKey, blankCanvas);

            // 2. Wipe 25.6KB Minimap Overview Buffer with 0x00
            byte[] blankMinimap = new byte[MinimapBytes];
            await db.StringSetAsync(CanvasMinimapRedisKey, blankMinimap);

            // 3. Auto-release active territory reservations on global wall
            await _reservationService.ReleaseAllGlobalWallReservationsAsync();

            // 4. Advance season in SQL Server
            var newSeason = await _repository.CreateNextSeasonAsync(executedBy, reason);

            // 5. Update Redis Season Keys
            await db.StringSetAsync(RedisKeySeasonNumber, newSeason.SeasonNumber);
            await db.StringSetAsync(RedisKeySeasonName, newSeason.Name);
            await db.StringSetAsync(RedisKeySeasonEpoch, newSeason.StartedAt.ToString("o"));

            // 6. Clear scheduled reset keys in Redis & mark executed in SQL
            var scheduled = await _repository.GetActiveScheduledResetAsync();
            if (scheduled != null)
            {
                await _repository.MarkScheduledResetExecutedAsync(scheduled.Id);
            }
            await db.KeyDeleteAsync(new RedisKey[] { RedisKeyScheduledUtc, RedisKeyMessage, RedisKeyScheduledBy });

            _logger.LogInformation("Global Wall Reset successfully executed. Started Season #{Season}: '{Name}'",
                newSeason.SeasonNumber, newSeason.Name);

            // 7. SignalR broadcast to all connected clients
            await _hubContext.Clients.All.SendAsync("CanvasResetExecuted", new
            {
                seasonNumber = newSeason.SeasonNumber,
                seasonName = newSeason.Name,
                executedAt = newSeason.StartedAt,
                executedBy,
                reason
            });

            return newSeason;
        }
        finally
        {
            _resetLock.Release();
        }
    }
}
