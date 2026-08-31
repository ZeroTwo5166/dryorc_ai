using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace backend.Tests;

/// <summary>
/// Service-level tests for SignedIdentityTokenService — real Postgres,
/// bare ServiceProvider, same convention as VerificationCodeServiceTests/
/// ShopifyOrderCancellationExecutorTests. FakeSignedTokenSigner produces
/// controllable RS256 tokens against an in-test RSA keypair, registered as
/// a BusinessSigningKey row's public key.
/// </summary>
public class SignedIdentityTokenServiceTests : IAsyncLifetime
{
    private const long ExternalCustomerId = 123456;

    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddSingleton(Options.Create(new SignedTokenOptions
            {
                MaxTokenLifetimeMinutes = 5,
                GrantDurationHours = 4,
                ClockSkewMinutes = 1,
            }))
            .AddScoped<ISignedIdentityTokenService, SignedIdentityTokenService>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ConsumedSignedIdentityTokens\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ExternalIdentities\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"BusinessSigningKeys\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Signed Token Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateStaffUserAsync(AppDbContext db, Guid businessId)
    {
        var email = $"signing-key-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> RegisterKeyAsync(
        AppDbContext db, Guid businessId, FakeSignedTokenSigner signer,
        DateTimeOffset? revokedAt = null, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var createdByUserId = await CreateStaffUserAsync(db, businessId);
        var key = new BusinessSigningKey
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            PublicKeyPem = signer.PublicKeyPem,
            Algorithm = algorithm,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = revokedAt,
        };
        db.BusinessSigningKeys.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }

    // ── Cryptographic validation ──────────────────────────────────────

    [Fact]
    public async Task ValidateAndResolveAsync_ValidToken_ReturnsVerifiedWithCustomerId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueToken(kid, ExternalCustomerId);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, result.Outcome);
        Assert.NotNull(result.CustomerId);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_InvalidSignature_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueTokenSignedByWrongKey(kid, ExternalCustomerId);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
        Assert.Null(result.CustomerId);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_UnsupportedAlgorithm_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        // Same key, different algorithm — must be rejected by the explicit
        // ValidAlgorithms allow-list, not merely "happen to still verify."
        var token = signer.IssueToken(kid, ExternalCustomerId, algorithm: SecurityAlgorithms.RsaSha384);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_UnknownKid_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();

        var token = signer.IssueToken(Guid.NewGuid(), ExternalCustomerId);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_RevokedKey_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var token = signer.IssueToken(kid, ExternalCustomerId);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_WrongIssuer_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueToken(kid, ExternalCustomerId, issuer: "not-nivora");
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_WrongAudience_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueToken(kid, ExternalCustomerId, audience: "not-nivora");
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ExpiredToken_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var issuedAt = DateTime.UtcNow.AddMinutes(-10);
        var token = signer.IssueToken(kid, ExternalCustomerId, issuedAt: issuedAt, expires: issuedAt.AddMinutes(3));
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ExcessiveLifetimeWindow_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        // Still unexpired (exp is in the future) but exp-iat exceeds the
        // configured 5-minute maximum — must be rejected by the explicit
        // check, not merely pass because exp hasn't been reached yet.
        var issuedAt = DateTime.UtcNow;
        var token = signer.IssueToken(kid, ExternalCustomerId, issuedAt: issuedAt, expires: issuedAt.AddHours(1));
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_MissingJti_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueToken(kid, ExternalCustomerId, omitJtiClaim: true);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_MissingExternalCustomerId_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var token = signer.IssueToken(kid, ExternalCustomerId, omitExternalCustomerIdClaim: true);
        var result = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_MalformedToken_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();

        var result = await service.ValidateAndResolveAsync(Guid.NewGuid(), "not-a-jwt", CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    // ── Tenant isolation ───────────────────────────────────────────────

    [Fact]
    public async Task ValidateAndResolveAsync_KeyBelongsToDifferentBusiness_Rejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessAId = await CreateBusinessAsync(db);
        var businessBId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kidForA = await RegisterKeyAsync(db, businessAId, signer);

        // A validly-signed token under Business A's key, presented against
        // Business B's widget context.
        var token = signer.IssueToken(kidForA, ExternalCustomerId);
        var result = await service.ValidateAndResolveAsync(businessBId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_SameExternalId_DifferentBusinesses_IsolatedCustomers()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessAId = await CreateBusinessAsync(db);
        var businessBId = await CreateBusinessAsync(db);
        using var signerA = new FakeSignedTokenSigner();
        using var signerB = new FakeSignedTokenSigner();
        var kidA = await RegisterKeyAsync(db, businessAId, signerA);
        var kidB = await RegisterKeyAsync(db, businessBId, signerB);

        var resultA = await service.ValidateAndResolveAsync(businessAId, signerA.IssueToken(kidA, ExternalCustomerId), CancellationToken.None);
        var resultB = await service.ValidateAndResolveAsync(businessBId, signerB.IssueToken(kidB, ExternalCustomerId), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, resultA.Outcome);
        Assert.Equal(SignedIdentityTokenOutcome.Verified, resultB.Outcome);
        Assert.NotEqual(resultA.CustomerId, resultB.CustomerId);
    }

    // ── Replay ───────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAndResolveAsync_SameTokenTwice_SecondUseRejected()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var token = signer.IssueToken(kid, ExternalCustomerId);

        var first = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);
        var second = await service.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, first.Outcome);
        Assert.Equal(SignedIdentityTokenOutcome.Rejected, second.Outcome);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ConcurrentUseOfSameToken_ExactlyOneSucceeds()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var token = signer.IssueToken(kid, ExternalCustomerId);

        // Pure DB-unique-index race (BusinessId, Jti) — no advisory lock
        // involved, same shape as LinkIdentity's own concurrent test.
        // Two independent scopes/connections via Task.WhenAll suffices;
        // no forced pg_locks barrier needed for this race shape.
        async Task<SignedIdentityTokenResult> CallAsync()
        {
            await using var callScope = _provider.CreateAsyncScope();
            var callService = callScope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
            return await callService.ValidateAndResolveAsync(businessId, token, CancellationToken.None);
        }

        var results = await Task.WhenAll(CallAsync(), CallAsync());

        Assert.Single(results, r => r.Outcome == SignedIdentityTokenOutcome.Verified);
        Assert.Single(results, r => r.Outcome == SignedIdentityTokenOutcome.Rejected);

        var consumedCount = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .CountAsync(t => t.BusinessId == businessId);
        Assert.Equal(1, consumedCount);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_FailureAfterJtiInsert_RollsBackJti_TokenRetryableAfterward()
    {
        // Proves the required invariant: JTI insertion followed by a forced
        // downstream failure rolls back entirely (transaction/savepoint
        // dependency, implementation-ready spec §4B), and the same
        // otherwise-valid, unexpired token can subsequently be retried
        // successfully — never permanently burned by an unrelated failure.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var token = signer.IssueToken(kid, ExternalCustomerId);

        // Force the downstream Customer/ExternalIdentity work to fail
        // GENUINELY AFTER the jti insert has already succeeded — not
        // before it. A pre-cancelled token was tried first and rejected
        // during independent verification: ValidateAndResolveAsync's very
        // first DB call is the BusinessSigningKeys kid lookup, well before
        // BeginTransactionAsync/the jti insert, so a pre-cancelled token
        // aborts there and never proves anything about the transaction at
        // all. Instead, hook DbContext.SavedChanges (fires synchronously
        // right after a SaveChangesAsync call actually persists) and
        // cancel on the FIRST such event — inside ValidateAndResolveAsync,
        // the jti insert's SaveChangesAsync is the first one to run, so by
        // the time this fires, the jti row has already been written to the
        // (still-uncommitted) transaction; the cancellation then aborts the
        // very next operation, ResolveCustomerAndIdentityAsync's own
        // SaveChangesAsync for the Customer insert — genuinely downstream
        // of the jti insert.
        using var cts = new CancellationTokenSource();
        var savedChangesCount = 0;
        void CancelAfterFirstSave(object? sender, SavedChangesEventArgs e)
        {
            if (++savedChangesCount == 1)
            {
                cts.Cancel();
            }
        }
        db.SavedChanges += CancelAfterFirstSave;

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>()
                    .ValidateAndResolveAsync(businessId, token, cts.Token));
        }
        finally
        {
            db.SavedChanges -= CancelAfterFirstSave;
        }

        // Confirms the failure genuinely occurred after the jti insert
        // (savedChangesCount == 1 means exactly the jti insert's own
        // SaveChangesAsync completed before cancellation fired) — not
        // before it, closing the exact gap the pre-cancelled-token version
        // of this test had.
        Assert.Equal(1, savedChangesCount);

        var consumedCount = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .CountAsync(t => t.BusinessId == businessId);
        Assert.Equal(0, consumedCount); // rolled back together with everything else in the same transaction

        // Retry with a fresh cancellation token (still-valid, unexpired,
        // never-actually-consumed token) succeeds.
        await using var retryScope = _provider.CreateAsyncScope();
        var retryService = retryScope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var retryResult = await retryService.ValidateAndResolveAsync(businessId, token, CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, retryResult.Outcome);

        var noPartialCustomers = await db.Customers.IgnoreQueryFilters().CountAsync(c => c.BusinessId == businessId);
        Assert.Equal(1, noPartialCustomers); // exactly the retry's Customer — no partial row from the failed attempt
    }

    // ── Customer / ExternalIdentity resolution ────────────────────────

    [Fact]
    public async Task ValidateAndResolveAsync_ExistingCustomer_Reused()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var first = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);
        var second = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, second.Outcome);
        Assert.Equal(first.CustomerId, second.CustomerId);

        var customerCount = await db.Customers.IgnoreQueryFilters()
            .CountAsync(c => c.BusinessId == businessId && c.ExternalId == ShopifyGid.ForCustomer(ExternalCustomerId));
        Assert.Equal(1, customerCount);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_SecondValidAssertion_RefreshesExistingActiveIdentity()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var first = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);
        var firstIdentity = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId);

        // Force time to actually differ, matching how ExecuteUpdateAsync
        // would produce a strictly-later ExpiresAt on a real refresh.
        await Task.Delay(50);

        var second = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);

        var identities = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();

        Assert.Equal(SignedIdentityTokenOutcome.Verified, second.Outcome);
        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Single(identities); // refreshed in place, never duplicated
        Assert.True(identities[0].ExpiresAt > firstIdentity.ExpiresAt);
        Assert.Equal(firstIdentity.Id, identities[0].Id);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ExpiredButNotRevokedIdentity_Refreshed_NotDuplicated()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var first = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        // Force the row into an already-expired-but-not-revoked state.
        await db.ExternalIdentities.IgnoreQueryFilters()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ExecuteUpdateAsync(s => s.SetProperty(ei => ei.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var second = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, second.Outcome);
        Assert.Equal(first.CustomerId, second.CustomerId);

        var identities = await db.ExternalIdentities.IgnoreQueryFilters()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();
        Assert.Single(identities);
        Assert.True(identities[0].ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_RevokedIdentity_CreatesNewActiveRow_PreservesHistoricalRevokedRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var first = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        // Simulate staff revocation (the existing RevokeIdentity endpoint's
        // own effect) of the only current row.
        await db.ExternalIdentities.IgnoreQueryFilters()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ExecuteUpdateAsync(s => s.SetProperty(ei => ei.RevokedAt, DateTimeOffset.UtcNow));

        var second = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, second.Outcome);
        Assert.Equal(first.CustomerId, second.CustomerId);

        var identities = await db.ExternalIdentities.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();
        Assert.Equal(2, identities.Count); // the historical revoked row, plus a fresh active one
        Assert.Single(identities, ei => ei.RevokedAt != null);
        Assert.Single(identities, ei => ei.RevokedAt == null);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ExistingIdentityOwnedByDifferentCustomer_FailsClosed_NeverReassigns()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        // Simulate the one realistic path to this anomaly the spec itself
        // names: a prior manual staff LinkIdentity action already assigned
        // this exact (Channel, ExternalUserId) to a different Customer than
        // the one Customer.ExternalId resolution will produce.
        var otherCustomer = new Customer
        {
            Id = Guid.NewGuid(), BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(otherCustomer);
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), BusinessId = businessId, CustomerId = otherCustomer.Id,
            Channel = ConversationChannels.Website, VerificationMethod = IdentityVerificationMethods.SignedToken,
            ExternalUserId = externalId, LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await service.ValidateAndResolveAsync(businessId, signer.IssueToken(kid, ExternalCustomerId), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);

        // The pre-existing identity's ownership must be completely untouched.
        var identity = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId);
        Assert.Equal(otherCustomer.Id, identity.CustomerId);
    }

    [Fact]
    public async Task ValidateAndResolveAsync_ConcurrentNewIdentityRequests_ExactlyOneActiveIdentity()
    {
        // Genuine concurrency proof for the ExternalIdentity race the
        // independent review specifically required — two different valid
        // tokens (different jti, so replay protection does not serialize
        // them), same intended Shopify identity, arriving concurrently.
        // Two independent scopes/connections via Task.WhenAll, real
        // Postgres, no advisory-lock barrier (pure unique-index race, same
        // precedent as LinkIdentity's own concurrent test).
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var tokenA = signer.IssueToken(kid, ExternalCustomerId);
        var tokenB = signer.IssueToken(kid, ExternalCustomerId);

        async Task<SignedIdentityTokenResult> CallAsync(string token)
        {
            await using var callScope = _provider.CreateAsyncScope();
            var callService = callScope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
            return await callService.ValidateAndResolveAsync(businessId, token, CancellationToken.None);
        }

        var results = await Task.WhenAll(CallAsync(tokenA), CallAsync(tokenB));

        Assert.All(results, r => Assert.Equal(SignedIdentityTokenOutcome.Verified, r.Outcome));
        Assert.Equal(results[0].CustomerId, results[1].CustomerId);

        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);
        var customerCount = await db.Customers.IgnoreQueryFilters()
            .CountAsync(c => c.BusinessId == businessId && c.ExternalId == externalId);
        Assert.Equal(1, customerCount);

        var activeIdentityCount = await db.ExternalIdentities.IgnoreQueryFilters()
            .CountAsync(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId && ei.RevokedAt == null);
        Assert.Equal(1, activeIdentityCount);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
