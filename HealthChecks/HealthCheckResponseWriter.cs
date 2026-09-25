using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GlobalGraffitiWall.API.HealthChecks;

/// <summary>
/// Formats HealthReport results into structured, developer-friendly JSON payloads.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            timestamp = DateTimeOffset.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 2),
                data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                error = e.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
