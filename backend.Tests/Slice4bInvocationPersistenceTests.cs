using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 4b (design §10.5): the migrated 4b call sites
/// persist real AiProviderInvocations rows through Program.cs's own
/// AiInvocationRecorder, over HTTP. Runs on PersistingInvocationRecorderApiFactory
/// (design §7.3), so cleanup deletes AiProviderInvocations before Businesses
/// (Restrict FK) — same order as AiProviderWrapperRegistrationTests. In the
/// KnowledgeChunks collection because POST /api/knowledge writes chunks
/// (see KnowledgeChunksCollection).
/// </summary>
[Collection("KnowledgeChunks collection")]
public class Slice4bInvocationPersistenceTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new PersistingInvocationRecorderApiFactory();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // Mutation (design §10.5, same as T-API2): revert V1 in
    // KnowledgeIndexingService.CreateAsync to the raw
    // IVoyageEmbeddingClient.EmbedAsync path — no row is persisted.
    [Fact]
    public async Task CreateKnowledgeDocument_PersistsOneVoyageKnowledgeIndexingRow()
    {
        var (businessId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Slice 4b Persisted Knowledge Business");
        using var _o = ownerClient;
        var content = "We ship within 2 business days. Returns are accepted within 30 days.";

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/knowledge", ownerCookie),
            new CreateKnowledgeDocumentRequest("Faq", "Shipping and returns", content)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = Assert.Single(await RowsForBusinessAsync(businessId));
        Assert.Equal(AiProviders.Voyage, row.Provider);
        Assert.Equal(VoyageModels.Voyage3, row.Model);
        Assert.Equal(AiOperations.KnowledgeIndexing, row.Operation);
        Assert.Equal(0, row.Sequence);
        Assert.Null(row.ConversationId);
        Assert.Equal(AiOutcomes.Succeeded, row.Outcome);
        Assert.Null(row.ErrorCategory);
        Assert.Equal(KnowledgeChunker.Chunk(content).Count, row.InputItemCount);
        Assert.NotNull(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheCreationInputTokens);
        Assert.Null(row.CacheReadInputTokens);
    }

    // The factory's default FakeMessageService selects no tools, so the Ask
    // is Refused after A4 only: one AnalystSelection row (design §2.2).
    [Fact]
    public async Task EnabledAsk_PersistsOneAnthropicAnalystSelectionRow()
    {
        var (businessId, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Slice 4b Persisted Analyst Business");
        using var _o = ownerClient;
        var enable = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/ai-business-analyst", ownerCookie),
            new SetAiBusinessAnalystEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie),
            new AnalystQuestionRequest("How many conversations this week?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await RowsForBusinessAsync(businessId));
        Assert.Equal(AiProviders.Anthropic, row.Provider);
        Assert.Equal(AnthropicModels.Default, row.Model);
        Assert.Equal(AiOperations.AnalystSelection, row.Operation);
        Assert.Equal(0, row.Sequence);
        Assert.Null(row.ConversationId);
        Assert.Equal(AiOutcomes.Succeeded, row.Outcome);
    }

    private async Task<List<AiProviderInvocation>> RowsForBusinessAsync(Guid businessId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiProviderInvocations.IgnoreQueryFilters().Where(x => x.BusinessId == businessId).ToListAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>Registers a fresh business (Owner), confirms email, logs in — same steps as AnalystApiTests.SetUpBusinessWithOwnerAsync.</summary>
    private async Task<(Guid BusinessId, HttpClient OwnerClient, string OwnerCookie)> SetUpBusinessWithOwnerAsync(string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var registerResponse = await anonClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, "Passw0rd!!"));
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        var registerBody = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);

        string token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(registerBody.UserId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }
        var confirmResponse = await anonClient.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(registerBody.UserId, token));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        var client = NewClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Passw0rd!!"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = loginResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;
        Assert.NotNull(cookie);

        return (registerBody.BusinessId, client, cookie!);
    }
}
