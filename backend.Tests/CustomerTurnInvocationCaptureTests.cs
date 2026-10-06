using System.Net;
using System.Net.Http.Json;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 4a (design §6.2, T-API4a1/T-API4a2): a widget turn
/// over HTTP records one AiProviderInvocation per customer-turn provider call
/// into the default NivoraApiFactory's InvocationCapture. Nothing is
/// persisted (design §6.1), so cleanup is the same as WidgetPipelineTests'.
/// The widget is initialised without a signed identity token, so no customer
/// is resolved and the reply takes the A1 (no-tool) branch.
/// </summary>
public class CustomerTurnInvocationCaptureTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // T-API4a1. Mutation: revert any one of V3, A1 or A3 to the raw client
    // (4b errata §1 form) → count 2.
    [Fact]
    public async Task WidgetTurn_NoCustomer_RecordsRetrievalReplyAndClassification_EachSeq0_DistinctCorrelations()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var token = await InitAsync(client, widget);

        await PostMessageAsync(client, token, "Do you ship to Denmark?");

        var conversationId = await SingleConversationIdAsync(business.Id);
        var records = _factory.InvocationCapture.ForBusiness(business.Id);
        Assert.Equal(3, records.Count);
        Assert.Equal(
            [AiOperations.KnowledgeRetrievalQuery, AiOperations.CustomerReply, AiOperations.ConversationClassification],
            records.Select(r => r.Operation));
        Assert.All(records, r => Assert.Equal(0, r.Sequence));
        Assert.Equal(3, records.Select(r => r.CorrelationId).Distinct().Count());
        Assert.All(records, r => Assert.Equal(conversationId, r.ConversationId));
    }

    // T-API4a2. Mutation: a static correlation in any one of V3, A1 or A3
    // → 5 distinct CorrelationIds.
    [Fact]
    public async Task TwoWidgetTurns_OneConversation_RecordSixDistinctCorrelations()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var token = await InitAsync(client, widget);

        await PostMessageAsync(client, token, "Do you ship to Denmark?");
        await PostMessageAsync(client, token, "How long does delivery take?");

        var conversationId = await SingleConversationIdAsync(business.Id);
        var records = _factory.InvocationCapture.ForBusiness(business.Id);
        Assert.Equal(6, records.Count);
        Assert.Equal(6, records.Select(r => r.CorrelationId).Distinct().Count());
        Assert.All(records, r => Assert.Equal(conversationId, r.ConversationId));
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Slice 4a Capture Test {Guid.NewGuid():N}",
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
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    // Init-then-message pattern from ClaudeIntegrationTests (design §6.2).
    private static async Task<string> InitAsync(HttpClient client, Widget widget)
    {
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        return initBody!.Token;
    }

    private static async Task PostMessageAsync(HttpClient client, string token, string content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/messages");
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Content = JsonContent.Create(new PostWidgetMessageRequest(content));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> SingleConversationIdAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == businessId);
        return conversation.Id;
    }
}
