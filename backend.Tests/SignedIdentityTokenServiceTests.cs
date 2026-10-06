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
using Npgsql;

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
    private string _adoConnectionString = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        // Identity-binding/erasure-integrity Phase 5's lock-freshness test
        // needs a raw, independent connection (not routed through the app's
        // own DI-managed AppDbContext) to act as a deterministic
        // advisory-lock barrier — same technique
        // CustomerDataLifecycleTests.cs/CustomersApiTests.cs already
        // established for their own ForCustomerErasure races.
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);

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
            // Identity-binding/erasure-integrity Phase 5 — a real,
            // checkpoint-pausable CustomerErasureService is constructed
            // directly in the tests below (mirroring
            // CustomerDataLifecycleTests.cs/CustomersApiTests.cs's own Phase
            // 3/4 tests), so its own dependencies need DI registrations
            // here too. Erasure itself is never mocked — only
            // ICustomerErasureCheckpoint (an intentional test seam, no-op
            // in production) is substituted.
            .AddScoped<IConversationPurgeService, ConversationPurgeService>()
            .AddScoped<IPurgeFailurePoint, NoOpPurgeFailurePoint>()
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddScoped<ICustomerErasureCheckpoint, NoOpCustomerErasureCheckpoint>()
            .BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
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

    // ── Identity-binding/erasure-integrity Phase 5 (Decision 3, Path 2a) ──
    //
    // docs/handoffs/identity-binding-erasure-integrity-design.md §5a;
    // implementation-plan.md's Phase 5. Erasure is never mocked — only
    // ICustomerErasureCheckpoint (a no-op in production) is substituted,
    // same convention CustomerDataLifecycleTests.cs/CustomersApiTests.cs
    // already established for their own Phase 3/4 tests.

    [Fact]
    public async Task ValidateAndResolveAsync_ErasurePendingMarkerSet_RejectsWithoutMutation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        var first = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t1-first"), CancellationToken.None);
        Assert.Equal(SignedIdentityTokenOutcome.Verified, first.Outcome);
        var firstIdentity = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId);

        await db.Customers.IgnoreQueryFilters()
            .Where(c => c.Id == first.CustomerId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, DateTimeOffset.UtcNow));

        var second = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t1-second"), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, second.Outcome);
        Assert.Null(second.CustomerId);

        var identities = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();
        Assert.Single(identities);
        Assert.Equal(firstIdentity.Id, identities[0].Id);
        Assert.Equal(firstIdentity.ExpiresAt, identities[0].ExpiresAt);

        var customer = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == first.CustomerId);
        Assert.False(customer.IsAnonymized);
        Assert.NotNull(customer.PendingErasureAt);
        Assert.Equal(externalId, customer.ExternalId);

        var consumedSecond = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .AnyAsync(t => t.BusinessId == businessId && t.Jti == "p5-t1-second");
        Assert.False(consumedSecond);
    }

    /// <summary>
    /// Proves the recheck is a fresh, AsNoTracking, post-lock query — not a
    /// re-read of the tracked `existingCustomer` instance
    /// ResolveCustomerAndIdentityAsync already reloaded before acquiring
    /// the lock. A raw barrier connection holds ForCustomerErasure open
    /// first; the service call's own Customer insert conflicts (existing
    /// row), it reloads the existing customer (PendingErasureAt == null at
    /// that point), then blocks acquiring the same lock. Only once exactly
    /// one waiter is confirmed (pg_locks, not a timer) does the barrier set
    /// PendingErasureAt and commit, releasing the lock — the service call's
    /// fresh recheck, issued strictly after it acquires the now-free lock,
    /// must observe the just-committed value and reject.
    /// </summary>
    [Fact]
    public async Task ValidateAndResolveAsync_BlockedOnLock_ObservesPendingErasureCommittedWhileWaiting()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        var seed = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t2-seed"), CancellationToken.None);
        Assert.Equal(SignedIdentityTokenOutcome.Verified, seed.Outcome);
        var customerId = seed.CustomerId!.Value;

        var lockKey = AdvisoryLockKeyGenerator.ForCustomerErasure(businessId, customerId);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        try
        {
            var callToken = signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t2-call");

            async Task<SignedIdentityTokenResult> CallAsync()
            {
                await using var callScope = _provider.CreateAsyncScope();
                var callService = callScope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
                return await callService.ValidateAndResolveAsync(businessId, callToken, CancellationToken.None);
            }

            var callTask = CallAsync();

            await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, expected: 1, raceName: "SignedIdentityToken vs erasure marker freshness");
            Assert.False(callTask.IsCompleted);

            await using (var duringCmd = new NpgsqlCommand(
                "UPDATE \"Customers\" SET \"PendingErasureAt\" = now() WHERE \"Id\" = @id", barrierConnection, barrierTransaction))
            {
                duringCmd.Parameters.AddWithValue("id", customerId);
                await duringCmd.ExecuteNonQueryAsync();
            }
            await barrierTransaction.CommitAsync();

            var result = await callTask;
            Assert.Equal(SignedIdentityTokenOutcome.Rejected, result.Outcome);
            Assert.Null(result.CustomerId);
        }
        finally
        {
            await barrierConnection.DisposeAsync();
        }

        var identities = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();
        Assert.Single(identities); // only the seed's — nothing written/refreshed by the blocked call

        var consumedCall = await db.ConsumedSignedIdentityTokens.IgnoreQueryFilters()
            .AnyAsync(t => t.BusinessId == businessId && t.Jti == "p5-t2-call");
        Assert.False(consumedCall);
    }

    /// <summary>
    /// The literal Decision-3 escape demonstration: an unfixed recheck (or
    /// no recheck at all) lets this call create/refresh an ExternalIdentity
    /// row strictly after Phase 1's one-time delete sweep has already run
    /// for this customer, so the row survives the otherwise-completed
    /// erasure. Assertion order matters — the surviving-row check runs
    /// before the Rejected check, so unfixed code fails at the row-survival
    /// assertion, not merely at the outcome assertion.
    /// </summary>
    [Fact]
    public async Task ValidateAndResolveAsync_AfterPhaseOneCheckpoint_RejectsAndNoIdentitySurvivesErasure()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var businessId = await CreateBusinessAsync(db);
        var staffUserId = await CreateStaffUserAsync(db, businessId);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);

        var seed = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t3-seed"), CancellationToken.None);
        Assert.Equal(SignedIdentityTokenOutcome.Verified, seed.Outcome);
        var customerId = seed.CustomerId!.Value;

        await using var erasureScope = _provider.CreateAsyncScope();
        var erasureDb = erasureScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var erasurePurgeService = erasureScope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var erasureAuditLogService = erasureScope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var gate = new GatedCustomerErasureCheckpoint("after-phase-1-committed");
        var erasureService = new CustomerErasureService(erasureDb, erasurePurgeService, erasureAuditLogService, gate);

        var eraseTask = erasureService.EraseCustomerAsync(customerId, staffUserId);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var duringScope = _provider.CreateAsyncScope())
        {
            var duringDb = duringScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var during = await duringDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
            Assert.NotNull(during.PendingErasureAt);
            Assert.False(during.IsAnonymized);
            Assert.False(await duringDb.ExternalIdentities.IgnoreQueryFilters().AnyAsync(ei => ei.CustomerId == customerId));
        }

        var capturedResult = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t3-call"), CancellationToken.None);

        gate.Release();
        await eraseTask;

        await using var afterScope = _provider.CreateAsyncScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Assertion order matters — see doc comment above.
        var survivingIdentities = await afterDb.ExternalIdentities.IgnoreQueryFilters()
            .CountAsync(ei => ei.CustomerId == customerId);
        Assert.Equal(0, survivingIdentities);
        var afterCustomer = await afterDb.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(afterCustomer.IsAnonymized);

        Assert.Equal(SignedIdentityTokenOutcome.Rejected, capturedResult.Outcome);
        Assert.Null(capturedResult.CustomerId);
    }

    /// <summary>
    /// Owner-approved: once erasure has completed, the erased customer's
    /// ExternalId is null and a fresh signed-token authentication
    /// legitimately creates a new, unlinked Customer. Decision 3 holds
    /// because no identity belonging to the erased customer survives.
    /// While erasure is in flight the unique index still holds ExternalId
    /// and the Phase 5 recheck rejects (see the tests above).
    /// </summary>
    [Fact]
    public async Task ValidateAndResolveAsync_AfterCompletedErasure_SameExternalId_CreatesFreshUnlinkedCustomer()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISignedIdentityTokenService>();
        var purgeService = scope.ServiceProvider.GetRequiredService<IConversationPurgeService>();
        var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var businessId = await CreateBusinessAsync(db);
        var staffUserId = await CreateStaffUserAsync(db, businessId);
        using var signer = new FakeSignedTokenSigner();
        var kid = await RegisterKeyAsync(db, businessId, signer);
        var externalId = ShopifyGid.ForCustomer(ExternalCustomerId);

        var seed = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t5-seed"), CancellationToken.None);
        Assert.Equal(SignedIdentityTokenOutcome.Verified, seed.Outcome);
        var originalCustomerId = seed.CustomerId!.Value;

        var erasureService = new CustomerErasureService(db, purgeService, auditLogService, new NoOpCustomerErasureCheckpoint());
        await erasureService.EraseCustomerAsync(originalCustomerId, staffUserId);

        var result = await service.ValidateAndResolveAsync(
            businessId, signer.IssueToken(kid, ExternalCustomerId, jti: "p5-t5-call"), CancellationToken.None);

        Assert.Equal(SignedIdentityTokenOutcome.Verified, result.Outcome);
        Assert.NotNull(result.CustomerId);
        Assert.NotEqual(originalCustomerId, result.CustomerId);

        var newCustomer = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == result.CustomerId);
        Assert.Equal(externalId, newCustomer.ExternalId);
        Assert.False(newCustomer.IsAnonymized);

        var originalCustomer = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == originalCustomerId);
        Assert.True(originalCustomer.IsAnonymized);
        Assert.Null(originalCustomer.ExternalId);

        var originalIdentityCount = await db.ExternalIdentities.IgnoreQueryFilters().CountAsync(ei => ei.CustomerId == originalCustomerId);
        Assert.Equal(0, originalIdentityCount);

        var identitiesForExternalId = await db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .Where(ei => ei.BusinessId == businessId && ei.ExternalUserId == externalId)
            .ToListAsync();
        Assert.Single(identitiesForExternalId);
        Assert.Equal(result.CustomerId, identitiesForExternalId[0].CustomerId);
    }

    private async Task<int> CountAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey)
    {
        var classId = unchecked((uint)(lockKey >> 32));
        var objId = unchecked((uint)lockKey);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks " +
            "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1 " +
            "AND classid::bigint = @classid AND objid::bigint = @objid",
            barrierConnection);
        cmd.Parameters.AddWithValue("classid", (long)classId);
        cmd.Parameters.AddWithValue("objid", (long)objId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Deterministic wait-for-condition, not a fixed sleep — same
    /// established convention as CustomerDataLifecycleTests.cs/
    /// CustomersApiTests.cs's own helper of this name.</summary>
    private async Task WaitForAdvisoryLockWaitersAsync(NpgsqlConnection barrierConnection, long lockKey, int expected, string raceName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int count;
        do
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"Expected {expected} concurrent advisory-lock waiters for the {raceName} race within 10s, " +
                    "but they did not appear. If the concurrency protection under test has been removed or is " +
                    "no longer reached, this is the expected failure mode.");
            }

            count = await CountAdvisoryLockWaitersAsync(barrierConnection, lockKey);
            if (count < expected)
            {
                await Task.Delay(25);
            }
        } while (count < expected);
    }

    /// <summary>Opens a raw barrier connection and pre-acquires the given
    /// advisory lock, holding it open until the caller commits/rolls back
    /// and disposes the returned transaction — same idiom as
    /// CustomerDataLifecycleTests.cs/CustomersApiTests.cs's own
    /// AcquireBarrierAsync.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AcquireBarrierAsync(long lockKey)
    {
        var connection = new NpgsqlConnection(_adoConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({lockKey})", connection, transaction))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }
        return (connection, transaction);
    }

    /// <summary>
    /// Same TaskCompletionSource-gated pause idiom as
    /// CustomerDataLifecycleTests.cs/CustomersApiTests.cs's own
    /// GatedCustomerErasureCheckpoint, scoped to one named checkpoint —
    /// every other checkpoint name passes through immediately, un-paused.
    /// </summary>
    private class GatedCustomerErasureCheckpoint(string checkpoint) : ICustomerErasureCheckpoint
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _releaseGate.TrySetResult();

        public async Task ReachedAsync(string checkpointName, CancellationToken cancellationToken)
        {
            if (checkpointName != checkpoint)
            {
                return;
            }

            _reached.TrySetResult();
            await _releaseGate.Task;
        }
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
