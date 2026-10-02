using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 23 Phase 5 (design §4/§5/§6) — HTTP-level tests for
/// AnalystController.Ask, run against the real app + real dev DB via
/// NivoraApiFactory, same pattern as AiBusinessAnalystApiTests's own
/// coverage of the sibling toggle endpoint. Proves the wiring this phase
/// actually adds — kill-switch gating, AnyStaff authorization, tenant
/// resolution, and the outcome-to-HTTP-status mapping — not orchestration
/// correctness itself, which Phase 4's fakes-only suite already proves
/// exhaustively. Most tests use the factory's default FakeMessageService
/// (always a single zero-tool TextBlock reply, per its own doc comment),
/// which is sufficient to prove the Refused/Disabled paths; tests that need
/// a specific outcome (Answered/CapabilityFailed/ClaudeCallFailed) layer a
/// SequencedFakeMessageService on top via WithWebHostBuilder, mirroring
/// IntegrationsApiTests' own established pattern for this exact kind of
/// per-test DI override.
/// </summary>
public class AnalystApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    private async Task ConfirmEmailAsync(HttpClient client, Guid userId)
    {
        string token;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            token = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        }

        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<(HttpResponseMessage Response, LoginResponse? Body, string? Cookie)> LoginAsync(
        HttpClient client, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        LoginResponse? body = null;
        string? cookie = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<LoginResponse>();
            cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.First().Split(';')[0] : null;
        }
        return (response, body, cookie);
    }

    private async Task<ApplicationUser> SeedStaffIntoBusinessAsync(Guid businessId, string email, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var createResult = await userManager.CreateAsync(user, "Passw0rd!!");
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }

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

    private async Task<(Guid BusinessId, Guid OwnerId, HttpClient OwnerClient, string OwnerCookie)> SetUpBusinessWithOwnerAsync(string businessName)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, businessName, email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(registerBody);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        return (registerBody.BusinessId, registerBody.UserId, client, cookie!);
    }

    private async Task EnableAnalystAsync(HttpClient ownerClient, string ownerCookie)
    {
        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Put, "/api/business/ai-business-analyst", ownerCookie),
            new SetAiBusinessAnalystEnabledRequest(true)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Dictionary<string, JsonElement> Args(object anonymous) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(anonymous))!;

    private static Usage FakeUsage() => new()
    {
        CacheCreation = null,
        CacheCreationInputTokens = null,
        CacheReadInputTokens = null,
        InferenceGeo = null,
        InputTokens = 1,
        OutputTokens = 1,
        OutputTokensDetails = null,
        ServerToolUse = null,
        ServiceTier = null,
    };

    private static Anthropic.Models.Messages.Message TextResponse(string text) => new()
    {
        ID = $"msg_{Guid.NewGuid():N}",
        Container = null,
        Content = [new TextBlock { Text = text, Citations = null }],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "end_turn",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    private static Anthropic.Models.Messages.Message ToolUseResponse(string toolName, Dictionary<string, JsonElement> input) => new()
    {
        ID = $"msg_{Guid.NewGuid():N}",
        Container = null,
        Content = [new ToolUseBlock { ID = $"tu_{Guid.NewGuid():N}", Name = toolName, Input = input, Caller = null }],
        Model = "claude-sonnet-5",
        StopDetails = null,
        StopReason = "tool_use",
        StopSequence = null,
        Usage = FakeUsage(),
    };

    /// <summary>
    /// Layers a SequencedFakeMessageService on top of the shared factory's
    /// default FakeMessageService, mirroring IntegrationsApiTests'
    /// NewShopifyFactory pattern exactly. Returns the fake so the caller can
    /// both enqueue responses and later assert CallCount/Requests.
    /// </summary>
    private (WebApplicationFactory<Program> Factory, SequencedFakeMessageService Fake) NewScriptedFactory()
    {
        var fake = new SequencedFakeMessageService();
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMessageService>(fake);
                // Defence in depth only (A1 Slice 4b design §7.3, §13 #10): the
                // inherited base ConfigureTestServices already registers this
                // capture; stated here so the scripted host never relies on it.
                services.AddSingleton<IAiInvocationRecorder>(_factory.InvocationCapture);
            });
        });
        return (factory, fake);
    }

    // ── Kill switch (design §4) ─────────────────────────────────────────

    [Fact]
    public async Task Ask_WhenDisabled_Returns403_WithDisabledOutcome_AndNeverCallsClaude()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Disabled Business");
        using var _o = ownerClient;
        // Flag defaults false — never toggled in this test.

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie),
            new AnalystQuestionRequest("How many conversations this week?")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalystAnswerResponse>();
        Assert.Equal("Disabled", body!.Outcome);

        var messageService = Assert.IsType<FakeMessageService>(_factory.Services.GetRequiredService<IMessageService>());
        Assert.Equal(0, messageService.CallCount); // gate is structurally first, not merely labeled Disabled after work happened
    }

    [Fact]
    public async Task Ask_TenantIsolation_EachBusinessRespectsItsOwnFlag()
    {
        var (_, _, ownerClientA, ownerCookieA) = await SetUpBusinessWithOwnerAsync("Analyst Tenant A Disabled");
        using var _a = ownerClientA;
        var (_, _, ownerClientB, ownerCookieB) = await SetUpBusinessWithOwnerAsync("Analyst Tenant B Enabled");
        using var _b = ownerClientB;
        await EnableAnalystAsync(ownerClientB, ownerCookieB);

        var responseA = await ownerClientA.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookieA), new AnalystQuestionRequest("Anything?")));
        var responseB = await ownerClientB.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookieB), new AnalystQuestionRequest("Anything?")));

        Assert.Equal(HttpStatusCode.Forbidden, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
    }

    // ── Zero-tool selection (the factory's default fake) — Refused ──────

    [Fact]
    public async Task Ask_WhenEnabled_DefaultFakeSelectsNoTools_Returns200_WithRefusedOutcome()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Refused Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie),
            new AnalystQuestionRequest("What is the meaning of life?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalystAnswerResponse>();
        Assert.Equal("Refused", body!.Outcome);
        Assert.NotEmpty(body.Message);

        var messageService = Assert.IsType<FakeMessageService>(_factory.Services.GetRequiredService<IMessageService>());
        Assert.Equal(1, messageService.CallCount); // selection call only — no synthesis call for a refusal
    }

    // ── Authorization: AnyStaff (design §6) ──────────────────────────────

    public static IEnumerable<object[]> AllStaffRoles() => RoleNames.All.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(AllStaffRoles))]
    public async Task Ask_AnyStaffRole_CanCall(string role)
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync($"Analyst AnyStaff {role} Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        await SeedStaffIntoBusinessAsync(businessId, email, role);
        using var staffClient = NewClient();
        var (_, _, staffCookie) = await LoginAsync(staffClient, email);

        var response = await staffClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", staffCookie!), new AnalystQuestionRequest("Anything?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ask_Unauthenticated_Returns401()
    {
        using var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/analyst", new AnalystQuestionRequest("Anything?"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Request validation ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ask_BlankQuestion_Returns400(string blankQuestion)
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Blank Question Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest(blankQuestion)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Outcome-to-status mapping, scripted via SequencedFakeMessageService ─

    [Fact]
    public async Task Ask_Answered_Returns200_WithSynthesizedText_NoCapabilityNamesInResponse()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Answered Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var (scriptedFactory, fake) = NewScriptedFactory();
        using var scriptedClient = scriptedFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        fake.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" })));
        const string synthesizedText = "There were no conversations in the last 7 days.";
        fake.Enqueue(TextResponse(synthesizedText));

        var response = await scriptedClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("How many conversations this week?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalystAnswerResponse>();
        Assert.Equal("Answered", body!.Outcome);
        Assert.Equal(synthesizedText, body.Message);
        Assert.Equal(2, fake.CallCount);

        // Design §5: capability names are never in the external response.
        var rawBody = await (await scriptedClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("How many conversations this week?"))))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("getConversationsByDay", rawBody);
    }

    [Fact]
    public async Task Ask_CapabilityFails_Returns500_WithCapabilityFailedOutcome()
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask CapabilityFailed Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var (scriptedFactory, fake) = NewScriptedFactory();
        using var scriptedClient = scriptedFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        // An invalid range makes the real GetConversationsByDayCapability
        // return ToolExecutionResult.Error — a real capability failure, not
        // a synthetic fake, proving the real Phase 3 validation path is
        // actually wired all the way through to this HTTP response.
        fake.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "99d" })));

        var response = await scriptedClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("How many conversations?")));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalystAnswerResponse>();
        Assert.Equal("CapabilityFailed", body!.Outcome);
        Assert.Equal(1, fake.CallCount); // no synthesis call
    }

    [Theory]
    [MemberData(nameof(ClaudeFailureExceptions))]
    public async Task Ask_ClaudeCallFails_Returns502_WithClaudeCallFailedOutcome(Exception exception)
    {
        var (_, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask ClaudeCallFailed Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var (scriptedFactory, fake) = NewScriptedFactory();
        using var scriptedClient = scriptedFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        fake.ExceptionToThrow = exception;

        var response = await scriptedClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("Anything?")));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AnalystAnswerResponse>();
        Assert.Equal("ClaudeCallFailed", body!.Outcome);
    }

    // ── T-API1 (A1 Slice 4b design §10.4): invocation capture over HTTP ─
    // Mutation: revert A4 (and A5) in AnalystOrchestrationService to the raw
    // IMessageService.Create path — no row is captured, so both go red.

    [Fact]
    public async Task Ask_WhenEnabled_DefaultFake_CapturesOneAnalystSelectionRow()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Capture Refused Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var response = await ownerClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("Anything?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(_factory.InvocationCapture.ForBusiness(businessId));
        Assert.Equal(AiOperations.AnalystSelection, record.Operation);
    }

    [Fact]
    public async Task Ask_ScriptedAnswered_CapturesTwoRowsUnderOneCorrelation()
    {
        var (businessId, _, ownerClient, ownerCookie) = await SetUpBusinessWithOwnerAsync("Analyst Ask Capture Answered Business");
        using var _o = ownerClient;
        await EnableAnalystAsync(ownerClient, ownerCookie);

        var (scriptedFactory, fake) = NewScriptedFactory();
        using var scriptedClient = scriptedFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        fake.Enqueue(ToolUseResponse("getConversationsByDay", Args(new { range = "7d" })));
        fake.Enqueue(TextResponse("There were no conversations in the last 7 days."));

        var response = await scriptedClient.SendAsync(WithBody(
            Authed(HttpMethod.Post, "/api/analyst", ownerCookie), new AnalystQuestionRequest("How many conversations this week?")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = _factory.InvocationCapture.ForBusiness(businessId);
        Assert.Equal(2, records.Count);
        Assert.Equal(records[0].CorrelationId, records[1].CorrelationId);
        Assert.Equal(AiOperations.AnalystSelection, records[0].Operation);
        Assert.Equal(AiOperations.AnalystSynthesis, records[1].Operation);
    }

    public static IEnumerable<object[]> ClaudeFailureExceptions()
    {
        yield return [new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" }];
        yield return [new Anthropic5xxException { StatusCode = HttpStatusCode.InternalServerError, ResponseBody = "{}" }];
    }
}
