namespace GlobalGraffitiWall.API;

public class CanvasResetWorkerService : BackgroundService
{
    private readonly CanvasResetService _resetService;
    private readonly ILogger<CanvasResetWorkerService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(1);

    public CanvasResetWorkerService(
        CanvasResetService resetService,
        ILogger<CanvasResetWorkerService> logger)
    {
        _resetService = resetService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CanvasResetWorkerService background worker started.");

        using var timer = new PeriodicTimer(_checkInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var status = await _resetService.GetResetStatusAsync();
                if (status.IsResetScheduled && status.RemainingSeconds <= 0)
                {
                    _logger.LogWarning("Scheduled Canvas Reset target reached! Executing automated wipe...");
                    await _resetService.ExecuteResetAsync(
                        status.ScheduledBy ?? "System Scheduler",
                        status.Message ?? "Scheduled season reset countdown reached zero.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in CanvasResetWorkerService loop.");
            }
        }

        _logger.LogInformation("CanvasResetWorkerService background worker stopped.");
    }
}
