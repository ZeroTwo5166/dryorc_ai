using System.Net;
using System.Net.Http.Json;
using backend.Controllers;
using backend.Data;
using backend.Enums;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// One end-to-end test proving Step 8's real wiring: POST /api/widget/messages
/// returns a real Claude-generated reply, and that reply lands in the
/// conversation's message history exactly as returned. Uses
/// LiveClaudeApiFactory (real IMessageService, not NivoraApiFactory's
/// fake) and is gated behind [LiveClaudeFact] — this is the one place in
/// the suite that makes a real, billed Anthropic API call, and only runs
/// when Anthropic:EnableLiveIntegrationTests=true AND a real API key are
/// both configured.
/// </summary>
public class ClaudeIntegrationTests : IAsyncLifetime
{
    private readonly LiveClaudeApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    [LiveClaudeFact]
    public async Task PostMessage_ReturnsRealAiReply_PersistedInConversationHistory()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Claude Integration Test Business",
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

        using var client = NewClient();
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token),
            new PostWidgetMessageRequest("What are your store hours?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetMessageResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AiReply));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await verifyDb.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == business.Id);
        var messages = await verifyDb.Messages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, messages.Count); // customer message + AI reply
        Assert.Equal(MessageRole.Customer, messages[0].Role);
        Assert.Equal(MessageRole.Ai, messages[1].Role);
        Assert.Equal(body.AiReply, messages[1].Content);
        Assert.Equal(messages[1].CreatedAt, conversation.LastMessageAt);
    }
}
