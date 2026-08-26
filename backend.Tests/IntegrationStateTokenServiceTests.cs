using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Unit-level tests for IIntegrationStateTokenService — the signed,
/// short-lived OAuth state token described in that class's own doc comment.
/// Resolved from the real DI container so it uses the real configured
/// signing key, with no HTTP call involved.
///
/// Each test targets exactly one of the four security properties documented
/// on IntegrationStateTokenService: (1) signature/shape integrity, (2)
/// expiry, (3) — deliberately not covered here, since it's the OAuth
/// provider's own guarantee, outside this class — and the platform-binding
/// check that IntegrationsController relies on together with the live
/// cookie check (business/user cross-check lives in the controller, not
/// here; that's covered by IntegrationsApiTests instead).
/// </summary>
public class IntegrationStateTokenServiceTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private (IIntegrationStateTokenService Service, IntegrationStateTokenOptions Options) Resolve(IServiceScope scope) => (
        scope.ServiceProvider.GetRequiredService<IIntegrationStateTokenService>(),
        scope.ServiceProvider.GetRequiredService<IOptions<IntegrationStateTokenOptions>>().Value);

    [Fact]
    public void Issue_Then_Validate_RoundTrips()
    {
        using var scope = _factory.Services.CreateScope();
        var (service, _) = Resolve(scope);
        var businessId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var token = service.Issue(businessId, userId, "shopify");
        var claims = service.Validate(token, "shopify");

        Assert.NotNull(claims);
        Assert.Equal(businessId, claims!.BusinessId);
        Assert.Equal(userId, claims.UserId);
        Assert.Equal("shopify", claims.Platform);
    }

    [Fact]
    public void Validate_PlatformMismatch_ReturnsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var (service, _) = Resolve(scope);
        var token = service.Issue(Guid.NewGuid(), Guid.NewGuid(), "shopify");

        // A state token minted for one platform must not validate against a
        // callback for a different platform route.
        Assert.Null(service.Validate(token, "slack"));
    }

    [Fact]
    public void Validate_TamperedSignature_ReturnsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var (service, _) = Resolve(scope);
        var token = service.Issue(Guid.NewGuid(), Guid.NewGuid(), "shopify");

        var lastDot = token.LastIndexOf('.');
        var tampered = token[..lastDot] + "." + new string(token[(lastDot + 1)..].Reverse().ToArray());

        Assert.Null(service.Validate(tampered, "shopify"));
    }

    [Fact]
    public void Validate_MalformedToken_ReturnsNullWithoutThrowing()
    {
        using var scope = _factory.Services.CreateScope();
        var (service, _) = Resolve(scope);

        Assert.Null(service.Validate("not-a-jwt-at-all", "shopify"));
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var (service, options) = Resolve(scope);

        // Crafted directly with the same signing key/issuer/audience the
        // real service uses, but with an expiry in the past — Issue() itself
        // has no way to backdate a token, so this is the only way to prove
        // expiry is actually enforced without sleeping the test for real
        // minutes.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim("nivora:integration_state:business_id", Guid.NewGuid().ToString()),
            new Claim("nivora:integration_state:user_id", Guid.NewGuid().ToString()),
            new Claim("nivora:integration_state:platform", "shopify"),
        };
        var expiredToken = new JwtSecurityToken(
            issuer: IntegrationStateTokenService.Issuer,
            audience: IntegrationStateTokenService.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(-30),
            signingCredentials: credentials);
        var expiredTokenString = new JwtSecurityTokenHandler().WriteToken(expiredToken);

        Assert.Null(service.Validate(expiredTokenString, "shopify"));
    }
}
