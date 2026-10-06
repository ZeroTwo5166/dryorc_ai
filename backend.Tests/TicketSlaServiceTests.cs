using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Service-level tests for TicketSlaService — same bare-ServiceProvider
/// pattern as ConversationModeServiceTests. No production caller exists for
/// this service or the surrounding ticket workflow yet (see its own doc
/// comment) — these tests exercise it directly.
/// </summary>
public class TicketSlaServiceTests : IAsyncLifetime
{
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
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
            .AddScoped<ITicketSlaService, TicketSlaService>()
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
            Name = $"SLA Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);
        return business.Id;
    }

    private static async Task SeedSlaTargetAsync(
        AppDbContext db, Guid businessId, TicketPriority priority, string? channel, int targetMinutes)
    {
        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Priority = priority,
            Channel = channel,
            TargetMinutes = targetMinutes,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ── ComputeSlaDeadlineAsync ──────────────────────────────────────

    [Fact]
    public async Task ComputeSlaDeadlineAsync_ExactPriorityChannelMatch_UsesItsTargetMinutes()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessId = await CreateBusinessAsync(db);
        await SeedSlaTargetAsync(db, businessId, TicketPriority.High, "Website", targetMinutes: 30);
        var createdAt = DateTimeOffset.UtcNow;

        var deadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.High, "Website", createdAt);

        Assert.Equal(createdAt.AddMinutes(30), deadline);
    }

    [Fact]
    public async Task ComputeSlaDeadlineAsync_NoExactMatch_FallsBackToChannelNullRule()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessId = await CreateBusinessAsync(db);
        await SeedSlaTargetAsync(db, businessId, TicketPriority.Urgent, channel: null, targetMinutes: 15);
        var createdAt = DateTimeOffset.UtcNow;

        var deadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.Urgent, "Email", createdAt);

        Assert.Equal(createdAt.AddMinutes(15), deadline);
    }

    [Fact]
    public async Task ComputeSlaDeadlineAsync_ExactMatch_PreferredOverFallback_WhenBothExist()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessId = await CreateBusinessAsync(db);
        await SeedSlaTargetAsync(db, businessId, TicketPriority.Medium, channel: null, targetMinutes: 120);
        await SeedSlaTargetAsync(db, businessId, TicketPriority.Medium, channel: "Website", targetMinutes: 45);
        var createdAt = DateTimeOffset.UtcNow;

        var deadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.Medium, "Website", createdAt);

        Assert.Equal(createdAt.AddMinutes(45), deadline);
    }

    [Fact]
    public async Task ComputeSlaDeadlineAsync_NoExactNoFallback_ReturnsNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessId = await CreateBusinessAsync(db);

        var deadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.Low, "Website", DateTimeOffset.UtcNow);

        Assert.Null(deadline);
    }

    [Fact]
    public async Task ComputeSlaDeadlineAsync_TenantIsolation_NeverMatchesAnotherBusinesssTarget()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessIdA = await CreateBusinessAsync(db);
        var businessIdB = await CreateBusinessAsync(db);
        await SeedSlaTargetAsync(db, businessIdA, TicketPriority.Urgent, channel: null, targetMinutes: 5);

        var deadline = await service.ComputeSlaDeadlineAsync(businessIdB, TicketPriority.Urgent, "Website", DateTimeOffset.UtcNow);

        Assert.Null(deadline);
    }

    /// <summary>
    /// Demonstrates this method always live-looks-up the current SlaTarget
    /// row rather than caching — the same (businessId, priority, channel)
    /// input yields a different deadline once the underlying rule changes.
    /// Whether/when a computed deadline gets frozen onto Ticket.SlaDeadline
    /// is entirely the future caller's responsibility (none exists yet).
    /// </summary>
    [Fact]
    public async Task ComputeSlaDeadlineAsync_ReflectsCurrentTargetConfig_NotACachedValue()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ITicketSlaService>();
        var businessId = await CreateBusinessAsync(db);
        await SeedSlaTargetAsync(db, businessId, TicketPriority.Low, channel: null, targetMinutes: 60);
        var createdAt = DateTimeOffset.UtcNow;

        var firstDeadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.Low, "Website", createdAt);
        Assert.Equal(createdAt.AddMinutes(60), firstDeadline);

        var target = await db.SlaTargets.IgnoreQueryFilters().SingleAsync(t => t.BusinessId == businessId);
        target.TargetMinutes = 10;
        await db.SaveChangesAsync();

        var secondDeadline = await service.ComputeSlaDeadlineAsync(businessId, TicketPriority.Low, "Website", createdAt);
        Assert.Equal(createdAt.AddMinutes(10), secondDeadline);
    }

    // ── ComputeFirstRespondedAt (pure, no DB) ────────────────────────

    private static Ticket NewTicket(DateTimeOffset createdAt, DateTimeOffset? firstRespondedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        PriorityReason = "Test",
        Channel = "Website",
        FirstRespondedAt = firstRespondedAt,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static Message NewMessage(MessageRole role, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Role = role,
        Content = "test",
        CreatedAt = createdAt,
    };

    [Fact]
    public void ComputeFirstRespondedAt_AgentMessageAfterTicketCreation_ReturnsItsCreatedAt()
    {
        var service = new TicketSlaService(null!);
        var ticketCreatedAt = DateTimeOffset.UtcNow;
        var ticket = NewTicket(ticketCreatedAt);
        var message = NewMessage(MessageRole.Agent, ticketCreatedAt.AddMinutes(5));

        var result = service.ComputeFirstRespondedAt(ticket, message);

        Assert.Equal(message.CreatedAt, result);
    }

    [Fact]
    public void ComputeFirstRespondedAt_AiMessage_Excluded_ReturnsNull()
    {
        var service = new TicketSlaService(null!);
        var ticketCreatedAt = DateTimeOffset.UtcNow;
        var ticket = NewTicket(ticketCreatedAt);
        var message = NewMessage(MessageRole.Ai, ticketCreatedAt.AddMinutes(5));

        var result = service.ComputeFirstRespondedAt(ticket, message);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeFirstRespondedAt_CustomerMessage_Excluded_ReturnsNull()
    {
        var service = new TicketSlaService(null!);
        var ticketCreatedAt = DateTimeOffset.UtcNow;
        var ticket = NewTicket(ticketCreatedAt);
        var message = NewMessage(MessageRole.Customer, ticketCreatedAt.AddMinutes(5));

        var result = service.ComputeFirstRespondedAt(ticket, message);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeFirstRespondedAt_AgentMessageBeforeTicketCreation_ReturnsNull()
    {
        var service = new TicketSlaService(null!);
        var ticketCreatedAt = DateTimeOffset.UtcNow;
        var ticket = NewTicket(ticketCreatedAt);
        var message = NewMessage(MessageRole.Agent, ticketCreatedAt.AddMinutes(-5));

        var result = service.ComputeFirstRespondedAt(ticket, message);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeFirstRespondedAt_AlreadySet_NeverOverwritten()
    {
        var service = new TicketSlaService(null!);
        var ticketCreatedAt = DateTimeOffset.UtcNow;
        var existing = ticketCreatedAt.AddMinutes(2);
        var ticket = NewTicket(ticketCreatedAt, firstRespondedAt: existing);
        var message = NewMessage(MessageRole.Agent, ticketCreatedAt.AddMinutes(10));

        var result = service.ComputeFirstRespondedAt(ticket, message);

        Assert.Equal(existing, result); // the later message must not overwrite the earlier recorded value
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
