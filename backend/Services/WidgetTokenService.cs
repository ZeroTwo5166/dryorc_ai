using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using backend.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services;

public interface IWidgetTokenService
{
    /// <summary>Issues a fresh widget session token bound to this exact
    /// (BusinessId, WidgetId, VisitorSessionId) tuple.</summary>
    string IssueToken(Guid businessId, Guid widgetId, string visitorSessionId);
}

/// <summary>
/// Issues and (via Program.cs's AddJwtBearer registration, which shares
/// Issuer/Audience/the signing key with this class) validates widget
/// session tokens. No database session table — the JWT itself IS the
/// session, matching the spec's explicit "do not create a database session
/// table" instruction.
///
/// Claims embedded: NivoraClaimTypes.BusinessId (shared claim type with the
/// staff cookie principal — this is what lets ClaimsCurrentTenantAccessor
/// resolve the tenant for a widget request with no scheme-specific code),
/// NivoraClaimTypes.WidgetId, NivoraClaimTypes.VisitorSessionId.
/// </summary>
public class WidgetTokenService(IOptions<WidgetTokenOptions> options) : IWidgetTokenService
{
    public const string Issuer = "nivora-widget";
    public const string Audience = "nivora-widget";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public string IssueToken(Guid businessId, Guid widgetId, string visitorSessionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(NivoraClaimTypes.BusinessId, businessId.ToString()),
            new Claim(NivoraClaimTypes.WidgetId, widgetId.ToString()),
            new Claim(NivoraClaimTypes.VisitorSessionId, visitorSessionId),
            new Claim(JwtRegisteredClaimNames.Sub, visitorSessionId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(Lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
