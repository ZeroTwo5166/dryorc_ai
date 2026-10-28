using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Email;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>
/// Trace-redaction (D-3, D-4, D-5, D-13): one log-capture test per request
/// record with a hand-written PrintMembers. Each test runs on its own capture
/// host, clears the capture immediately before its one POST, and denies the
/// record's secret values over every entry captured since the clear. No test
/// prints anything; a failure names a label, an entry index and a length.
/// </summary>
public class RequestRecordRedactionApiTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    [Fact]
    public async Task Register_Password_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var email = NewEmail();
        var password = NewSecretPassword();
        using var client = NewClient(host);

        logs.Clear();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(NewName(), "Europe/Copenhagen", email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(body);
        _businessIdsToClean.Add(body!.BusinessId);
        var captured = logs.Entries;

        LogCaptureAssert.NoneContain(captured, new DeniedValue("RegisterRequest.Password", password));
    }

    [Fact]
    public async Task Login_Password_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var (_, _, email, password) = await SeedConfirmedOwnerAsync(host);
        using var client = NewClient(host);

        logs.Clear();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = logs.Entries;

        var found = captured.Any(e =>
            e.Category.Contains("ControllerActionInvoker", StringComparison.Ordinal)
            && e.Level == LogLevel.Trace
            && e.Message.Contains("LoginRequest", StringComparison.Ordinal)
            && e.Message.Contains(email, StringComparison.Ordinal));
        Assert.True(found, "positive control: no ControllerActionInvoker Trace entry holds LoginRequest and its Email");

        LogCaptureAssert.NoneContain(captured, new DeniedValue("LoginRequest.Password", password));
    }

    [Fact]
    public async Task ConfirmEmail_Token_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var registered = await RegisterAsync(host, NewEmail(), NewSecretPassword());
        var token = await EmailConfirmationTokenAsync(host, registered.UserId);
        using var client = NewClient(host);

        logs.Clear();
        var response = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(registered.UserId, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var captured = logs.Entries;

        LogCaptureAssert.NoneContain(captured, new DeniedValue("ConfirmEmailRequest.Token", token));
    }

    [Fact]
    public async Task ResetPassword_TokenAndNewPassword_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var (_, userId, _, _) = await SeedConfirmedOwnerAsync(host);
        var token = await PasswordResetTokenAsync(host, userId);
        var newPassword = NewSecretPassword();
        using var client = NewClient(host);

        logs.Clear();
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var captured = logs.Entries;

        LogCaptureAssert.NoneContain(
            captured,
            new DeniedValue("ResetPasswordRequest.Token", token),
            new DeniedValue("ResetPasswordRequest.NewPassword", newPassword));
    }

    [Fact]
    public async Task WidgetInit_SignedIdentityTokenAndVisitorSessionId_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var widget = await CreateBusinessAndWidgetAsync(host);
        var signedIdentityToken = NewSyntheticSignedIdentityToken();
        var visitorSessionId = NewSyntheticVisitorSessionId();
        using var client = NewClient(host);

        logs.Clear();
        var response = await client.PostAsJsonAsync(
            "/api/widget/init", new WidgetInitRequest(widget.PublicKey, visitorSessionId, signedIdentityToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = logs.Entries;

        LogCaptureAssert.NoneContain(
            captured,
            new DeniedValue("WidgetInitRequest.SignedIdentityToken", signedIdentityToken),
            new DeniedValue("WidgetInitRequest.VisitorSessionId", visitorSessionId));
    }

    [Fact]
    public async Task WidgetVerificationVerify_Code_NeverInCapturedLogs()
    {
        var logs = new CapturingLoggerProvider();
        var host = CapturingLoggerProvider.Attach(_factory, logs);
        var widget = await CreateBusinessAndWidgetAsync(host);
        using var client = NewClient(host);
        var (bearerToken, conversationId) = await InitAndStartConversationAsync(client, widget, host);
        var email = NewEmail();
        var requested = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/request", bearerToken),
            new WidgetVerificationRequestRequest(conversationId, email)));
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        var code = ExtractCode(EmailSender(host), email);

        logs.Clear();
        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/verification/verify", bearerToken),
            new WidgetVerificationVerifyRequest(conversationId, email, code)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetVerificationVerifyResponse>();
        Assert.True(body is not null && body.Verified, "the verify response is not Verified true");
        var captured = logs.Entries;

        var leak = captured.Any(e =>
            e.Category.Contains("ControllerActionInvoker", StringComparison.Ordinal)
            && e.Message.Contains("WidgetVerificationVerifyRequest", StringComparison.Ordinal)
            && e.Message.Contains(code, StringComparison.Ordinal));
        Assert.False(leak, "the code is in the invoker argument line of WidgetVerificationVerifyRequest");

        LogCaptureAssert.NoneContain(captured, new DeniedValue("WidgetVerificationVerifyRequest.Code", code, AllowShort: true));
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string bearerToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        return request;
    }

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static string NewEmail() => "trace-redaction-" + Guid.NewGuid().ToString("N") + "@example.test";

    private static string NewName() => "Trace Redaction " + Guid.NewGuid().ToString("N");

    private static string NewSecretPassword() => "Aa1#" + Guid.NewGuid().ToString("N");

    private static string NewSyntheticSignedIdentityToken() => "sit+" + Guid.NewGuid().ToString("N");

    private static string NewSyntheticVisitorSessionId() => "vsid+" + Guid.NewGuid().ToString("N");

    private async Task<(Guid BusinessId, Guid UserId, string Email, string Password)> SeedConfirmedOwnerAsync(WebApplicationFactory<Program> host)
    {
        var businessId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Businesses.Add(new Business
            {
                Id = businessId,
                Name = NewName(),
                TimeZone = "Europe/Copenhagen",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _businessIdsToClean.Add(businessId);

        var email = NewEmail();
        var password = NewSecretPassword();
        await using (var scope = host.Services.CreateAsyncScope())
        {
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
            var createResult = await userManager.CreateAsync(user, password);
            Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
            var roleResult = await userManager.AddToRoleAsync(user, RoleNames.Owner);
            Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            return (businessId, user.Id, email, password);
        }
    }

    private async Task<RegisterResponse> RegisterAsync(WebApplicationFactory<Program> host, string email, string password)
    {
        using var client = NewClient(host);
        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(NewName(), "Europe/Copenhagen", email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(body);
        _businessIdsToClean.Add(body!.BusinessId);
        return body;
    }

    private static async Task<string> EmailConfirmationTokenAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        return await userManager.GenerateEmailConfirmationTokenAsync(user!);
    }

    private static async Task<string> PasswordResetTokenAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        return await userManager.GeneratePasswordResetTokenAsync(user!);
    }

    private async Task<Widget> CreateBusinessAndWidgetAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = NewName(),
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

    private static async Task<(string Token, Guid ConversationId)> InitAndStartConversationAsync(
        HttpClient client, Widget widget, WebApplicationFactory<Program> host)
    {
        var initResponse = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(widget.PublicKey, null));
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var initBody = await initResponse.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(initBody);

        var messageResponse = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", initBody!.Token), new PostWidgetMessageRequest("Hello")));
        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessId == widget.BusinessId
                && c.WidgetId == widget.Id
                && c.VisitorSessionId == initBody.VisitorSessionId);

        return (initBody.Token, conversation.Id);
    }

    private static FakeEmailSender EmailSender(WebApplicationFactory<Program> host) =>
        (FakeEmailSender)host.Services.GetRequiredService<IEmailSender>();

    private static string ExtractCode(FakeEmailSender sender, string to)
    {
        var email = sender.SentEmails.Last(e => e.To == to);
        var start = email.HtmlBody.IndexOf("<strong>", StringComparison.Ordinal) + "<strong>".Length;
        var end = email.HtmlBody.IndexOf("</strong>", StringComparison.Ordinal);
        return email.HtmlBody[start..end];
    }
}
