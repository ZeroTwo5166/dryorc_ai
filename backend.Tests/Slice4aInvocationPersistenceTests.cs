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
/// Step 27 Slice A1, Slice 4a (design §6.2, T-P4a1): one widget turn persists
/// real AiProviderInvocations rows through Program.cs's own
/// AiInvocationRecorder. Runs on PersistingInvocationRecorderApiFactory, so
/// cleanup deletes AiProviderInvocations first (Restrict FK on Businesses),
/// following Slice4bInvocationPersistenceTests, then the widget rows in
/// ClaudeIntegrationTests' order (design §6.1).
/// </summary>
public class Slice4aInvocationPersistenceTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new PersistingInvocationRecorderApiFactory();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AiProviderInvocations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    // T-P4a1. The recorder fails open, so a row that broke a table CHECK or
    // the ConversationId FK would be dropped, not thrown: the exact count is
    // what shows all three rows satisfy them. Mutation: revert V3 to the raw
    // IVoyageEmbeddingClient (4b errata §1 form) → 2 rows.
    [Fact]
    public async Task WidgetTurn_PersistsThreeRows_EachCarryingThePersistedConversation()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/messages");
        request.Headers.Add("Authorization", $"Bearer {initBody!.Token}");
        request.Content = JsonContent.Create(new PostWidgetMessageRequest("Do you ship to Denmark?"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.BusinessId == business.Id);
        var rows = await db.AiProviderInvocations.IgnoreQueryFilters()
            .Where(x => x.BusinessId == business.Id)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            [AiOperations.ConversationClassification, AiOperations.CustomerReply, AiOperations.KnowledgeRetrievalQuery],
            rows.Select(r => r.Operation).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(conversation.Id, r.ConversationId));
        Assert.All(rows, r => Assert.Equal(AiOutcomes.Succeeded, r.Outcome));
    }

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Slice 4a Persisted Turn Test {Guid.NewGuid():N}",
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
}
