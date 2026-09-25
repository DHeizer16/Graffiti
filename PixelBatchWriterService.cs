using System.Diagnostics;

namespace GlobalGraffitiWall.API;

public class PixelBatchWriterService : BackgroundService
{
    private readonly PixelPlacementQueue _queue;
    private readonly CanvasRepository _repository;
    private readonly ILogger<PixelBatchWriterService> _logger;

    private const int MaxBatchSize = 1000;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromMilliseconds(250);

    public PixelBatchWriterService(
        PixelPlacementQueue queue,
        CanvasRepository repository,
        ILogger<PixelBatchWriterService> logger)
    {
        _queue = queue;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PixelBatchWriterService started.");

        var batch = new List<PixelPlacementItem>(MaxBatchSize);
        var reader = _queue.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Wait for at least one item to become available
                if (await reader.WaitToReadAsync(stoppingToken))
                {
                    var stopwatch = Stopwatch.StartNew();

                    // 2. Accumulate up to MaxBatchSize or until MaxWaitTime expires
                    while (batch.Count < MaxBatchSize && stopwatch.Elapsed < MaxWaitTime)
                    {
                        if (reader.TryRead(out var item))
                        {
                            batch.Add(item);
                        }
                        else
                        {
                            // Brief pause to allow concurrent placements to coalesce into the batch
                            await Task.Delay(10, stoppingToken);
                        }
                    }

                    // 3. Flush accumulated batch to SQL Server
                    if (batch.Count > 0)
                    {
                        await FlushBatchAsync(batch);
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in PixelBatchWriterService loop.");
                await Task.Delay(1000, stoppingToken);
            }
        }

        // Graceful shutdown: Drain remaining items in queue before stopping
        _logger.LogInformation("Flushing remaining queued pixel placements before shutdown...");
        while (reader.TryRead(out var remainingItem))
        {
            batch.Add(remainingItem);
            if (batch.Count >= MaxBatchSize)
            {
                await FlushBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch);
            batch.Clear();
        }

        _logger.LogInformation("PixelBatchWriterService shutdown complete.");
    }

    private async Task FlushBatchAsync(List<PixelPlacementItem> batch)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await _repository.BulkInsertPixelPlacementsAsync(batch);
            sw.Stop();
            _logger.LogDebug("Bulk inserted {Count} pixel placements in {ElapsedMs}ms.", batch.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk insert {Count} pixel placements into SQL Server.", batch.Count);
        }
    }
}
