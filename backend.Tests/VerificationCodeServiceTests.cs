using System.Security.Cryptography;
using System.Text;
using backend.Constants;
using backend.Data;
using backend.Email;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for VerificationCodeService — real Postgres, bare
/// ServiceProvider, same pattern as ConversationModeServiceTests/
/// AuditLogServiceTests. FakeEmailSender substitutes for the real
/// Resend-backed IEmailSender (never a real, billed send from these tests).
/// </summary>
public class VerificationCodeServiceTests : IAsyncLifetime
{
    private const string TestHashingPepper = "test-only-pepper-not-a-real-secret";

    private ServiceProvider _provider = null!;
    private FakeEmailSender _emailSender = null!;
    private string _adoConnectionString = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        _emailSender = new FakeEmailSender();
        // Step 21 forced-interleaving test needs a raw, independent
        // connection (not routed through the app's own DI-managed
        // AppDbContext) to act as a deterministic advisory-lock barrier —
        // same technique ConversationsApiTests.cs/WidgetConversationServiceTests.cs
        // already established for the notification-concurrency races.
        _adoConnectionString = PostgresConnectionString.FromUri(connectionUri);

        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddSingleton<IEmailSender>(_emailSender)
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddSingleton(Options.Create(new VerificationOptions
            {
                CodeLength = 6,
                CodeExpiryMinutes = 5,
                Level2VerifiedDurationMinutes = 30,
                ResendCooldownSeconds = 60,
                MaxResendsPerHour = 5,
                HashingPepper = TestHashingPepper,
            }))
            .AddScoped<IVerificationCodeService, VerificationCodeService>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VerificationCodes\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    private async Task<Guid> CreateBusinessAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Verification Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task<Guid> CreateWidgetAsync(AppDbContext db, Guid businessId)
    {
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        return widget.Id;
    }

