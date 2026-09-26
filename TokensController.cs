using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace GlobalGraffitiWall.API;

[ApiController]
[Route("api/tokens")]
public class TokensController : ControllerBase
{
    private readonly CanvasRepository _repository;
    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokensController> _logger;

    private readonly double _maxCapacity;
    private readonly double _refillRate;

    public TokensController(
        CanvasRepository repository,
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        ILogger<TokensController> logger)
    {
        _repository = repository;
        _redis = redis;
        _configuration = configuration;
        _logger = logger;
        _maxCapacity = configuration.GetValue<double>("CanvasSettings:MaxCapacity", 16);
        _refillRate = configuration.GetValue<double>("CanvasSettings:RefillRatePerSecond", 0.2);
    }

    /// <summary>
    /// Returns the token balances (regenerating charges + bonus tokens) and daily claim status for the caller.
    /// Supports authenticated JWT or anonymous guest userId query fallback.
    /// </summary>
    [HttpGet("balance")]
    public async Task<ActionResult<TokenBalanceDto>> GetBalance([FromQuery] string? userId)
    {
        string? authIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isAuthenticated = !string.IsNullOrEmpty(authIdStr) && Guid.TryParse(authIdStr, out _);
        string effectiveId = isAuthenticated ? authIdStr! : (userId ?? "anonymous");

        var db = _redis.GetDatabase();
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Calculate regenerating charges from Redis token bucket
        double regeneratingCharges = _maxCapacity;
        var tokenVal = await db.StringGetAsync($"user:{effectiveId}:tokens");
        var lastUpdateVal = await db.StringGetAsync($"user:{effectiveId}:last_update");

        if (tokenVal.HasValue && lastUpdateVal.HasValue &&
            double.TryParse((string?)tokenVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var currentTokens) &&
            long.TryParse((string?)lastUpdateVal, out var lastUpdate))
        {
            long elapsed = Math.Max(0, nowUnix - lastUpdate);
            regeneratingCharges = Math.Min(_maxCapacity, currentTokens + (elapsed * _refillRate));
        }

        // 2. Fetch bonus tokens
        int bonusTokens = 0;
        bool canClaimDaily = false;
        int secondsUntilNextClaim = 0;

        if (isAuthenticated && Guid.TryParse(authIdStr, out var userGuid))
        {
            var user = await _repository.GetUserByIdAsync(userGuid);
            if (user != null)
            {
                bonusTokens = user.BonusTokens;
                // Sync Redis if needed
                await db.StringSetAsync($"user:{userGuid}:bonus_balance", bonusTokens);

                if (!user.LastDailyClaim.HasValue)
                {
                    canClaimDaily = true;
                    secondsUntilNextClaim = 0;
                }
                else
                {
                    var nextClaim = user.LastDailyClaim.Value.AddHours(24);
                    var now = DateTimeOffset.UtcNow;
                    if (now >= nextClaim)
                    {
                        canClaimDaily = true;
                        secondsUntilNextClaim = 0;
                    }
                    else
                    {
                        canClaimDaily = false;
                        secondsUntilNextClaim = (int)Math.Max(0, (nextClaim - now).TotalSeconds);
                    }
                }
            }
        }
        else
        {
            // Anonymous guest: check if any bonus tokens stored in Redis
            var guestBonus = await db.StringGetAsync($"user:{effectiveId}:bonus_balance");
            if (guestBonus.HasValue && int.TryParse((string?)guestBonus, out var b))
            {
                bonusTokens = b;
            }
        }

        return Ok(new TokenBalanceDto
        {
            IsAuthenticated = isAuthenticated,
            RegeneratingCharges = Math.Round(regeneratingCharges, 2),
            MaxCapacity = _maxCapacity,
            RefillRate = _refillRate,
            BonusTokens = bonusTokens,
            CanClaimDaily = canClaimDaily,
            SecondsUntilNextDailyClaim = secondsUntilNextClaim
        });
    }

    /// <summary>
    /// Claims the daily supply drop reward (+30 Bonus Tokens) once every 24 hours.
    /// Requires user authentication.
    /// </summary>
    [HttpPost("claim-daily")]
    [Authorize]
    public async Task<ActionResult<ClaimDailyResponseDto>> ClaimDailyReward()
    {
        var authIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(authIdStr) || !Guid.TryParse(authIdStr, out var userId))
        {
            return Unauthorized("Authenticated painter identity required.");
        }

