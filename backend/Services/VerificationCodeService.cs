using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using backend.Constants;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace backend.Services;

/// <summary>
/// IgnoreQueryFilters() + explicit BusinessId throughout, matching
/// WidgetConversationService/ConversationModeService's defensive posture —
/// this service has no HTTP-request-scoped caller guaranteeing the right
/// tenant context on its own (the widget controller resolves/cross-checks
/// the conversation against the JWT's own claims before ever calling in
/// here, but this service does not rely on that having happened correctly).
///
/// Audit logging is deliberately failure-focused, not routine-success
/// logging — Level2VerificationSucceeded is never written by this service.
/// Only genuine failure/ambiguity events are audited (ambiguous customer
/// match at request time; NotFound/Expired/AlreadyUsed/Superseded/
/// AttemptsExhausted/CodeMismatch at verify time), and never with the raw
/// code or raw email in the payload.
/// </summary>
public class VerificationCodeService(
    AppDbContext db,
    IAuditLogService auditLogService,
    IOptions<VerificationOptions> options,
    IServiceProvider serviceProvider,
    ILogger<VerificationCodeService> logger) : IVerificationCodeService
{
    private enum CustomerMatchOutcome
    {
        SingleMatch,
        ZeroMatches,
        Ambiguous,
    }

    public async Task<RequestCodeResult> RequestCodeAsync(
        Guid businessId, Guid conversationId, string channel, string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);

        var (matchOutcome, customer) = await ResolveCustomerAsync(
            businessId, normalizedEmail, autoCreateIfMissing: true, cancellationToken);

        if (matchOutcome == CustomerMatchOutcome.Ambiguous)
        {
            await auditLogService.LogAsync(
                businessId, AuditEntityTypes.Conversation, conversationId.ToString(),
                AuditEventTypes.Level2VerificationFailed, ActorType.Customer,
                newValue: "AmbiguousCustomerMatch", conversationId: conversationId,
                cancellationToken: cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return new RequestCodeResult(RequestCodeOutcome.AmbiguousCustomerMatch);
        }

        var customerId = customer!.Id;
        var now = DateTimeOffset.UtcNow;
        var lockKey = AdvisoryLockKeyGenerator.ForVerificationCode(businessId, customerId, conversationId, channel);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

            var lastCreatedAt = await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.CustomerId == customerId
                    && v.ConversationId == conversationId && v.Channel == channel)
                .OrderByDescending(v => v.CreatedAt)
                .Select(v => (DateTimeOffset?)v.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (lastCreatedAt is { } last && now - last < TimeSpan.FromSeconds(options.Value.ResendCooldownSeconds))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new RequestCodeResult(RequestCodeOutcome.ResendCooldownActive);
            }

            var windowStart = now - TimeSpan.FromHours(1);
            var recentCount = await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.CustomerId == customerId
                    && v.ConversationId == conversationId && v.Channel == channel
                    && v.CreatedAt > windowStart)
                .CountAsync(cancellationToken);

            if (recentCount >= options.Value.MaxResendsPerHour)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new RequestCodeResult(RequestCodeOutcome.ResendLimitExceeded);
            }

            // Atomic supersede — VerificationCode's own doc comment mandates
            // this happen under the advisory lock, not check-then-insert.
            await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.CustomerId == customerId
                    && v.ConversationId == conversationId && v.Channel == channel
                    && !v.Used && v.SupersededAt == null && v.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.SupersededAt, now), cancellationToken);

            var rawCode = GenerateNumericCode(options.Value.CodeLength);

            db.VerificationCodes.Add(new VerificationCode
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                CustomerId = customerId,
                ConversationId = conversationId,
                Channel = channel,
                CodeHash = ComputeCodeHash(rawCode),
                ExpiresAt = now.AddMinutes(options.Value.CodeExpiryMinutes),
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Deliberately detached, not awaited — Step 21 Finding A. Removes
            // the dominant outbound-network-call asymmetry that let a caller
            // distinguish this branch from AmbiguousCustomerMatch's fast
            // return via response timing, closing the enumeration-resistance
            // gap Decision 21 requires. See DispatchVerificationEmail's own
            // doc comment for the accepted best-effort/no-retry trade-off.
            // customer.Email (the stored, already-normalized-on-write value),
            // not the raw request input, is the delivery address.
            DispatchVerificationEmail(customer.Email!, rawCode, options.Value.CodeExpiryMinutes);

            return new RequestCodeResult(RequestCodeOutcome.Issued);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<VerificationResult> VerifyCodeAsync(
        Guid businessId, Guid conversationId, string channel, string email, string submittedCode,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);

        var (matchOutcome, customer) = await ResolveCustomerAsync(
            businessId, normalizedEmail, autoCreateIfMissing: false, cancellationToken);

        if (matchOutcome != CustomerMatchOutcome.SingleMatch)
        {
            // Zero and ambiguous matches both fail closed identically here
            // — verification never auto-creates a Customer, and never
            // guesses among duplicates.
            return await FailAsync(businessId, conversationId, VerificationFailureReason.NotFound, cancellationToken);
        }

        var customerId = customer!.Id;
        var now = DateTimeOffset.UtcNow;

        var code = await db.VerificationCodes
            .IgnoreQueryFilters()
            .Where(v => v.BusinessId == businessId && v.CustomerId == customerId
                && v.ConversationId == conversationId && v.Channel == channel
                && !v.Used && v.SupersededAt == null)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (code is null)
        {
            return await FailAsync(businessId, conversationId, VerificationFailureReason.NotFound, cancellationToken);
        }

        if (code.ExpiresAt <= now)
        {
            return await FailAsync(businessId, conversationId, VerificationFailureReason.Expired, cancellationToken);
        }

        if (code.AttemptCount >= code.MaxAttempts)
        {
            return await FailAsync(businessId, conversationId, VerificationFailureReason.AttemptsExhausted, cancellationToken);
        }

        if (!ConstantTimeEquals(ComputeCodeHash(submittedCode), code.CodeHash))
        {
            // Atomic increment (SQL "AttemptCount + 1", not a client-side
            // read-modify-write) — concurrent wrong-code submissions
            // correctly accumulate, and the AttemptCount < MaxAttempts guard
            // keeps this from ever violating CK_VerificationCodes_Attempts.
            await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.Id == code.Id && !v.Used && v.SupersededAt == null && v.AttemptCount < v.MaxAttempts)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.AttemptCount, v => v.AttemptCount + 1), cancellationToken);

            return await FailAsync(businessId, conversationId, VerificationFailureReason.CodeMismatch, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Atomic consume — the WHERE clause is the entire authorization
            // check, same idiom as NotificationsController.MarkRead. Exactly
            // one concurrent verify attempt with the correct code can win.
            var affected = await db.VerificationCodes
                .IgnoreQueryFilters()
                .Where(v => v.Id == code.Id && !v.Used && v.SupersededAt == null
                    && v.AttemptCount < v.MaxAttempts && v.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Used, true), cancellationToken);

            if (affected == 0)
            {
                // Lost a race, or state changed since the reads above
                // (a concurrent resend superseded it, it expired, or attempts
                // were exhausted in between) — re-read to attribute the real
                // cause rather than guessing. Fails closed either way: no
                // branch below this point ever sets Used or touches
                // Conversation.
                await transaction.RollbackAsync(cancellationToken);

                var current = await db.VerificationCodes
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == code.Id, cancellationToken);

                var reason = current switch
                {
                    null => VerificationFailureReason.NotFound,
                    { Used: true } => VerificationFailureReason.AlreadyUsed,
                    { SupersededAt: not null } => VerificationFailureReason.Superseded,
                    { } c when c.ExpiresAt <= now => VerificationFailureReason.Expired,
                    { } c when c.AttemptCount >= c.MaxAttempts => VerificationFailureReason.AttemptsExhausted,
                    _ => VerificationFailureReason.AlreadyUsed,
                };

                return await FailAsync(businessId, conversationId, reason, cancellationToken);
            }

            var level2Until = now.AddMinutes(options.Value.Level2VerifiedDurationMinutes);

            // Decision 1 (identity-binding/erasure-integrity design;
            // implementation plan Phase 1) — conditional UPDATE (row-level CAS),
            // not the prior unconditional one: a Conversation already bound to a
            // different customer must reject this rebind rather than silently
            // being overwritten. Re-verification by the already-bound customer
            // (CustomerId == customerId) remains permitted, as does a first bind
            // (CustomerId == null). !IsAnonymized matches
            // WidgetController.ResolveOwnConversationAsync's existing
            // convention, not a new invariant.
            var conversationAffected = await db.Conversations
                .IgnoreQueryFilters()
                .Where(c => c.Id == conversationId && c.BusinessId == businessId && !c.IsAnonymized
                    && (c.CustomerId == null || c.CustomerId == customerId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.CustomerId, customerId)
                    .SetProperty(c => c.Level2VerifiedUntil, level2Until), cancellationToken);

            if (conversationAffected == 0)
            {
                // Lost the CAS — roll back the whole transaction, including the
                // code-consumption Used=true write above, so a rejected rebind
                // attempt never burns the submitter's own otherwise-valid code.
                // Re-read (AsNoTracking, IgnoreQueryFilters) to attribute the
                // real cause rather than guessing — same discipline as the
                // reattribution switch above.
                await transaction.RollbackAsync(cancellationToken);

                var currentConversation = await db.Conversations
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == conversationId && c.BusinessId == businessId, cancellationToken);

                var conversationReason = currentConversation switch
                {
                    null => VerificationFailureReason.NotFound,
                    { IsAnonymized: true } => VerificationFailureReason.NotFound,
                    _ => VerificationFailureReason.AlreadyBoundToDifferentCustomer,
                };

                return await FailAsync(businessId, conversationId, conversationReason, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new VerificationResult(true, VerificationFailureReason.None);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<VerificationResult> FailAsync(
        Guid businessId, Guid conversationId, VerificationFailureReason reason, CancellationToken cancellationToken)
    {
        await auditLogService.LogAsync(
            businessId, AuditEntityTypes.Conversation, conversationId.ToString(),
            AuditEventTypes.Level2VerificationFailed, ActorType.Customer,
            newValue: reason.ToString(), conversationId: conversationId,
            cancellationToken: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new VerificationResult(false, reason);
    }

    private async Task<(CustomerMatchOutcome Outcome, Customer? Customer)> ResolveCustomerAsync(
        Guid businessId, string normalizedEmail, bool autoCreateIfMissing, CancellationToken cancellationToken)
    {
        if (!autoCreateIfMissing)
        {
            // VerifyCodeAsync's call — never creates a Customer, so there is
            // no race to serialize against here.
            return ToOutcome(await MatchCustomersAsync(businessId, normalizedEmail, cancellationToken));
        }

        // RequestCodeAsync's call. Two concurrent requests for the same
        // not-yet-existing email could otherwise both observe zero matches
        // and both create a Customer — serialize the whole match+create
        // decision under an advisory lock keyed by (BusinessId,
        // normalizedEmail), re-reading matches fresh once the lock is held
        // rather than trusting a pre-lock read. Deliberately a lock, not a
        // (BusinessId, Email) unique-index/DB-constraint arbiter — see
        // AdvisoryLockKeyGenerator.ForCustomerEmailResolution's own doc
        // comment for why a DB constraint here would be wrong (it would
        // outlaw the legitimate, already-supported case of multiple
        // Customer rows sharing an email, which CustomerMatchOutcome.Ambiguous
        // exists specifically to handle safely).
        var lockKey = AdvisoryLockKeyGenerator.ForCustomerEmailResolution(businessId, normalizedEmail);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        var matches = await MatchCustomersAsync(businessId, normalizedEmail, cancellationToken);

        if (matches.Count != 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToOutcome(matches);
        }

        var now = DateTimeOffset.UtcNow;
        var newCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = normalizedEmail,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Customers.Add(newCustomer);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CustomerMatchOutcome.SingleMatch, newCustomer);
    }

    private async Task<List<Customer>> MatchCustomersAsync(
        Guid businessId, string normalizedEmail, CancellationToken cancellationToken) =>
        // c.Email != null both guards ToLower() and, as a side effect,
        // structurally excludes anonymized customers — Customer.cs's own
        // doc comment confirms Email is nulled on anonymization.
        await db.Customers
            .IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId && c.Email != null && c.Email.Trim().ToLower() == normalizedEmail)
            .ToListAsync(cancellationToken);

    private static (CustomerMatchOutcome, Customer?) ToOutcome(List<Customer> matches) => matches.Count switch
    {
        0 => (CustomerMatchOutcome.ZeroMatches, null),
        1 => (CustomerMatchOutcome.SingleMatch, matches[0]),
        _ => (CustomerMatchOutcome.Ambiguous, null),
    };

    /// <summary>
    /// Step 21 Finding A remediation. Fire-and-forget, deliberately detached
    /// from the HTTP request's own lifetime — resolves its own fresh DI
    /// scope (same pattern DataLifecycleBackgroundService already
    /// establishes for per-tick scoped work) rather than closing over this
    /// request's own scoped IEmailSender/AppDbContext, both of which are
    /// disposed once the request completes; this work is intentionally
    /// allowed to keep running after that.
    ///
    /// Best-effort only, by explicit architectural decision: no retry, no
    /// durable "still needs sending" record, and therefore no delivery
    /// guarantee across a process crash, restart, or deploy landing between
    /// the HTTP response and this task's completion — a detached Task.Run
    /// is not part of the host's tracked request-draining lifecycle and
    /// will not be reliably awaited during graceful shutdown either.
    /// Accepted specifically because a lost OTP email never grants
    /// authentication or Level 2 trust on its own — the customer's only
    /// recourse is requesting another code, a UX/support cost, not a
    /// security one. Do not reuse this pattern for delivery where losing
    /// the message has a security or correctness consequence, without a
    /// separate, explicit architectural decision.
    ///
    /// The try/catch wraps the entire operation, not just the send itself —
    /// scope creation and DI resolution can fail too, and nothing here may
    /// become an unobserved task exception. ResendEmailSender already logs
    /// its own delivery failures internally; this log call is a safety net
    /// for failures that never even reach it (e.g. scope/DI failures).
    ///
    /// Deliberately not Task.Run: this is I/O-bound work (an outbound HTTPS
    /// call), not CPU-bound, and ASP.NET Core's server request-handling
    /// threads have no SynchronizationContext to escape from — Task.Run
    /// would only add an extra thread-pool queuing hop (and its own
    /// scheduling latency under thread-pool pressure) before this method's
    /// own cheap, synchronous prefix (scope creation, DI resolution) even
    /// starts, for no benefit. Calling the async method directly and
    /// discarding its Task starts that prefix immediately on the calling
    /// thread and yields control back to the caller at the first real
    /// await inside SendEmailAsync — still fully detached from the
    /// request's own awaited execution path, just without the pointless
    /// extra hop.
    /// </summary>
    private void DispatchVerificationEmail(string toEmail, string rawCode, int codeExpiryMinutes)
    {
        _ = DispatchVerificationEmailAsync(toEmail, rawCode, codeExpiryMinutes);
    }

    private async Task DispatchVerificationEmailAsync(string toEmail, string rawCode, int codeExpiryMinutes)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var scopedEmailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            await scopedEmailSender.SendEmailAsync(
                toEmail,
                "Your Nivora verification code",
                $"<p>Your verification code is <strong>{rawCode}</strong>. It expires in {codeExpiryMinutes} minutes.</p>");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Detached OTP verification email dispatch to {Recipient} failed.", toEmail);
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string GenerateNumericCode(int length)
    {
        var max = (int)Math.Pow(10, length);
        var value = RandomNumberGenerator.GetInt32(0, max);
        return value.ToString(CultureInfo.InvariantCulture).PadLeft(length, '0');
    }

    private string ComputeCodeHash(string rawCode)
    {
        var keyBytes = Encoding.UTF8.GetBytes(options.Value.HashingPepper);
        var codeBytes = Encoding.UTF8.GetBytes(rawCode);
        var hash = HMACSHA256.HashData(keyBytes, codeBytes);
        return Convert.ToBase64String(hash);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        // CodeHash is always a fixed-length HMAC-SHA256 output — this
        // length short-circuit never leaks anything about the raw code.
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
