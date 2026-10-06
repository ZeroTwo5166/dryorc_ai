using System.Net;
using System.Net.Http.Json;
using backend.Controllers;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// API-level tests for the Step 21 (OTP half) widget verification endpoints
/// — real HTTP pipeline via NivoraApiFactory, same convention as
/// WidgetAuthenticationTests. FakeEmailSender substitutes for the real
/// Resend-backed IEmailSender.
/// </summary>
public class WidgetVerificationApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<Widget> CreateBusinessAndWidgetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Verification Test {Guid.NewGuid():N}",
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

        return widget;
    }

    private async Task<(string Token, Guid ConversationId)> InitAndStartConversationAsync(HttpClient client, Widget widget)
    {
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(initBody);

        var messageResponse = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("Hello")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessId == widget.BusinessId
                && c.WidgetId == widget.Id
                && c.VisitorSessionId == initBody.VisitorSessionId);

        return (initBody.Token, conversation.Id);
    }

    private FakeEmailSender EmailSender => (FakeEmailSender)_factory.Services.GetRequiredService<backend.Email.IEmailSender>();

    private static string ExtractCode(FakeEmailSender sender, string to)
    {
        var email = sender.SentEmails.Last(e => e.To == to);
        var start = email.HtmlBody.IndexOf("<strong>", StringComparison.Ordinal) + "<strong>".Length;
        var end = email.HtmlBody.IndexOf("</strong>", StringComparison.Ordinal);
        return email.HtmlBody[start..end];
    }

    [Fact]
    public async Task RequestVerificationCode_ValidRequest_ReturnsOkWithSentTrue()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);
        var email = $"valid-{Guid.NewGuid():N}@example.com";

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationRequestResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Sent);
    }

    [Fact]
    public async Task RequestVerificationCode_MalformedEmail_ReturnsBadRequest()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, "not-an-email")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequestVerificationCode_UnknownConversation_ReturnsNotFound()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, _) = await InitAndStartConversationAsync(client, widget);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(Guid.NewGuid(), "someone@example.com")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestVerificationCode_NoBearerToken_ReturnsUnauthorized()
    {
        using var client = NewClient();

        var response = await client.PostAsJsonAsync(
            "/api/widget/verification/request", new WidgetVerificationRequestRequest(Guid.NewGuid(), "someone@example.com"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequestVerificationCode_ConversationBelongsToDifferentVisitorSession_ReturnsNotFound()
    {
        var widget = await CreateBusinessAndWidgetAsync();

        using var ownerClient = NewClient();
        var (_, conversationId) = await InitAndStartConversationAsync(ownerClient, widget);

        // A second, independent widget session (fresh Init => fresh
        // VisitorSessionId) tries to act on the first session's conversation.
        using var attackerClient = NewClient();
        var attackerInit = await attackerClient.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        var attackerBody = await attackerInit.Content.ReadFromJsonAsync<WidgetInitResponse>();

        var response = await attackerClient.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", attackerBody!.Token),
            new WidgetVerificationRequestRequest(conversationId, "someone@example.com")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RequestVerificationCode_ZeroMatchAndSingleMatch_ReturnIdenticalResponseShape()
    {
        var widget = await CreateBusinessAndWidgetAsync();

        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Customers.Add(new Customer
            {
                Id = Guid.NewGuid(),
                BusinessId = widget.BusinessId,
                Email = $"existing-{Guid.NewGuid():N}@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        using var zeroMatchClient = NewClient();
        var (zeroToken, zeroConversationId) = await InitAndStartConversationAsync(zeroMatchClient, widget);
        var zeroMatchResponse = await zeroMatchClient.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", zeroToken),
            new WidgetVerificationRequestRequest(zeroConversationId, $"brand-new-{Guid.NewGuid():N}@example.com")));

        using var multiMatchClient = NewClient();
        var (multiToken, multiConversationId) = await InitAndStartConversationAsync(multiMatchClient, widget);
        var multiEmail = $"multi-{Guid.NewGuid():N}@example.com";
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.Customers.Add(new Customer { Id = Guid.NewGuid(), BusinessId = widget.BusinessId, Email = multiEmail, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            seedDb.Customers.Add(new Customer { Id = Guid.NewGuid(), BusinessId = widget.BusinessId, Email = multiEmail, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await seedDb.SaveChangesAsync();
        }
        var multiMatchResponse = await multiMatchClient.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", multiToken),
            new WidgetVerificationRequestRequest(multiConversationId, multiEmail)));

        Assert.Equal(zeroMatchResponse.StatusCode, multiMatchResponse.StatusCode);
        var zeroBody = await zeroMatchResponse.Content.ReadFromJsonAsync<WidgetVerificationRequestResponse>();
        var multiBody = await multiMatchResponse.Content.ReadFromJsonAsync<WidgetVerificationRequestResponse>();
        Assert.Equal(zeroBody, multiBody);
    }

    [Fact]
    public async Task RequestVerificationCode_SecondRequestWithinCooldown_ReturnsTooManyRequests()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);
        var email = $"throttle-{Guid.NewGuid():N}@example.com";

        var first = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task VerifyVerificationCode_CorrectCode_ReturnsVerifiedTrue()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);
        var email = $"verify-api-{Guid.NewGuid():N}@example.com";

        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));
        var code = ExtractCode(EmailSender, email);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", token),
            new WidgetVerificationVerifyRequest(conversationId, email, code)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationVerifyResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Verified);
        Assert.Null(body.Reason);
    }

    [Fact]
    public async Task VerifyVerificationCode_WrongCode_ReturnsVerifiedFalseWithReason()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);
        var email = $"verify-wrong-api-{Guid.NewGuid():N}@example.com";

        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));
        var realCode = ExtractCode(EmailSender, email);
        var wrongCode = realCode == "000000" ? "111111" : "000000";

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", token),
            new WidgetVerificationVerifyRequest(conversationId, email, wrongCode)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationVerifyResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Verified);
        Assert.Equal("CodeMismatch", body.Reason);
    }

    [Fact]
    public async Task VerifyVerificationCode_NoCodeEverRequested_ReturnsVerifiedFalseNotFound()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", token),
            new WidgetVerificationVerifyRequest(conversationId, "nobody@example.com", "123456")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationVerifyResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Verified);
        Assert.Equal("NotFound", body.Reason);
    }

    /// <summary>
    /// Test G — identity-binding/erasure-integrity design (Decision 2;
    /// implementation plan Phase 7). Confirms
    /// WidgetController.VerifyVerificationCode's existing, unmodified
    /// result.Reason.ToString() forwarding surfaces the new
    /// CustomerErasureInProgress reason exactly like every other reason —
    /// no controller change is needed or made for this.
    /// </summary>
    [Fact]
    public async Task VerifyVerificationCode_ErasureInProgress_ReturnsVerifiedFalseWithCustomerErasureInProgressReason()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);
        var email = $"verify-erasure-api-{Guid.NewGuid():N}@example.com";

        await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", token),
            new WidgetVerificationRequestRequest(conversationId, email)));
        var code = ExtractCode(EmailSender, email);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Customers.IgnoreQueryFilters()
                .Where(c => c.BusinessId == widget.BusinessId && c.Email == email)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingErasureAt, DateTimeOffset.UtcNow));
        }

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", token),
            new WidgetVerificationVerifyRequest(conversationId, email, code)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationVerifyResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Verified);
        Assert.Equal("CustomerErasureInProgress", body.Reason);
    }

    [Fact]
    public async Task VerifyVerificationCode_MalformedRequest_ReturnsBadRequest()
    {
        var widget = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var (token, conversationId) = await InitAndStartConversationAsync(client, widget);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", token),
            new WidgetVerificationVerifyRequest(conversationId, "not-an-email", "123456")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task VerifyVerificationCode_NoBearerToken_ReturnsUnauthorized()
    {
        var response = await NewClient().SendAsync(WithJsonBody(
            new HttpRequestMessage(HttpMethod.Post, "/api/widget/verification/verify"),
            new WidgetVerificationVerifyRequest(Guid.NewGuid(), "someone@example.com", "123456")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