        const int DailyRewardAmount = 30;
        var (success, message, claimedAmount, newBalance, nextClaimAt) = await _repository.ClaimDailyRewardAsync(userId, DailyRewardAmount);

        if (!success)
        {
            var remaining = nextClaimAt - DateTimeOffset.UtcNow;
            return BadRequest(new ClaimDailyResponseDto
            {
                Success = false,
                Message = message,
                ClaimedTokens = 0,
                NewBonusBalance = newBalance,
                NextClaimAt = nextClaimAt,
                SecondsUntilNextClaim = (int)Math.Max(0, remaining.TotalSeconds)
            });
        }

        // Sync Redis cache with new balance
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"user:{userId}:bonus_balance", newBalance);

        _logger.LogInformation("User {UserId} claimed daily supply drop: +{Amount} tokens. New balance: {Balance}",
            userId, claimedAmount, newBalance);

        return Ok(new ClaimDailyResponseDto
        {
            Success = true,
            Message = message,
            ClaimedTokens = claimedAmount,
            NewBonusBalance = newBalance,
            NextClaimAt = nextClaimAt,
            SecondsUntilNextClaim = (int)(nextClaimAt - DateTimeOffset.UtcNow).TotalSeconds
        });
    }

    /// <summary>
    /// Redeems a community or event promo code for bonus tokens.
    /// Requires user authentication.
    /// </summary>
    [HttpPost("redeem")]
    [Authorize]
    public async Task<ActionResult<RedeemCodeResponseDto>> RedeemCode([FromBody] RedeemCodeRequestDto req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new RedeemCodeResponseDto { Success = false, Message = "Promo code is required." });

        var authIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(authIdStr) || !Guid.TryParse(authIdStr, out var userId))
        {
            return Unauthorized(new RedeemCodeResponseDto { Success = false, Message = "Authenticated painter identity required." });
        }

        var (success, message, grantedTokens, newBalance) = await _repository.RedeemPromoCodeAsync(userId, req.Code);
        if (!success)
        {
            return BadRequest(new RedeemCodeResponseDto { Success = false, Message = message });
        }

        // Sync Redis cache with new balance
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"user:{userId}:bonus_balance", newBalance);

        _logger.LogInformation("User {UserId} redeemed code '{Code}' for +{Amount} bonus tokens. New balance: {Balance}",
            userId, req.Code.Trim().ToUpperInvariant(), grantedTokens, newBalance);

        return Ok(new RedeemCodeResponseDto
        {
            Success = true,
            Message = message,
            GrantedTokens = grantedTokens,
            NewBonusBalance = newBalance
        });
    }

    /// <summary>
    /// Returns the caller's recent token ledger transactions.
    /// Requires user authentication.
    /// </summary>
    [HttpGet("history")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<TokenTransactionDto>>> GetHistory([FromQuery] int limit = 20)
    {
        var authIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(authIdStr) || !Guid.TryParse(authIdStr, out var userId))
        {
            return Unauthorized("Authenticated painter identity required.");
        }

        var clampedLimit = Math.Clamp(limit, 1, 100);
        var transactions = await _repository.GetTokenTransactionsAsync(userId, clampedLimit);
        return Ok(transactions);
    }

    /// <summary>
    /// Calculates the token cost for a territory reservation of specified dimensions and duration.
    /// </summary>
    [HttpGet("cost-preview")]
    public ActionResult<TokenCostPreviewDto> GetCostPreview(
        [FromQuery] int width,
        [FromQuery] int height,
        [FromQuery] int durationMinutes,
        [FromQuery] string? userId = null)
    {
        int clampedWidth = Math.Clamp(width, 5, 128);
        int clampedHeight = Math.Clamp(height, 5, 128);
        int clampedDuration = Math.Clamp(durationMinutes, 15, 1440);

        int cost = ReservationService.CalculateCost(clampedWidth, clampedHeight, clampedDuration);

        return Ok(new TokenCostPreviewDto
        {
            Width = clampedWidth,
            Height = clampedHeight,
            Area = clampedWidth * clampedHeight,
            DurationMinutes = clampedDuration,
            TokenCost = cost
        });
    }
}
