using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HoaVoting.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HoaVoting.Api.Services.Auth;

public class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    public (string token, DateTimeOffset expires) Issue(AppUser user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_o.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
