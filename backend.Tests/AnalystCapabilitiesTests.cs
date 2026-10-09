using System.Text.Json;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.Tests;

/// <summary>
/// Step 23 Phase 3 — design §1c / implementation plan §3. Deliberately
/// Claude-free: no orchestration, no dispatch ceiling, no fakes. Each of the
/// 5 concrete IAnalystCapability implementations is proven two ways:
/// (1) InputSchema/Description shape assertions (the 4 range-based
/// capabilities' `range` enum, and GetHandledBySnapshotCapability's empty
/// schema and explicit non-range/non-trend language), and (2) ExecuteAsync
/// called directly against real seeded data, asserting a byte-identical
/// JSON match against IAnalyticsQueryService's own already-verified output
/// for the same window/business — proving each capability is a faithful,
/// parameter-correct pass-through, not a second, independently-risked
/// query (the same discipline AnalyticsQueryServiceTests.cs already applies
/// one layer down). Tenant isolation itself is not re-proven here — that is
/// already established at the IAnalyticsQueryService layer these
/// capabilities delegate to entirely; this file's job is proving the
/// wrapper is faithful, not re-deriving isolation a third time.
/// </summary>
public class AnalystCapabilitiesTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()));
        _provider = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _provider);
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private async Task<(Guid BusinessId, Guid WidgetId, Guid CustomerId)> SeedBusinessAsync(string namePrefix)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var businessId = Guid.NewGuid();
        var widgetId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = $"{namePrefix} {Guid.NewGuid():N}",
            TimeZone = "UTC",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Widgets.Add(new Widget
        {
            Id = widgetId,
            BusinessId = businessId,
            Name = $"{namePrefix} Widget",
            PublicKey = $"analyst-capability-test-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Customers.Add(new Customer
        {
            Id = customerId,
            BusinessId = businessId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return (businessId, widgetId, customerId);
    }

    private static async Task AddConversationAsync(
        AppDbContext db, Guid businessId, Guid widgetId,
        ConversationStatus status, HandledBy handledBy, DateTimeOffset createdAt)
    {
        db.Conversations.Add(new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            WidgetId = widgetId,
            Status = status,
            HandledBy = handledBy,
            Channel = "widget",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddTicketAsync(
        AppDbContext db, Guid businessId, Guid customerId, TicketStatus status, DateTimeOffset createdAt)
    {
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            Status = status,
            Priority = TicketPriority.Medium,
            PriorityReason = TicketPriorityReasons.ManualEscalation,
            Channel = "widget",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddHandoffAsync(AppDbContext db, Guid businessId, DateTimeOffset createdAt)
    {
        db.ConversationHandoffs.Add(new ConversationHandoff
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ConversationId = null,
            FromHandledBy = HandledBy.Ai,
            ToHandledBy = HandledBy.Human,
            Reason = HandoffReasons.StaffManualTakeover,
            ActorType = ActorType.NivoraSystem,
            ActorId = null,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset UtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private AppDbContext DbFor(Guid businessId) =>
        new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(businessId))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                TestDatabase.ConnectionString, x => x.UseVector()))
            .BuildServiceProvider()
            .GetRequiredService<AppDbContext>();

    private static void AssertRangeInputSchema(IAnalystCapability capability)
    {
        Assert.True(capability.InputSchema.Properties.TryGetValue("range", out var rangeProperty));
        Assert.Equal("string", rangeProperty.GetProperty("type").GetString());

        var enumValues = rangeProperty.GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet();
        Assert.Equal(new HashSet<string?> { "7d", "30d", "90d" }, enumValues);

        Assert.NotNull(capability.InputSchema.Required);
        Assert.Contains("range", capability.InputSchema.Required!);
    }

    private static async Task AssertRejectsInvalidRangeAsync(IAnalystCapability capability, Guid businessId)
    {
        var input = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("14d") };
        var result = await capability.ExecuteAsync(businessId, input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("range must be one of", result.Content);
    }

    private static async Task AssertRejectsMissingRangeAsync(IAnalystCapability capability, Guid businessId)
    {
        var result = await capability.ExecuteAsync(businessId, new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("range must be one of", result.Content);
    }

    // ── InputSchema / Description shape ───────────────────────────────

    [Fact]
    public void GetConversationsByDayCapability_InputSchema_RequiresRangeEnum() =>
        AssertRangeInputSchema(new GetConversationsByDayCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid()))));

    [Fact]
    public void GetConversationStatusDistributionCapability_InputSchema_RequiresRangeEnum() =>
        AssertRangeInputSchema(new GetConversationStatusDistributionCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid()))));

    [Fact]
    public void GetHandoffsByDayCapability_InputSchema_RequiresRangeEnum() =>
        AssertRangeInputSchema(new GetHandoffsByDayCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid()))));

    [Fact]
    public void GetTicketStatusDistributionCapability_InputSchema_RequiresRangeEnum() =>
        AssertRangeInputSchema(new GetTicketStatusDistributionCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid()))));

    [Fact]
    public void GetHandledBySnapshotCapability_InputSchema_IsEmpty()
    {
        var capability = new GetHandledBySnapshotCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid())));

        Assert.Empty(capability.InputSchema.Properties);
        Assert.True(capability.InputSchema.Required is null || !capability.InputSchema.Required.Any());
    }

    [Fact]
    public void GetHandledBySnapshotCapability_Description_RulesOutRangeAndTrendUsage()
    {
        var capability = new GetHandledBySnapshotCapability(new AnalyticsQueryService(DbFor(Guid.NewGuid())));

        // Design §1c: this limitation must be stated prominently in the
        // Description content itself, not left implicit — a real behavioral
        // contract the tool-selection prompt depends on, not just prose.
        Assert.Contains("does not accept a date range", capability.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trend", capability.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── Faithful pass-through against real seeded data ────────────────

    [Fact]
    public async Task GetConversationsByDayCapability_ExecuteAsync_MatchesAnalyticsQueryServiceOutput()
    {
        var (bizId, widgetId, _) = await SeedBusinessAsync("Analyst Capability Conversations");
        var today = UtcMidnight(DateOnly.FromDateTime(DateTime.UtcNow));
        await using (var seedDb = DbFor(bizId))
        {
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, today);
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Closed, HandledBy.Human, today);
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, today.AddDays(-1));
        }

        await using var expectedDb = DbFor(bizId);
        var expectedService = new AnalyticsQueryService(expectedDb);
        Assert.True(expectedService.TryParseRange("7d", out var window));
        var expected = await expectedService.GetConversationsByDayAsync(window, CancellationToken.None);

        await using var capabilityDb = DbFor(bizId);
        var capability = new GetConversationsByDayCapability(new AnalyticsQueryService(capabilityDb));
        var input = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("7d") };
        var result = await capability.ExecuteAsync(bizId, input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), result.Content);

        await AssertRejectsInvalidRangeAsync(capability, bizId);
        await AssertRejectsMissingRangeAsync(capability, bizId);
    }

    [Fact]
    public async Task GetConversationStatusDistributionCapability_ExecuteAsync_MatchesAnalyticsQueryServiceOutput()
    {
        var (bizId, widgetId, _) = await SeedBusinessAsync("Analyst Capability Status");
        var now = DateTimeOffset.UtcNow;
        await using (var seedDb = DbFor(bizId))
        {
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, now);
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Closed, HandledBy.Human, now);
        }

        await using var expectedDb = DbFor(bizId);
        var expectedService = new AnalyticsQueryService(expectedDb);
        Assert.True(expectedService.TryParseRange("30d", out var window));
        var expected = await expectedService.GetConversationStatusDistributionAsync(window, CancellationToken.None);

        await using var capabilityDb = DbFor(bizId);
        var capability = new GetConversationStatusDistributionCapability(new AnalyticsQueryService(capabilityDb));
        var input = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("30d") };
        var result = await capability.ExecuteAsync(bizId, input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), result.Content);

        await AssertRejectsInvalidRangeAsync(capability, bizId);
        await AssertRejectsMissingRangeAsync(capability, bizId);
    }

    [Fact]
    public async Task GetHandledBySnapshotCapability_ExecuteAsync_MatchesAnalyticsQueryServiceOutput_IgnoringInput()
    {
        var (bizId, widgetId, _) = await SeedBusinessAsync("Analyst Capability HandledBy");
        var now = DateTimeOffset.UtcNow;
        await using (var seedDb = DbFor(bizId))
        {
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, now);
            // A very old row — proves the capability's own pass-through
            // doesn't accidentally introduce a filter GetHandledBySnapshotAsync
            // itself doesn't have.
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Open, HandledBy.Ai, now.AddDays(-400));
            await AddConversationAsync(seedDb, bizId, widgetId, ConversationStatus.Closed, HandledBy.Human, now);
        }

        await using var expectedDb = DbFor(bizId);
        var expected = await new AnalyticsQueryService(expectedDb).GetHandledBySnapshotAsync(CancellationToken.None);

        await using var capabilityDb = DbFor(bizId);
        var capability = new GetHandledBySnapshotCapability(new AnalyticsQueryService(capabilityDb));

        // No `range` in the input at all — the capability must not require
        // or look for one.
        var result = await capability.ExecuteAsync(bizId, new Dictionary<string, JsonElement>(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), result.Content);

        // Even a stray `range` value in the input (never sent by a correctly-
        // behaving caller, but defense-in-depth) must still be ignored, not
        // rejected — this capability has no range concept to validate against.
        var inputWithStrayRange = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("14d") };
        var resultWithStrayInput = await capability.ExecuteAsync(bizId, inputWithStrayRange, CancellationToken.None);
        Assert.False(resultWithStrayInput.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), resultWithStrayInput.Content);
    }

    [Fact]
    public async Task GetHandoffsByDayCapability_ExecuteAsync_MatchesAnalyticsQueryServiceOutput()
    {
        var (bizId, _, _) = await SeedBusinessAsync("Analyst Capability Handoffs");
        var today = UtcMidnight(DateOnly.FromDateTime(DateTime.UtcNow));
        await using (var seedDb = DbFor(bizId))
        {
            await AddHandoffAsync(seedDb, bizId, today);
            await AddHandoffAsync(seedDb, bizId, today);
            await AddHandoffAsync(seedDb, bizId, today.AddDays(-2));
        }

        await using var expectedDb = DbFor(bizId);
        var expectedService = new AnalyticsQueryService(expectedDb);
        Assert.True(expectedService.TryParseRange("90d", out var window));
        var expected = await expectedService.GetHandoffsByDayAsync(window, CancellationToken.None);

        await using var capabilityDb = DbFor(bizId);
        var capability = new GetHandoffsByDayCapability(new AnalyticsQueryService(capabilityDb));
        var input = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("90d") };
        var result = await capability.ExecuteAsync(bizId, input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), result.Content);

        await AssertRejectsInvalidRangeAsync(capability, bizId);
        await AssertRejectsMissingRangeAsync(capability, bizId);
    }

    [Fact]
    public async Task GetTicketStatusDistributionCapability_ExecuteAsync_MatchesAnalyticsQueryServiceOutput()
    {
        var (bizId, _, customerId) = await SeedBusinessAsync("Analyst Capability Tickets");
        var now = DateTimeOffset.UtcNow;
        await using (var seedDb = DbFor(bizId))
        {
            await AddTicketAsync(seedDb, bizId, customerId, TicketStatus.Open, now);
            await AddTicketAsync(seedDb, bizId, customerId, TicketStatus.Resolved, now);
            await AddTicketAsync(seedDb, bizId, customerId, TicketStatus.Reopened, now);
        }

        await using var expectedDb = DbFor(bizId);
        var expectedService = new AnalyticsQueryService(expectedDb);
        Assert.True(expectedService.TryParseRange("7d", out var window));
        var expected = await expectedService.GetTicketStatusDistributionAsync(window, CancellationToken.None);

        await using var capabilityDb = DbFor(bizId);
        var capability = new GetTicketStatusDistributionCapability(new AnalyticsQueryService(capabilityDb));
        var input = new Dictionary<string, JsonElement> { ["range"] = JsonSerializer.SerializeToElement("7d") };
        var result = await capability.ExecuteAsync(bizId, input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(JsonSerializer.Serialize(expected), result.Content);

        await AssertRejectsInvalidRangeAsync(capability, bizId);
        await AssertRejectsMissingRangeAsync(capability, bizId);
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
