using System.Diagnostics.Metrics;

namespace GlobalGraffitiWall.API.Telemetry;

/// <summary>
/// Provides OpenTelemetry-compatible metrics, counters, and gauges for canvas operations,
/// SignalR connections, placement rates, and database batch flushes.
/// </summary>
public class CanvasMetrics
{
    public const string MeterName = "GlobalGraffitiWall.Canvas";

    public Meter Meter { get; }

    private readonly Counter<long> _pixelPlacementsCounter;
    private readonly Counter<long> _batchesFlushedCounter;
    private readonly UpDownCounter<long> _activeSignalRConnectionsCounter;
    private readonly Histogram<double> _batchFlushDurationHistogram;

    private long _totalPixelsPlaced;
    private long _totalBatchesFlushed;
    private long _currentActiveConnections;

    public DateTimeOffset ServerStartTime { get; } = DateTimeOffset.UtcNow;

    public CanvasMetrics()
    {
        Meter = new Meter(MeterName, "1.0.0");

        _pixelPlacementsCounter = Meter.CreateCounter<long>(
            "canvas.pixels.placed",
            description: "Total number of pixels placed on canvas walls");

        _batchesFlushedCounter = Meter.CreateCounter<long>(
            "canvas.batches.flushed",
            description: "Total number of batched SQL Server bulk inserts completed");

        _activeSignalRConnectionsCounter = Meter.CreateUpDownCounter<long>(
            "canvas.signalr.active_connections",
            description: "Number of active real-time SignalR WebSocket painter connections");

        _batchFlushDurationHistogram = Meter.CreateHistogram<double>(
            "canvas.batch_writer.duration_ms",
            unit: "ms",
            description: "Duration of SqlBulkCopy database flush operations in milliseconds");
    }

    public void RecordPixelPlaced(string wallId = "Global")
    {
        Interlocked.Increment(ref _totalPixelsPlaced);
        _pixelPlacementsCounter.Add(1, new KeyValuePair<string, object?>("wall", wallId));
    }

    public void RecordBatchFlushed(int pixelCount, double durationMs)
    {
        Interlocked.Increment(ref _totalBatchesFlushed);
        _batchesFlushedCounter.Add(1);
        _batchFlushDurationHistogram.Record(durationMs);
    }

    public void IncrementConnection()
    {
        Interlocked.Increment(ref _currentActiveConnections);
        _activeSignalRConnectionsCounter.Add(1);
    }

    public void DecrementConnection()
    {
        Interlocked.Decrement(ref _currentActiveConnections);
        _activeSignalRConnectionsCounter.Add(-1);
    }

    public TelemetrySummary GetSummary() => new()
    {
        TotalPixelsPlaced = Interlocked.Read(ref _totalPixelsPlaced),
        TotalBatchesFlushed = Interlocked.Read(ref _totalBatchesFlushed),
        ActiveConnections = Math.Max(0, Interlocked.Read(ref _currentActiveConnections)),
        Uptime = DateTimeOffset.UtcNow - ServerStartTime,
        ServerTime = DateTimeOffset.UtcNow
    };
}

public class TelemetrySummary
{
    public long TotalPixelsPlaced { get; set; }
    public long TotalBatchesFlushed { get; set; }
    public long ActiveConnections { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}
