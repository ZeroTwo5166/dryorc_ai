using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace backend.Services;

public record IntegrationStateClaims(Guid BusinessId, Guid UserId, string Platform);

public interface IIntegrationStateTokenService
{
    string Issue(Guid businessId, Guid userId, string platform);

    /// <summary>Returns null on any signature, expiry, malformed-token, or
    /// platform-mismatch failure — never throws. An OAuth callback is an
    /// untrusted redirect target and must fail closed via a null return, not
    /// via an unhandled exception.</summary>
    IntegrationStateClaims? Validate(string token, string expectedPlatform);
}

/// <summary>
/// Issues and validates short-lived, signed OAuth "state" tokens binding a
/// connect attempt to the exact (BusinessId, UserId, Platform) tuple that
/// initiated it — same stateless-JWT-as-the-session pattern WidgetTokenService
/// already established for widget sessions (no database table backing this).
///
/// SECURITY MODEL — read this in full before touching this class or citing
/// it elsewhere. Do not summarize any of this as "OAuth is CSRF protected."
///
/// 1. What this signed token guarantees: it was issued by this backend for
///    this exact (BusinessId, UserId, Platform) tuple at a specific time, has
///    not been tampered with (HMAC signature, verified via
///    ValidateIssuerSigningKey), and has not exceeded its short Lifetime
///    (ValidateLifetime). It does NOT guarantee it has not already been
///    redeemed once before — there is no server-side record of which state
///    tokens have already been consumed (see point 4). This is a deliberate
///    Step 18 design decision, not an oversight.
/// 2. What the live authenticated staff cookie guarantees (checked by
///    IntegrationsController.Callback, not by this class): the
///    BusinessId/UserId this token claims must match the BusinessId/UserId
///    of whoever is actually presenting the callback request right now. This
///    is what defeats a stolen state token being redeemed by a different
///    tenant or user than the one who initiated the connect — the state
///    token's claims are cross-checked against, never substituted for, the
///    live session's own tenant identity.
/// 3. What the provider's authorization code guarantees (entirely outside
///    this class, enforced by the OAuth provider itself): authorization
///    codes are single-use — a second exchange attempt for the same code
///    fails at the provider's token endpoint regardless of anything Nivora
///    tracks server-side.
/// 4. Residual risk, accepted deliberately: because state validity is not
///    server-side single-use tracked, a captured, not-yet-expired state
///    token could in principle be replayed by an attacker already
///    authenticated to the SAME business/user before the legitimate callback
///    completes. Point 3 bounds the actual damage of that scenario to a
///    failed/duplicate connect attempt (the provider rejects whichever code
///    exchange happens second) rather than a successful hijack. No
///    nonce-tracking table or cache was added to close this narrow window —
///    a deliberate choice to avoid new persistent infrastructure for a
///    residual risk this bounded, not an oversight. Re-evaluate only if a
///    future provider's threat model makes this risk materially worse.
/// </summary>
public class IntegrationStateTokenService(IOptions<IntegrationStateTokenOptions> options) : IIntegrationStateTokenService
{
    private const string BusinessIdClaim = "nivora:integration_state:business_id";
    private const string UserIdClaim = "nivora:integration_state:user_id";
    private const string PlatformClaim = "nivora:integration_state:platform";

    public const string Issuer = "nivora-integration-state";
    public const string Audience = "nivora-integration-state";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public string Issue(Guid businessId, Guid userId, string platform)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(BusinessIdClaim, businessId.ToString()),
            new Claim(UserIdClaim, userId.ToString()),
            new Claim(PlatformClaim, platform),
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

    public IntegrationStateClaims? Validate(string token, string expectedPlatform)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        ClaimsPrincipal principal;
        try
        {
            principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var businessIdRaw = principal.FindFirst(BusinessIdClaim)?.Value;
        var userIdRaw = principal.FindFirst(UserIdClaim)?.Value;
        var platform = principal.FindFirst(PlatformClaim)?.Value;

        if (!Guid.TryParse(businessIdRaw, out var businessId)) return null;
        if (!Guid.TryParse(userIdRaw, out var userId)) return null;
        if (string.IsNullOrEmpty(platform)) return null;
        if (!string.Equals(platform, expectedPlatform, StringComparison.Ordinal)) return null;

        return new IntegrationStateClaims(businessId, userId, platform);
    }
}
