using Microsoft.AspNetCore.Mvc;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/[controller]")]
public class ReservationsController : ControllerBase
{
    private readonly ReservationService _reservationService;
    private readonly ILogger<ReservationsController> _logger;

    public ReservationsController(
        ReservationService reservationService,
        ILogger<ReservationsController> logger)
    {
        _reservationService = reservationService;
        _logger = logger;
    }

    /// <summary>
    /// Returns all currently active canvas territory reservations.
    /// </summary>
    [HttpGet("active")]
    public ActionResult<IEnumerable<ReservationDto>> GetActiveReservations()
    {
        return Ok(_reservationService.GetActiveReservations());
    }

    /// <summary>
    /// Creates a new territory reservation.
    /// </summary>
    [HttpPost("reserve")]
    public async Task<ActionResult<ReservationCreatedResponse>> ReserveArea([FromBody] CreateReservationRequest request)
    {
        if (request == null)
            return BadRequest(new ReservationCreatedResponse { Success = false, Message = "Invalid request payload." });

        var response = await _reservationService.CreateReservationAsync(request);
        if (!response.Success)
        {
            return BadRequest(response);
        }

        return Ok(response);
    }

    /// <summary>
    /// Releases an active reservation early using owner identifier or collaborator secret key.
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelReservation([FromBody] CancelReservationRequest request)
    {
        if (request == null || request.ReservationId == Guid.Empty)
            return BadRequest(new { success = false, message = "Reservation ID is required." });

        var (success, message) = await _reservationService.CancelReservationAsync(
            request.ReservationId, request.OwnerId, request.SecretKey);

        if (!success)
        {
            return BadRequest(new { success, message });
        }

        return Ok(new { success, message });
    }

    /// <summary>
    /// Validates and links a collaborator secret key to an active reservation.
    /// </summary>
    [HttpPost("link-key")]
    public async Task<IActionResult> LinkKey([FromBody] LinkKeyRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Key))
            return BadRequest(new { success = false, message = "Collaborator key is required." });

        var (success, message, reservation) = await _reservationService.LinkKeyAsync(request.Key);
        if (!success)
        {
            return BadRequest(new { success, message });
        }

        return Ok(new { success, message, reservation });
    }

    /// <summary>
    /// Checks if a coordinate is reserved and whether caller has authorization.
    /// </summary>
    [HttpGet("check")]
    public ActionResult CheckCoordinate(
        [FromQuery] int x,
        [FromQuery] int y,
        [FromQuery] string? userId = null,
        [FromQuery] string? secretKey = null)
    {
        var result = _reservationService.CheckPlacementPermission(x, y, userId ?? string.Empty, secretKey);
        return Ok(new
        {
            isReserved = result.IsReserved,
            isAuthorized = result.IsAuthorized,
            reason = result.Reason,
            reservation = result.Reservation?.ToDto(DateTimeOffset.UtcNow)
        });
    }
}
