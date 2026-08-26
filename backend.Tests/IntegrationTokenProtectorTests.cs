using System.Security.Cryptography;
using backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Unit-level tests for IIntegrationTokenProtector, resolved from the real
/// DI container (NivoraApiFactory) so it exercises the real, EF-backed
/// (Postgres) Data Protection key ring configured in Program.cs — not a
/// fake/in-memory provider — while staying at the service level, with no
/// HTTP call involved.
/// </summary>
public class IntegrationTokenProtectorTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public void Protect_Then_Unprotect_RoundTrips()
    {
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        const string plaintext = "shpat_super-secret-shopify-access-token";

        var ciphertext = protector.Protect("shopify", plaintext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.Equal(plaintext, protector.Unprotect("shopify", ciphertext));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_ThrowsRatherThanReturningGarbage()
    {
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var ciphertext = protector.Protect("shopify", "some-token-value");

        // Flip a character in the middle of the payload — Data Protection's
        // authenticated encryption must detect this, never silently return
        // corrupted plaintext.
        var chars = ciphertext.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("shopify", tampered));
    }

    [Fact]
    public void Unprotect_WrongPurposePlatform_ThrowsRatherThanCrossDecrypting()
    {
        // Purpose-string separation, not blast-radius containment (see
        // IIntegrationTokenProtector's own doc comment): ciphertext
        // produced for one platform's purpose must never be decryptable
        // through a different platform's purpose string.
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IIntegrationTokenProtector>();
        var ciphertext = protector.Protect("shopify", "some-token-value");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("slack", ciphertext));
    }
}
