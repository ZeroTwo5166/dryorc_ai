using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using backend.Services;
using Microsoft.IdentityModel.Tokens;

namespace backend.Tests;

/// <summary>
/// Test-only RSA keypair + low-level JWT builder for exercising
/// SignedIdentityTokenService against controllable, deliberately valid or
/// invalid RS256 tokens — mirrors FakeShopifyHttpMessageHandler's role
/// ("produce controllable external input"), not any Shopify-specific
/// mechanics. Builds JwtHeader/JwtPayload directly rather than using the
/// handler's high-level convenience methods, so every claim/header value
/// (including deliberately wrong ones, for negative-path tests) is under
/// the test's exact control.
/// </summary>
public sealed class FakeSignedTokenSigner : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    public string IssueToken(
        Guid kid,
        long externalCustomerId,
        string? issuer = null,
        string? audience = null,
        DateTime? issuedAt = null,
        DateTime? expires = null,
        string? jti = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        bool omitExternalCustomerIdClaim = false,
        bool omitJtiClaim = false)
    {
        var now = DateTime.UtcNow;
        var effectiveIssuedAt = issuedAt ?? now;
        var effectiveExpires = expires ?? effectiveIssuedAt.AddMinutes(3);

        var claims = new List<Claim>();
        if (!omitJtiClaim)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti ?? Guid.NewGuid().ToString()));
        }
        if (!omitExternalCustomerIdClaim)
        {
            claims.Add(new Claim("external_customer_id", externalCustomerId.ToString()));
        }

        var credentials = new SigningCredentials(new RsaSecurityKey(_rsa), algorithm);
        var header = new JwtHeader(credentials);
        header["kid"] = kid.ToString();

        var payload = new JwtPayload(
            issuer: issuer ?? SignedIdentityTokenService.Issuer,
            audience: audience ?? SignedIdentityTokenService.Audience,
            claims: claims,
            notBefore: effectiveIssuedAt,
            expires: effectiveExpires,
            issuedAt: effectiveIssuedAt);

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Signs with a DIFFERENT, throwaway keypair never registered
    /// as any BusinessSigningKey's public key — for "invalid signature"
    /// tests where kid still points at a real, registered row but the
    /// bytes weren't actually signed by that key's private counterpart.</summary>
    public string IssueTokenSignedByWrongKey(Guid kid, long externalCustomerId)
    {
        using var otherSigner = new FakeSignedTokenSigner();
        return otherSigner.IssueToken(kid, externalCustomerId);
    }

    public void Dispose() => _rsa.Dispose();
}
