using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace GlobalGraffitiWall.API.HealthChecks;

/// <summary>
/// Verifies Redis connectivity, responsiveness, and ping round-trip latency.
/// </summary>
public class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_redis.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis is not connected.");
            }

            var db = _redis.GetDatabase();
            var latency = await db.PingAsync();

            var data = new Dictionary<string, object>
            {
                { "endpoints", string.Join(", ", _redis.GetEndPoints().Select(e => e.ToString())) },
                { "latencyMs", Math.Round(latency.TotalMilliseconds, 2) }
            };

            return HealthCheckResult.Healthy($"Redis is healthy (ping: {latency.TotalMilliseconds:F1}ms)", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis ping failed: " + ex.Message, ex);
        }
    }
}
