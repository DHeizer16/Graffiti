using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GlobalGraffitiWall.API.HealthChecks;

/// <summary>
/// Verifies SQL Server database connectivity and query execution latency.
/// </summary>
public class SqlServerHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public SqlServerHealthCheck(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SqlServerConnection") ?? string.Empty;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(cancellationToken);
            sw.Stop();

            var data = new Dictionary<string, object>
            {
                { "database", connection.Database },
                { "serverVersion", connection.ServerVersion },
                { "latencyMs", Math.Round(sw.Elapsed.TotalMilliseconds, 2) }
            };

            return HealthCheckResult.Healthy($"SQL Server is healthy (query latency: {sw.Elapsed.TotalMilliseconds:F1}ms)", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server health check failed: " + ex.Message, ex);
        }
    }
}
