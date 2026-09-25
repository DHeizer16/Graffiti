namespace GlobalGraffitiWall.API;

public class ReservationExpirationService : BackgroundService
{
    private readonly ReservationService _reservationService;
    private readonly ILogger<ReservationExpirationService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(15);

    public ReservationExpirationService(
        ReservationService reservationService,
        ILogger<ReservationExpirationService> logger)
    {
        _reservationService = reservationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReservationExpirationService background worker started.");

        using var timer = new PeriodicTimer(_checkInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _reservationService.PruneExpiredReservationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while pruning expired canvas reservations.");
            }
        }

        _logger.LogInformation("ReservationExpirationService background worker stopped.");
    }
}
