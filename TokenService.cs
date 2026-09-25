using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GlobalGraffitiWall.API;

public class TokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Generates a signed JWT token containing the user's Id, Username, DisplayName, and Role.
    /// </summary>
    public string GenerateJwtToken(User user)
    {
        var secret = _configuration["JwtSettings:Secret"] ?? "GlobalGraffitiWall_SuperSecretKey_ForSigningTokens_2026!#*";
        var issuer = _configuration["JwtSettings:Issuer"] ?? "GlobalGraffitiWall";
        var audience = _configuration["JwtSettings:Audience"] ?? "GlobalGraffitiWallClient";
        var expirationDays = _configuration.GetValue<int>("JwtSettings:ExpirationDays", 30);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.GivenName, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(expirationDays),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
