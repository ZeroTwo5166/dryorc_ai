using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using backend.Constants;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace backend.Services;

/// <summary>
/// See ISignedIdentityTokenService for the contract. IgnoreQueryFilters()
/// + explicit BusinessId throughout, matching VerificationCodeService/
/// OrderCancellationService's non-HTTP-service posture — this service is
/// called from WidgetController.Init but is deliberately written as if it
/// has no HTTP-request-scoped tenant guarantee of its own.
///
/// Validation ordering (fail-closed at every step): parse -&gt; kid lookup -&gt;
/// business-binding check -&gt; revocation check -&gt; algorithm check -&gt;
/// signature/standard-claims validation (RS256 only, ValidAlgorithms
/// pinned) -&gt; explicit max-lifetime check -&gt; jti extraction -&gt; external
/// customer id extraction/normalization -&gt; jti-insert (the sole replay
/// arbiter, first inside the shared transaction) -&gt; Customer resolution -&gt;
/// ExternalIdentity resolution. See the implementation-ready spec's §2/§4/
/// §5/§5A-§5E for the full design this mirrors.
/// </summary>
public class SignedIdentityTokenService(
    AppDbContext db,
    IOptions<SignedTokenOptions> options,
    ILogger<SignedIdentityTokenService> logger) : ISignedIdentityTokenService
{
    public const string Issuer = "nivora-customer-identity";
    public const string Audience = "nivora-customer-identity";

    /// <summary>The raw numeric Shopify customer id the business's storefront
    /// asserts — never a GID; ShopifyGid.ForCustomer normalizes it Nivora-side
    /// (design doc Decision 19's reasoning, reused here).</summary>
    private const string ExternalCustomerIdClaim = "external_customer_id";

    private const string ConsumedTokenUniqueIndexName = "IX_ConsumedSignedIdentityTokens_BusinessId_Jti";
    private const string CustomerExternalIdUniqueIndexName = "IX_Customers_BusinessId_ExternalId";
    private const string ExternalIdentityUniqueIndexName = "IX_ExternalIdentities_BusinessId_Channel_ExternalUserId";

    public async Task<SignedIdentityTokenResult> ValidateAndResolveAsync(
        Guid businessId, string token, CancellationToken cancellationToken)
    {
        JwtSecurityToken jwt;
        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Signed identity token for business {BusinessId} could not be parsed.", businessId);
            return Rejected();
        }

        if (!Guid.TryParse(jwt.Header.Kid, out var kid))
        {
            logger.LogWarning("Signed identity token for business {BusinessId} had a missing/malformed kid.", businessId);
            return Rejected();
        }

        var key = await db.BusinessSigningKeys
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Id == kid, cancellationToken);

        if (key is null)
        {
            logger.LogWarning("Signed identity token referenced unknown kid {Kid}.", kid);
            return Rejected();
        }

        // Mandatory business binding — the selected key row's OWN BusinessId
        // must match the caller's already-resolved business. Never trust a
        // claim inside the token for this; kid selects a row, the row's own
        // column is what's authoritative.
        if (key.BusinessId != businessId)
        {
            logger.LogWarning(
                "Signed identity token's kid {Kid} belongs to a different business than the one it was presented to.", kid);
            return Rejected();
        }

        if (key.RevokedAt is not null)
        {
            logger.LogWarning("Signed identity token referenced revoked key {Kid}.", kid);
            return Rejected();
        }

        if (key.Algorithm != SecurityAlgorithms.RsaSha256)
        {
            // Defensive — no row should ever be stored with anything else in
            // v1, but never assume it based on what the registration path
            // currently allows.
            logger.LogError("BusinessSigningKey {Kid} has unsupported algorithm {Algorithm}.", kid, key.Algorithm);
            return Rejected();
        }

        RSA rsa;
        try
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "BusinessSigningKey {Kid} has an unparsable public key.", kid);
            return Rejected();
        }

        using (rsa)
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                // KeyId must match the token's own kid header — the
                // library's own signature validation enforces this
                // matching internally (IDX10503 otherwise), on top of (not
                // instead of) our own explicit kid->BusinessSigningKey
                // lookup and business-binding check already performed
                // above.
                IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = jwt.Header.Kid },
                // Explicit allow-list, not left to default behavior — the
                // algorithm-confusion defense. RS256 only, matching the
                // owner-locked decision.
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(options.Value.ClockSkewMinutes),
                // Microsoft.IdentityModel.Tokens caches SignatureProviders
                // keyed by the key material's own content hash (SecurityKey
                // .InternalId), not C# object identity — since a fresh RSA
                // object is imported from PEM on every call and disposed at
                // the end of this method (see the enclosing `using (rsa)`),
                // two different BusinessSigningKey validations for the same
                // stored key would otherwise collide in that cache and the
                // second could try to use the first call's already-disposed
                // RSA object. Disabling the cache for this per-call,
                // ephemeral key avoids that entirely — it is not a
                // performance-sensitive path (one validation per widget
                // Init call).
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };

            ClaimsPrincipal principal;
            SecurityToken validatedToken;
            try
            {
                principal = new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out validatedToken);
            }
            catch (SecurityTokenException)
            {
                return Rejected();
            }
            catch (ArgumentException)
            {
                return Rejected();
            }

            var validatedJwt = (JwtSecurityToken)validatedToken;

            // Explicit max-lifetime cap — ValidateLifetime only checks exp
            // against now; it does not bound how long a token's own stated
            // validity window (exp - iat, exposed here as ValidTo - ValidFrom)
            // can be. A business's signing system, compromised or merely
            // misconfigured, could otherwise mint an arbitrarily-long-lived
            // token that still passes standard validation.
            if (validatedJwt.ValidTo - validatedJwt.ValidFrom > TimeSpan.FromMinutes(options.Value.MaxTokenLifetimeMinutes))
            {
                logger.LogWarning(
                    "Signed identity token for business {BusinessId} exceeded the maximum allowed lifetime.", businessId);
                return Rejected();
            }

            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            if (string.IsNullOrEmpty(jti))
            {
                logger.LogWarning("Signed identity token for business {BusinessId} was missing jti.", businessId);
                return Rejected();
            }

            var externalCustomerIdRaw = principal.FindFirst(ExternalCustomerIdClaim)?.Value;
            if (!long.TryParse(externalCustomerIdRaw, out var numericCustomerId))
            {
                logger.LogWarning(
                    "Signed identity token for business {BusinessId} was missing/malformed {Claim}.",
                    businessId, ExternalCustomerIdClaim);
                return Rejected();
            }

            string externalId;
            try
            {
                externalId = ShopifyGid.ForCustomer(numericCustomerId);
            }
            catch (ArgumentOutOfRangeException)
            {
                logger.LogWarning(
                    "Signed identity token for business {BusinessId} carried a non-positive external customer id.", businessId);
                return Rejected();
            }

            // Everything from here on happens inside one transaction — jti
            // consumption and Customer/ExternalIdentity work are atomic
            // together. A failure at any later step rolls back the jti
            // insert too, leaving a legitimate token retriable within its
            // remaining lifetime (see the design doc's §4B).
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                db.ConsumedSignedIdentityTokens.Add(new ConsumedSignedIdentityToken
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Jti = jti,
                    ConsumedAt = DateTimeOffset.UtcNow,
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (
                    ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: ConsumedTokenUniqueIndexName })
                {
                    await transaction.RollbackAsync(cancellationToken);
                    logger.LogWarning(
                        "Signed identity token replay detected for business {BusinessId}, jti {Jti}.", businessId, jti);
                    return Rejected();
                }

                var customerId = await ResolveCustomerAndIdentityAsync(businessId, externalId, cancellationToken);
                if (customerId is null)
                {
                    // Ownership conflict (§5D states 4/7) or a revoke-race on
                    // refresh — fail closed, never treat as verified.
                    await transaction.RollbackAsync(cancellationToken);
                    return Rejected();
                }

                await transaction.CommitAsync(cancellationToken);
                return new SignedIdentityTokenResult(SignedIdentityTokenOutcome.Verified, customerId);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Customer resolution (insert-first/catch-23505/reload against the
    /// existing (BusinessId, ExternalId) unique index), then ExternalIdentity
    /// create-or-refresh (insert-first/catch-23505/reload-and-refresh
    /// against the existing filtered (BusinessId, Channel, ExternalUserId)
    /// unique index) — the full §5/§5A-§5E state machine. Returns null only
    /// for the fail-closed ownership-conflict/revoke-race cases.
    /// </summary>
    private async Task<Guid?> ResolveCustomerAndIdentityAsync(
        Guid businessId, string externalId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var newCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ExternalId = externalId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Customers.Add(newCustomer);

        Customer customer;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            customer = newCustomer;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: CustomerExternalIdUniqueIndexName })
        {
            db.Entry(newCustomer).State = EntityState.Detached;

            var existingCustomer = await db.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.BusinessId == businessId && c.ExternalId == externalId, cancellationToken);

            if (existingCustomer is null)
            {
                // The row that caused the 23505 is gone by the time we
                // re-read — an ambiguous state this service must never
                // silently paper over.
                throw;
            }

            customer = existingCustomer;
        }

        var newIdentity = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customer.Id,
            Channel = ConversationChannels.Website,
            VerificationMethod = IdentityVerificationMethods.SignedToken,
            ExternalUserId = externalId,
            LinkedAt = now,
            ExpiresAt = now.AddHours(options.Value.GrantDurationHours),
        };
        db.ExternalIdentities.Add(newIdentity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return customer.Id;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: ExternalIdentityUniqueIndexName })
        {
            db.Entry(newIdentity).State = EntityState.Detached;

            var existingIdentity = await db.ExternalIdentities
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    ei => ei.BusinessId == businessId && ei.Channel == ConversationChannels.Website
                        && ei.ExternalUserId == externalId && ei.RevokedAt == null,
                    cancellationToken);

            if (existingIdentity is null)
            {
                throw;
            }

            if (existingIdentity.CustomerId != customer.Id)
            {
                // §5D states 4/7 — a data-integrity anomaly (e.g. a prior
                // manual staff LinkIdentity action assigned this exact
                // (Channel, ExternalUserId) to a different Customer). Fail
                // closed: never silently reassign ownership.
                logger.LogError(
                    "ExternalIdentity {Id} for (BusinessId {BusinessId}, ExternalUserId {ExternalUserId}) belongs " +
                    "to Customer {ExistingCustomerId}, not the resolved Customer {ResolvedCustomerId}.",
                    existingIdentity.Id, businessId, externalId, existingIdentity.CustomerId, customer.Id);
                return null;
            }

            // Refresh — deliberately unconditional on the row's current
            // ExpiresAt (an already-expired-but-not-revoked row is still
            // eligible, §5D state 6). RevokedAt == null is the only
            // precondition: if the row was revoked between the reload and
            // this update, the conditional update affects zero rows.
            var refreshed = await db.ExternalIdentities
                .IgnoreQueryFilters()
                .Where(ei => ei.Id == existingIdentity.Id && ei.RevokedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(ei => ei.ExpiresAt, now.AddHours(options.Value.GrantDurationHours)),
                    cancellationToken);

            return refreshed == 0 ? null : customer.Id;
        }
    }

    private static SignedIdentityTokenResult Rejected() => new(SignedIdentityTokenOutcome.Rejected, null);
}
