using System.Net;
using Anthropic.Exceptions;
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
/// Service-level tests for ClaudeReplyService, run against the real Neon
/// dev DB via a bare ServiceProvider — same pattern as
/// CustomerDataLifecycleTests. IMessageService is constructed directly per
/// test (FakeMessageService, a real AnthropicClient, or
/// MissingAnthropicApiKeyMessageService) rather than through a shared DI
/// registration, so each scenario gets exactly the implementation it needs
/// with no risk of one test's setup leaking into another's.
///
/// Only ONE test in this file makes a real, billed Anthropic call — gated
/// behind [LiveClaudeFact]. Every other test here uses FakeMessageService's
/// realistic Message/ContentBlock/TextBlock/Usage response shape, so
/// ClaudeReplyService's actual content-block extraction logic is exercised
/// for real, without any network dependency.
/// </summary>
public class ClaudeReplyServiceTests : IAsyncLifetime
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

        // Deliberately no IMessageService/IClaudeReplyService registered
        // here — each test constructs ClaudeReplyService directly with
        // exactly the IMessageService implementation its scenario needs.
        _provider = new ServiceCollection()
            .AddSingleton<ICurrentTenantAccessor>(new FixedTenantAccessor(null))
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(
                PostgresConnectionString.FromUri(connectionUri), x => x.UseVector()))
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

    private async Task<(Business Business, Conversation Conversation)> SeedConversationWithOneCustomerMessageAsync(AppDbContext db)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Claude Reply Test {Guid.NewGuid():N}",
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
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            WidgetId = widget.Id,
            VisitorSessionId = Guid.NewGuid().ToString("N"),
            Channel = "Website",
            Status = ConversationStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        db.Conversations.Add(conversation);
        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Customer,
            Content = "Do you ship to Denmark?",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, conversation);
    }

    // ── Success path — fake, but realistic response shape ──────────

    [Fact]
    public async Task GenerateReplyAsync_FakeRealisticResponse_ExtractsTextViaRealParsing_PersistsAiMessage()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ReplyText = "Yes, we ship to Denmark within 3-5 business days." };
        var service = new ClaudeReplyService(db, fake);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        // Proves this went through ClaudeReplyService's real
        // response.Content.Select(b => b.Value).OfType<TextBlock>()
        // extraction — the fake returns a Message with a real TextBlock
        // inside a real ContentBlock union, not a pre-extracted string.
        Assert.Equal(fake.ReplyText, reply);
        Assert.Equal(1, fake.CallCount);

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(fake.ReplyText, aiMessage.Content);

        var updatedConversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(aiMessage.CreatedAt, updatedConversation.LastMessageAt);
    }

    // ── Anthropic-typed failures — each returns null, nothing persisted ─

    [Fact]
    public async Task GenerateReplyAsync_AnthropicRateLimitException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" },
        };
        var service = new ClaudeReplyService(db, fake);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    [Fact]
    public async Task GenerateReplyAsync_Anthropic5xxException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" },
        };
        var service = new ClaudeReplyService(db, fake);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    [Fact]
    public async Task GenerateReplyAsync_AnthropicApiException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService
        {
            ExceptionToThrow = new AnthropicApiException("Simulated API error") { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" },
        };
        var service = new ClaudeReplyService(db, fake);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // ── Genuine network/timeout failures — same "return null" outcome,
    //    but a structurally different exception family (not the SDK's own
    //    typed hierarchy) ─────────────────────────────────────────────

    [Fact]
    public async Task GenerateReplyAsync_HttpRequestException_ReturnsNull_PersistsNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var fake = new FakeMessageService { ExceptionToThrow = new HttpRequestException("Simulated network failure") };
        var service = new ClaudeReplyService(db, fake);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.Null(reply);
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    // ── Missing configuration — must NOT be swallowed into a null reply ─

    [Fact]
    public async Task GenerateReplyAsync_MissingApiKeyConfiguration_ThrowsDistinctException_IsNotSwallowed()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        // No real secrets touched — this constructs the exact stub
        // Program.cs registers when Anthropic:ApiKey is absent, directly,
        // independent of whatever's actually configured in this environment.
        var service = new ClaudeReplyService(db, new MissingAnthropicApiKeyMessageService());

        await Assert.ThrowsAsync<AnthropicNotConfiguredException>(
            () => service.GenerateReplyAsync(business.Id, conversation.Id));

        // Confirms the exception genuinely propagated rather than being
        // caught-and-converted somewhere — no AI message, no LastMessageAt
        // change, same as the other failure tests, but reached via a throw
        // the caller must handle, not a quiet null.
        await AssertNoAiMessageOrLastMessageAtChangeAsync(db, conversation.Id);
    }

    private static async Task AssertNoAiMessageOrLastMessageAtChangeAsync(AppDbContext db, Guid conversationId)
    {
        var messageCount = await db.Messages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId);
        Assert.Equal(1, messageCount); // still just the original seeded customer message

        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId);
        Assert.Null(conversation.LastMessageAt);
    }

    // ── Real Anthropic API call — explicitly opt-in only ────────────

    [LiveClaudeFact]
    public async Task GenerateReplyAsync_RealApiCall_ReturnsReplyAndPersistsAiMessage()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var apiKey = configuration["Anthropic:ApiKey"]!; // LiveClaudeFact already guarantees this is present

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (business, conversation) = await SeedConversationWithOneCustomerMessageAsync(db);

        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var service = new ClaudeReplyService(db, client.Messages);

        var reply = await service.GenerateReplyAsync(business.Id, conversation.Id);

        Assert.False(string.IsNullOrWhiteSpace(reply));

        var aiMessage = await db.Messages.IgnoreQueryFilters()
            .SingleAsync(m => m.ConversationId == conversation.Id && m.Role == MessageRole.Ai);
        Assert.Equal(reply, aiMessage.Content);

        var updatedConversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversation.Id);
        Assert.Equal(aiMessage.CreatedAt, updatedConversation.LastMessageAt);
    }

    private class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