    private static async Task<Conversation> CreateConversationAsync(AppDbContext db, Guid businessId, Guid widgetId)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            Channel = "Website",
            Status = ConversationStatus.Open,
            HandledBy = HandledBy.Ai,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private static async Task<Customer> CreateCustomerAsync(AppDbContext db, Guid businessId, string? email)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = email,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private async Task<(Guid BusinessId, Conversation Conversation)> SeedConversationAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = await CreateBusinessAsync(db);
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversation = await CreateConversationAsync(db, businessId, widgetId);
        return (businessId, conversation);
    }

    private static string ExtractCode(FakeEmailSender sender, string to)
    {
        var email = sender.SentEmails.Last(e => e.To == to);
        var start = email.HtmlBody.IndexOf("<strong>", StringComparison.Ordinal) + "<strong>".Length;
        var end = email.HtmlBody.IndexOf("</strong>", StringComparison.Ordinal);
        return email.HtmlBody[start..end];
    }

    // ── Customer resolution ──────────────────────────────────────────

    [Fact]
    public async Task RequestCodeAsync_ZeroMatches_AutoCreatesCustomerAndIssuesCode()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"new-{Guid.NewGuid():N}@example.com";

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);
        var customer = await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessId && c.Email == email);
        Assert.Null(customer.ExternalId);
        Assert.Null(customer.Name);
        Assert.Single(_emailSender.SentEmails, e => e.To == email);
    }

    [Fact]
    public async Task RequestCodeAsync_ExactlyOneMatch_UsesExistingCustomer()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var email = $"existing-{Guid.NewGuid():N}@example.com";
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await CreateCustomerAsync(seedDb, businessId, email);
        }

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);
        var customerCount = await db.Customers.IgnoreQueryFilters().CountAsync(c => c.BusinessId == businessId && c.Email == email);
        Assert.Equal(1, customerCount);
    }

    [Fact]
    public async Task RequestCodeAsync_MultipleMatches_FailsSafelyWithNoCodeCreated()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await CreateCustomerAsync(seedDb, businessId, email);
            await CreateCustomerAsync(seedDb, businessId, email);
        }

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.AmbiguousCustomerMatch, result.Outcome);
        var codeCount = await db.VerificationCodes.IgnoreQueryFilters().CountAsync(v => v.BusinessId == businessId);
        Assert.Equal(0, codeCount);
        Assert.DoesNotContain(_emailSender.SentEmails, e => e.To == email);
    }

    [Fact]
    public async Task RequestCodeAsync_SameEmailInAnotherBusiness_NeverResolvesAcrossTenants()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var email = $"cross-tenant-{Guid.NewGuid():N}@example.com";

        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var otherBusinessId = await CreateBusinessAsync(seedDb);
            await CreateCustomerAsync(seedDb, otherBusinessId, email);
        }

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        // A brand-new Customer must be auto-created in THIS business —
        // the other business's same-email Customer must never be resolved.
        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);
        var customersInThisBusiness = await db.Customers.IgnoreQueryFilters().CountAsync(c => c.BusinessId == businessId && c.Email == email);
        Assert.Equal(1, customersInThisBusiness);
    }

    [Fact]
    public async Task RequestCodeAsync_EmailNormalization_TrimAndCaseInsensitiveMatchExistingCustomer()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var normalizedEmail = $"norm-{Guid.NewGuid():N}@example.com";
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await CreateCustomerAsync(seedDb, businessId, normalizedEmail);
        }

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mixedCaseWithWhitespace = $"  {normalizedEmail.ToUpperInvariant()}  ";
        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, mixedCaseWithWhitespace, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);
        var customerCount = await db.Customers.IgnoreQueryFilters().CountAsync(c => c.BusinessId == businessId && c.Email == normalizedEmail);
        Assert.Equal(1, customerCount);
    }

    // ── VerificationCode issuance / verification ─────────────────────

    [Fact]
    public async Task RequestCodeAsync_SentEmail_ContainsSixDigitNumericCode()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"code-format-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        var code = ExtractCode(_emailSender, email);
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    // ── Detached email dispatch (Finding A remediation) ───────────────

    private ServiceProvider BuildProvider(IEmailSender emailSender, ILogger<VerificationCodeService>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();
        var connectionUri = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not found via user-secrets.");

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddSingleton(emailSender)
            .AddScoped<IAuditLogService, AuditLogService>()
            .AddSingleton(Options.Create(new VerificationOptions
            {
                CodeLength = 6,
                CodeExpiryMinutes = 5,
                Level2VerifiedDurationMinutes = 30,
                ResendCooldownSeconds = 60,
                MaxResendsPerHour = 5,
                HashingPepper = TestHashingPepper,
            }))
            .AddScoped<IVerificationCodeService, VerificationCodeService>();

        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {description}");
            }
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Proves RequestCodeAsync does not await email delivery — not by
    /// timing, but structurally: BlockingEmailSender never completes until
    /// explicitly released, so if the implementation regressed to awaiting
    /// the send synchronously, the WaitAsync below would time out and fail
    /// loudly rather than the test silently passing on a race.
    /// </summary>
    [Fact]
    public async Task RequestCodeAsync_DoesNotAwaitEmailDelivery()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var blockingSender = new BlockingEmailSender();
        await using var provider = BuildProvider(blockingSender);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"no-await-email-{Guid.NewGuid():N}@example.com";

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);

        // The detached dispatch genuinely got scheduled and started —
        // proven deterministically via a real signal, not assumed.
        await blockingSender.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        // ...but has not completed, since the request above already
        // returned while the send is still blocked here.
        Assert.Empty(blockingSender.SentEmails);

        blockingSender.Release();
        await WaitUntilAsync(() => blockingSender.SentEmails.Count == 1, TimeSpan.FromSeconds(5), "detached email to finish sending");
        Assert.Single(blockingSender.SentEmails);
        Assert.Equal(email, blockingSender.SentEmails[0].To);
    }

    /// <summary>
    /// Proves a detached dispatch failure is caught and logged, not left to
    /// become an unobserved task exception — the entire operation's
    /// try/catch, not merely the SendEmailAsync call, is what's under test
    /// here (ThrowingEmailSender throws from inside SendEmailAsync itself,
    /// exercising the innermost point the catch must still cover).
    /// </summary>
    [Fact]
    public async Task RequestCodeAsync_DetachedEmailFailure_IsCaughtAndLogged()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var throwingSender = new ThrowingEmailSender();
        var capturingLogger = new CapturingLogger<VerificationCodeService>();
        await using var provider = BuildProvider(throwingSender, capturingLogger);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"detached-failure-{Guid.NewGuid():N}@example.com";

        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The HTTP-facing outcome is unaffected by the later delivery
        // failure — it already committed before dispatch was even attempted.
        Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);

        await throwingSender.Called.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => capturingLogger.Entries.Count > 0, TimeSpan.FromSeconds(5), "detached dispatch failure to be logged");

        Assert.Contains(capturingLogger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    /// <summary>
    /// Lifetime-safety proof: disposes the ORIGINAL request scope (and its
    /// AppDbContext) immediately after RequestCodeAsync returns, then
    /// proves the detached dispatch still completes successfully — it
    /// cannot be depending on anything tied to the now-disposed original
    /// scope, since that scope is already gone by the time the send
    /// actually proceeds.
    /// </summary>
    [Fact]
    public async Task RequestCodeAsync_DetachedDispatch_SurvivesOriginalScopeDisposal()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var blockingSender = new BlockingEmailSender();
        await using var provider = BuildProvider(blockingSender);
        var email = $"scope-survives-{Guid.NewGuid():N}@example.com";

        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);
        } // The request's own scope, including its AppDbContext, is disposed here.

        await blockingSender.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        blockingSender.Release();
        await WaitUntilAsync(
            () => blockingSender.SentEmails.Count == 1, TimeSpan.FromSeconds(5),
            "detached email to finish sending after the original scope was disposed");
        Assert.Single(blockingSender.SentEmails);
    }

    [Fact]
    public async Task VerifyCodeAsync_CorrectCode_SucceedsAndAttachesCustomerAndLevel2VerifiedUntil()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"verify-ok-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        var beforeVerify = DateTimeOffset.UtcNow;
        var result = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(VerificationFailureReason.None, result.Reason);

        var updated = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.NotNull(updated.CustomerId);
        Assert.NotNull(updated.Level2VerifiedUntil);
        var expectedUntil = beforeVerify.AddMinutes(30);
        Assert.InRange(updated.Level2VerifiedUntil!.Value, expectedUntil.AddSeconds(-30), expectedUntil.AddSeconds(30));
    }

    [Fact]
    public async Task VerifyCodeAsync_WrongCode_FailsAndDoesNotAttachCustomerId()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"verify-wrong-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var realCode = ExtractCode(_emailSender, email);
        var wrongCode = realCode == "000000" ? "111111" : "000000";

        var result = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, wrongCode, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(VerificationFailureReason.CodeMismatch, result.Reason);

        var updated = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Null(updated.CustomerId);
        Assert.Null(updated.Level2VerifiedUntil);
    }

    [Fact]
    public async Task VerifyCodeAsync_SameCodeSubmittedTwice_SecondAttemptFailsAsAlreadyUsed()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"replay-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        var first = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);
        var second = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(VerificationFailureReason.NotFound, second.Reason);
    }

    [Fact]
    public async Task VerifyCodeAsync_AttemptExhaustion_RejectsEvenTheCorrectCode()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"exhaust-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);
        var wrongCode = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var attempt = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, wrongCode, CancellationToken.None);
            Assert.Equal(VerificationFailureReason.CodeMismatch, attempt.Reason);
        }

        var finalAttempt = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);

        Assert.False(finalAttempt.Success);
        Assert.Equal(VerificationFailureReason.AttemptsExhausted, finalAttempt.Reason);
    }

    [Fact]
    public async Task VerifyCodeAsync_ExpiredCode_Fails()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"expired-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        // Force expiry directly — VerificationCodeService itself has no
        // caller-facing way to backdate ExpiresAt.
        await db.VerificationCodes.IgnoreQueryFilters()
            .Where(v => v.BusinessId == businessId && v.ConversationId == conversation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var result = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(VerificationFailureReason.Expired, result.Reason);
    }

    [Fact]
    public async Task RequestCodeAsync_Resend_SupersedesPreviousCodeSoOnlyLatestVerifies()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"supersede-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var firstCode = ExtractCode(_emailSender, email);

        // Bypass the resend cooldown directly (not under test here) so the
        // second RequestCodeAsync call actually issues a new code.
        await db.VerificationCodes.IgnoreQueryFilters()
            .Where(v => v.BusinessId == businessId && v.ConversationId == conversation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-2)));

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var secondCode = ExtractCode(_emailSender, email);

        var activeCount = await db.VerificationCodes.IgnoreQueryFilters()
            .CountAsync(v => v.BusinessId == businessId && v.ConversationId == conversation.Id && v.SupersededAt == null);
        Assert.Equal(1, activeCount);

        var oldCodeResult = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, firstCode, CancellationToken.None);
        Assert.False(oldCodeResult.Success);

        var newCodeResult = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, secondCode, CancellationToken.None);
        Assert.True(newCodeResult.Success);
    }

    [Fact]
    public async Task RequestCodeAsync_WithinCooldown_ReturnsResendCooldownActiveAndDoesNotIssueNewCode()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"cooldown-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.ResendCooldownActive, result.Outcome);
        var codeCount = await db.VerificationCodes.IgnoreQueryFilters().CountAsync(v => v.BusinessId == businessId && v.ConversationId == conversation.Id);
        Assert.Equal(1, codeCount);
    }

    [Fact]
    public async Task RequestCodeAsync_CooldownCannotBeBypassedByFreshVisitorSession()
    {
        // VerificationCodeService's resend cooldown is keyed by
        // (BusinessId, CustomerId, ConversationId, Channel) — not by
        // VisitorSessionId at all, so there is nothing for a fresh
        // VisitorSessionId to bypass. This test proves the cooldown holds
        // purely from the service's own scope key, independent of any
        // widget-session identity, mirroring the design doc's explicit
        // requirement (Decision 24's refinement).
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"no-bypass-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.ResendCooldownActive, result.Outcome);
    }

    [Fact]
    public async Task RequestCodeAsync_SixResendsWithinHour_SixthReturnsResendLimitExceeded()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"limit-{Guid.NewGuid():N}@example.com";

        for (var i = 0; i < 5; i++)
        {
            var result = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
            Assert.Equal(RequestCodeOutcome.Issued, result.Outcome);

            // Backdate CreatedAt past the cooldown so the next call isn't
            // rejected by ResendCooldownActive first — isolates the
            // per-hour limit from the cooldown, which is covered above.
            await db.VerificationCodes.IgnoreQueryFilters()
                .Where(v => v.BusinessId == businessId && v.ConversationId == conversation.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-2)));
        }

        var sixth = await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        Assert.Equal(RequestCodeOutcome.ResendLimitExceeded, sixth.Outcome);
        var codeCount = await db.VerificationCodes.IgnoreQueryFilters().CountAsync(v => v.BusinessId == businessId && v.ConversationId == conversation.Id);
        Assert.Equal(5, codeCount);
    }

    [Fact]
    public async Task VerifyCodeAsync_ConcurrentAttemptsWithSameCorrectCode_ExactlyOneSucceeds()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var setupScope = _provider.CreateAsyncScope();
        var setupService = setupScope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"race-{Guid.NewGuid():N}@example.com";

        await setupService.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            return await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Success);
    }

    // ── Conversation-scoped isolation ─────────────────────────────────

    /// <summary>
    /// Design doc adversarial case: "A code issued for one conversation
    /// never verifies a different conversation, even for the same
    /// customer." Both conversations share a Business, and Customer
    /// resolution by email is Business-scoped (not Conversation-scoped) —
    /// so B genuinely resolves the SAME Customer A's code was issued to.
    /// The only thing that can make B's attempt fail is ConversationId
    /// being part of the VerificationCode lookup scope itself, not some
    /// other check failing first. Re-verifying through A afterward proves
    /// the code was never touched (not consumed, not attempt-incremented)
    /// by the failed cross-conversation attempt.
    /// </summary>
    [Fact]
    public async Task VerifyCodeAsync_CodeIssuedForConversationA_NeverVerifiesAgainstConversationB()
    {
        var (businessId, conversationA) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var widgetId = await CreateWidgetAsync(db, businessId);
        var conversationB = await CreateConversationAsync(db, businessId, widgetId);
        var email = $"same-customer-diff-conversation-{Guid.NewGuid():N}@example.com";

        var requestResult = await service.RequestCodeAsync(businessId, conversationA.Id, conversationA.Channel, email, CancellationToken.None);
        Assert.Equal(RequestCodeOutcome.Issued, requestResult.Outcome);
        var code = ExtractCode(_emailSender, email);

        var verifyViaB = await service.VerifyCodeAsync(businessId, conversationB.Id, conversationB.Channel, email, code, CancellationToken.None);

        Assert.False(verifyViaB.Success);
        Assert.Equal(VerificationFailureReason.NotFound, verifyViaB.Reason);

        var updatedB = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationB.Id);
        Assert.Null(updatedB.CustomerId);
        Assert.Null(updatedB.Level2VerifiedUntil);

        var updatedA = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationA.Id);
        Assert.Null(updatedA.CustomerId);
        Assert.Null(updatedA.Level2VerifiedUntil);

        var codeRow = await db.VerificationCodes.IgnoreQueryFilters()
            .SingleAsync(v => v.BusinessId == businessId && v.ConversationId == conversationA.Id);
        Assert.False(codeRow.Used);
        Assert.Equal(0, codeRow.AttemptCount);
        Assert.Null(codeRow.SupersededAt);

        // The code is still fully valid — B's failed attempt neither
        // consumed it nor spent an attempt against it — and only becomes
        // usable when presented through the conversation it was actually
        // issued for.
        var verifyViaA = await service.VerifyCodeAsync(businessId, conversationA.Id, conversationA.Channel, email, code, CancellationToken.None);
        Assert.True(verifyViaA.Success);
    }

    // ── Tenant isolation ─────────────────────────────────────────────

    [Fact]
    public async Task VerifyCodeAsync_CodeFromDifferentBusiness_NeverVerifies()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var (otherBusinessId, otherConversation) = await SeedConversationAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var email = $"cross-tenant-verify-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(otherBusinessId, otherConversation.Id, otherConversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        // Same email, same code, but presented against the WRONG business's
        // conversation — Customer.Email uniqueness is per-business, so this
        // legitimately auto-creates a distinct Customer in businessId and
        // then correctly fails to find any code for it.
        var result = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(VerificationFailureReason.NotFound, result.Reason);
    }

    // ── Audit behavior ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyCodeAsync_Failure_WritesAuditLogWithoutRawCodeOrEmail()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"audit-fail-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var realCode = ExtractCode(_emailSender, email);
        var wrongCode = realCode == "000000" ? "111111" : "000000";

        await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, wrongCode, CancellationToken.None);

        var auditLogs = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.BusinessId == businessId && a.EventType == AuditEventTypes.Level2VerificationFailed)
            .ToListAsync();

        Assert.Single(auditLogs);
        Assert.Equal(ActorType.Customer, auditLogs[0].Actor);
        Assert.DoesNotContain(wrongCode, auditLogs[0].NewValue ?? "");
        Assert.DoesNotContain(email, auditLogs[0].OldValue + (auditLogs[0].NewValue ?? ""));
    }

    [Fact]
    public async Task VerifyCodeAsync_Success_DoesNotWriteRoutineSuccessAuditLog()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"audit-success-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        var code = ExtractCode(_emailSender, email);

        var result = await service.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, code, CancellationToken.None);
        Assert.True(result.Success);

        var auditLogCount = await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.BusinessId == businessId);
        Assert.Equal(0, auditLogCount);
    }

    // ── Conversation state ───────────────────────────────────────────

    [Fact]
    public async Task RequestCodeAsync_DoesNotAttachConversationCustomerId()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"no-attach-{Guid.NewGuid():N}@example.com";

        await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);

        var updated = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Null(updated.CustomerId);
        Assert.Null(updated.Level2VerifiedUntil);
    }

    // ── Concurrency (forced advisory-lock interleaving) ───────────────

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

    /// <summary>
    /// Deterministic wait-for-condition, not a fixed sleep — same
    /// established convention as ConversationsApiTests.cs. Fails loudly if
    /// the expected number of real, distinct backends never becomes
    /// provably blocked on the exact advisory-lock key within the timeout.
    /// </summary>
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

    /// <summary>Opens the barrier connection and pre-acquires the given verification scope's advisory lock, holding it open until the caller disposes/rolls back the returned transaction.</summary>
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

    private static string ComputeTestCodeHash(string rawCode)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestHashingPepper), Encoding.UTF8.GetBytes(rawCode));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Design doc's "the forced-pg_locks-interleaving convention already
    /// established for HandledBy/notification races, for the
    /// resend-supersession concurrency proof" — reused verbatim (barrier
    /// connection + pg_locks waiter count + deterministic release), not a
    /// Task.WhenAll-based approximation.
    ///
    /// A pre-existing, cooldown-expired code is seeded first — with the
    /// 60-second scope-keyed resend cooldown enforced INSIDE the same
    /// advisory-lock-guarded transaction, two truly-simultaneous
    /// RequestCodeAsync calls starting from an EMPTY scope can never both
    /// reach the supersede step (whichever acquires the lock second always
    /// observes the first caller's just-committed row and is cooldown-
    /// rejected before ever superseding anything) — that's a real,
    /// correct, but different guarantee than the one under test here.
    /// Seeding an already-stale prior code lets the FIRST lock-holder
    /// legitimately pass its cooldown check and exercise the actual
    /// supersede-then-insert step, while the SECOND lock-holder still
    /// correctly gets cooldown-rejected against the first holder's fresh
    /// insert — proving both the supersede atomicity AND that the lock
    /// prevents two simultaneously-active codes, in one race.
    /// </summary>
    [Fact]
    public async Task RequestCodeAsync_ConcurrentResendsForSameScope_ExactlyOneActiveCodeSurvives()
    {
        var (businessId, conversation) = await SeedConversationAsync();
        var email = $"forced-resend-race-{Guid.NewGuid():N}@example.com";
        Guid customerId;
        const string preSeededRawCode = "424242";

        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await CreateCustomerAsync(seedDb, businessId, email);
            customerId = customer.Id;

            seedDb.VerificationCodes.Add(new VerificationCode
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                CustomerId = customerId,
                ConversationId = conversation.Id,
                Channel = conversation.Channel,
                CodeHash = ComputeTestCodeHash(preSeededRawCode),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2), // past the 60s cooldown
            });
            await seedDb.SaveChangesAsync();
        }

        var lockKey = AdvisoryLockKeyGenerator.ForVerificationCode(businessId, customerId, conversation.Id, conversation.Channel);
        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var task1 = Task.Run(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            return await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        });
        var task2 = Task.Run(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            return await service.RequestCodeAsync(businessId, conversation.Id, conversation.Channel, email, CancellationToken.None);
        });

        // Deterministic proof of genuine contention — both real callers are
        // provably blocked on this exact advisory lock key before we ever
        // let either proceed.
        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "resend-supersession");
        await barrierTransaction.RollbackAsync();

        var results = await Task.WhenAll(task1, task2);

        // Exactly one caller actually creates a code — the other is
        // cooldown-rejected against whichever committed first. Both are
        // legitimate, deterministic outcomes of the same lock-serialized
        // race; neither can silently create a second simultaneously-active
        // code.
        Assert.Single(results, r => r.Outcome == RequestCodeOutcome.Issued);
        Assert.Single(results, r => r.Outcome == RequestCodeOutcome.ResendCooldownActive);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var allCodes = await verifyDb.VerificationCodes.IgnoreQueryFilters()
            .Where(v => v.BusinessId == businessId && v.CustomerId == customerId
                && v.ConversationId == conversation.Id && v.Channel == conversation.Channel)
            .ToListAsync();

        // The pre-seeded code plus exactly one newly-inserted code — never
        // two simultaneously-active rows.
        Assert.Equal(2, allCodes.Count);
        var activeCodes = allCodes.Where(v => v.SupersededAt == null).ToList();
        Assert.Single(activeCodes);

        // Identify the superseded row by its known, pre-seeded hash — this
        // is the pre-seeded row specifically, not merely "whichever of the
        // two rows happens to be superseded."
        var supersededCode = allCodes.Single(v => v.CodeHash == ComputeTestCodeHash(preSeededRawCode));
        Assert.NotNull(supersededCode.SupersededAt);

        // The surviving active row is the newest by CreatedAt — the
        // pre-seeded row is provably the one that got superseded, not the
        // other way around.
        var activeCode = activeCodes[0];
        Assert.True(activeCode.CreatedAt > supersededCode.CreatedAt);
        Assert.NotEqual(supersededCode.Id, activeCode.Id);

        // The superseded (pre-seeded) code can no longer verify. VerifyCodeAsync
        // only ever looks up the CURRENT active row for the scope and
        // compares the submitted value against THAT row's hash — it never
        // searches historical/superseded rows for a match — so submitting
        // the superseded code's raw value is checked against the (different)
        // active row's hash and correctly reported as CodeMismatch, not
        // Superseded (Superseded is only reachable via the race-timing path:
        // a code that WAS the active row at read time but got superseded by
        // a concurrent resend before the atomic consume executed). Either
        // way, the required property holds: the superseded code cannot
        // verify.
        await using (var supersededVerifyScope = _provider.CreateAsyncScope())
        {
            var supersededService = supersededVerifyScope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            var supersededVerify = await supersededService.VerifyCodeAsync(
                businessId, conversation.Id, conversation.Channel, email, preSeededRawCode, CancellationToken.None);
            Assert.False(supersededVerify.Success);
            Assert.Equal(VerificationFailureReason.CodeMismatch, supersededVerify.Reason);
        }

        // The single winning caller's freshly-issued code (the only email
        // actually sent during the whole race) still verifies successfully.
        var freshCode = ExtractCode(_emailSender, email);
        await using var finalVerifyScope = _provider.CreateAsyncScope();
        var finalService = finalVerifyScope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
        var freshVerify = await finalService.VerifyCodeAsync(businessId, conversation.Id, conversation.Channel, email, freshCode, CancellationToken.None);
        Assert.True(freshVerify.Success);
    }

    /// <summary>
    /// Same forced-pg_locks-interleaving convention as the resend-
    /// supersession proof above, reused verbatim (barrier connection +
    /// pg_locks waiter count + deterministic release) — not a bare
    /// Task.WhenAll approximation. Proves the duplicate-Customer race an
    /// independent audit found in ResolveCustomerAsync's zero-match
    /// auto-create branch (two concurrent RequestCodeAsync calls for the
    /// same brand-new email both observing zero matches and both creating
    /// a Customer) is now closed by AdvisoryLockKeyGenerator.ForCustomerEmailResolution,
    /// not by a (BusinessId, Email) unique-index/DB-constraint — see that
    /// method's own doc comment for why a DB constraint would be the wrong
    /// fix here (it would outlaw the legitimate, already-tested
    /// Ambiguous-match scenario). Two different conversations in the same
    /// business are used so the later, per-(customer,conversation,channel)
    /// verification-code lock never contends between the two callers —
    /// this test isolates the customer-resolution race specifically.
    /// </summary>
    [Fact]
    public async Task RequestCodeAsync_ConcurrentZeroMatchRequestsForSameNewEmail_CreatesExactlyOneCustomer()
    {
        var (businessId, conversationA) = await SeedConversationAsync();
        Conversation conversationB;
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var widgetId = await CreateWidgetAsync(seedDb, businessId);
            conversationB = await CreateConversationAsync(seedDb, businessId, widgetId);
        }

        var email = $"race-new-customer-{Guid.NewGuid():N}@example.com";
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var lockKey = AdvisoryLockKeyGenerator.ForCustomerEmailResolution(businessId, normalizedEmail);

        var (barrierConnection, barrierTransaction) = await AcquireBarrierAsync(lockKey);
        await using var _bc = barrierConnection;
        await using var _bt = barrierTransaction;

        var task1 = Task.Run(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            return await service.RequestCodeAsync(businessId, conversationA.Id, conversationA.Channel, email, CancellationToken.None);
        });
        var task2 = Task.Run(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IVerificationCodeService>();
            return await service.RequestCodeAsync(businessId, conversationB.Id, conversationB.Channel, email, CancellationToken.None);
        });

        // Deterministic proof of genuine contention — both real callers
        // (each on its own scope/DbContext/connection) are provably
        // blocked on this exact advisory lock key before we ever let
        // either proceed.
        await WaitForAdvisoryLockWaitersAsync(barrierConnection, lockKey, 2, "customer-email-resolution");
        await barrierTransaction.RollbackAsync();

        var results = await Task.WhenAll(task1, task2);

        // Both requests succeed — the second (lock-serialized) caller
        // recovers by resolving to the first's already-committed Customer
        // row, never creating a second identity and never surfacing an
        // unhandled exception/500.
        Assert.All(results, r => Assert.Equal(RequestCodeOutcome.Issued, r.Outcome));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customers = await verifyDb.Customers.IgnoreQueryFilters()
            .Where(c => c.BusinessId == businessId && c.Email == normalizedEmail)
            .ToListAsync();
        Assert.Single(customers);

        // Both conversations' issued codes resolved to the SAME Customer
        // identity, not two different ones — checked via VerificationCode
        // rather than Conversation.CustomerId, since RequestCodeAsync alone
        // never attaches Conversation.CustomerId (see
        // RequestCodeAsync_DoesNotAttachConversationCustomerId).
        var codeCustomerIds = await verifyDb.VerificationCodes.IgnoreQueryFilters()
            .Where(v => v.BusinessId == businessId
                && (v.ConversationId == conversationA.Id || v.ConversationId == conversationB.Id))
            .Select(v => v.CustomerId)
            .Distinct()
            .ToListAsync();
        Assert.Single(codeCustomerIds);
        Assert.Equal(customers[0].Id, codeCustomerIds[0]);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
