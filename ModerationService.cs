using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

public class ModerationService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CanvasRepository _repository;
    private readonly ILogger<ModerationService> _logger;

    private const string RedisBannedIpsKey = "canvas:shadow_banned:ips";
    private const string RedisBannedUsersKey = "canvas:shadow_banned:users";
    private const int IpBurstThreshold = 25; // max placements per 10 seconds per IP

    public ModerationService(
        IConnectionMultiplexer redis,
        CanvasRepository repository,
        ILogger<ModerationService> logger)
    {
        _redis = redis;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Rehydrates Redis shadow-ban sets from SQL Server on startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Rehydrating shadow bans from SQL Server into Redis...");

        var db = _redis.GetDatabase();
        var activeBans = await _repository.GetActiveShadowBansAsync();

        int ipCount = 0;
        int userCount = 0;

        foreach (var ban in activeBans)
        {
            if (string.Equals(ban.BanType, "user", StringComparison.OrdinalIgnoreCase))
            {
                await db.SetAddAsync(RedisBannedUsersKey, ban.Identifier);
                userCount++;
            }
            else
            {
                await db.SetAddAsync(RedisBannedIpsKey, ban.Identifier);
                ipCount++;
            }
        }

        _logger.LogInformation("Loaded {UserCount} shadow-banned users and {IpCount} shadow-banned IPs into Redis.",
            userCount, ipCount);
    }

    /// <summary>
    /// Evaluates if a given user identifier or IP address is currently shadow-banned.
    /// In-memory Redis Set check: O(1) complexity.
    /// </summary>
    public async Task<bool> IsShadowBannedAsync(string? userId, string? ipAddress)
    {
        var db = _redis.GetDatabase();

        if (!string.IsNullOrEmpty(ipAddress))
        {
            if (await db.SetContainsAsync(RedisBannedIpsKey, ipAddress))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(userId))
        {
            if (await db.SetContainsAsync(RedisBannedUsersKey, userId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// IP abuse and automated griefing detection tripwire.
    /// Tracks placement frequency per IP and auto-quarantines bot scripts.
    /// </summary>
    public async Task<bool> CheckIpAbuseAndAutoQuarantineAsync(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || ipAddress == "127.0.0.1" || ipAddress == "::1")
            return false;

        var db = _redis.GetDatabase();
        string burstKey = $"canvas:ip_burst:{ipAddress}";

        long count = await db.StringIncrementAsync(burstKey);
        if (count == 1)
        {
            await db.KeyExpireAsync(burstKey, TimeSpan.FromSeconds(10));
        }

        if (count > IpBurstThreshold)
        {
            _logger.LogWarning("IP {IP} exceeded griefing threshold ({Count} placements in 10s). Triggering auto shadow-ban!",
                ipAddress, count);

            await ShadowBanAsync(
                identifier: ipAddress,
                banType: "ip",
                reason: $"Automated anti-abuse tripwire: exceeded {IpBurstThreshold} placements within 10s",
                bannedBy: "SystemTripwire");

            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks an entity (IP or User ID) as shadow-banned in both SQL Server and Redis.
    /// </summary>
    public async Task ShadowBanAsync(string identifier, string banType, string? reason, string? bannedBy)
    {
        var db = _redis.GetDatabase();
        string normalizedType = banType.ToLowerInvariant() == "user" ? "user" : "ip";

        await _repository.AddOrUpdateShadowBanAsync(identifier, normalizedType, reason, bannedBy);

        if (normalizedType == "user")
        {
            await db.SetAddAsync(RedisBannedUsersKey, identifier);
        }
        else
        {
            await db.SetAddAsync(RedisBannedIpsKey, identifier);
        }

        _logger.LogWarning("Entity {Identifier} ({Type}) shadow-banned by {Admin}. Reason: {Reason}",
            identifier, normalizedType, bannedBy ?? "Admin", reason ?? "No reason provided");
    }

    /// <summary>
    /// Deactivates a shadow ban in SQL Server and removes it from Redis.
    /// </summary>
    public async Task UnbanAsync(string identifier)
    {
        var db = _redis.GetDatabase();

        await _repository.DeactivateShadowBanAsync(identifier);
        await db.SetRemoveAsync(RedisBannedIpsKey, identifier);
        await db.SetRemoveAsync(RedisBannedUsersKey, identifier);

        _logger.LogInformation("Entity {Identifier} shadow-ban removed.", identifier);
    }

    /// <summary>
    /// Retrieves all active shadow bans.
    /// </summary>
    public async Task<IEnumerable<ShadowBanRecord>> GetActiveBansAsync()
    {
        return await _repository.GetActiveShadowBansAsync();
    }

    /// <summary>
    /// Retrieves recent shadow-banned pixel placements for audit review.
    /// </summary>
    public async Task<IEnumerable<ShadowPlacementAuditDto>> GetShadowBannedPlacementsAsync(int limit = 50)
    {
        return await _repository.GetShadowBannedPlacementsAsync(limit);
    }
}
