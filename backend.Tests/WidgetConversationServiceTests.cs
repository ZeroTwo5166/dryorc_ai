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
/// Service-level regression test for the original Steps 6-7 guarantee:
/// WidgetConversationService.PostMessageAsync atomically persists the
/// customer message and updates Conversation.LastMessageAt to that
/// message's own timestamp, in the same write.
///
/// Deliberately calls IWidgetConversationService directly — no
/// WidgetController, no IClaudeReplyService anywhere in this file. Once a
/// request passes through the controller, Step 8's AI reply write runs
/// synchronously afterward and overwrites LastMessageAt again before any
/// HTTP response is observable, which makes this specific guarantee
/// unassertable at that layer (see WidgetPipelineTests.
/// PostMessage_SecondMessage_ReusesExistingOpenConversation_LastMessageAtUpdated,
/// which now tests the full pipeline instead and deliberately does not
/// assert LastMessageAt). Testing WidgetConversationService directly is
/// the layer where the original write-path guarantee is still true and
/// still observable — nothing else touches Conversation.LastMessageAt
/// unless the controller also calls GenerateReplyAsync, which nothing
/// here does.
/// </summary>
public class WidgetConversationServiceTests : IAsyncLifetime
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
            .AddScoped<IWidgetConversationService, WidgetConversationService>()
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _provider.DisposeAsync();
    }

    // Same truncation as WidgetPipelineTests/NotificationApiTests — Postgres's
    // timestamptz stores microsecond precision, one fractional digit less
    // than .NET's 100ns ticks; a value read straight off a returned entity
    // (never round-tripped) still carries full tick precision, while the
    // same instant read back via a fresh DbContext comes back truncated.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Conversation Service Test {Guid.NewGuid():N}",
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var widget = new Widget
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Test Widget",
            PublicKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    [Fact]
    public async Task PostMessageAsync_SecondCall_ReusesConversation_SetsLastMessageAtToSecondCustomerMessageTimestamp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversationService = scope.ServiceProvider.GetRequiredService<IWidgetConversationService>();

        var (business, widget) = await CreateBusinessAndWidgetAsync(db);
        var visitorSessionId = Guid.NewGuid().ToString("N");

        var firstMessage = await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "First message");
        var secondMessage = await conversationService.PostMessageAsync(business.Id, widget.Id, visitorSessionId, "Second message");

        // Both calls operated on the same conversation — reused, not a
        // second Conversation created for the second call.
        Assert.Equal(firstMessage.ConversationId, secondMessage.ConversationId);
        Assert.Equal(MessageRole.Customer, secondMessage.Role);

        // Reload from a fresh DbContext — this must reflect a real,
        // committed write, not anything still tracked by the scope above.
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await verifyDb.Conversations
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == secondMessage.ConversationId);

        // Specifically the second customer message's own timestamp — not
        // merely "changed" or "non-null."
        Assert.Equal(TruncateToMicroseconds(secondMessage.CreatedAt), conversation.LastMessageAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
