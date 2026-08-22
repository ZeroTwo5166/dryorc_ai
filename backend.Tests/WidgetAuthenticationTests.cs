using System.Net;
using System.Net.Http.Json;
using backend.Auth;
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
/// Proves the two authentication schemes (staff cookie / IdentityConstants
/// .ApplicationScheme, and WidgetBearer) are genuinely independent — a real
/// HTTP-pipeline test in both directions, not a unit test on configuration
/// strings, per the spec's explicit requirement. Also proves
/// ClaimsCurrentTenantAccessor resolves BusinessId from the widget JWT
/// principal without any new tenant-resolution code, since the widget JWT
/// shares NivoraClaimTypes.BusinessId with the staff cookie principal.
/// </summary>
public class WidgetAuthenticationTests : IAsyncLifetime
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

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Messages\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Conversations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Widgets\" WHERE \"BusinessId\" = ANY({ids})");
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

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private async Task<(Business Business, Widget Widget)> CreateBusinessAndWidgetAsync(bool widgetActive = true)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = $"Widget Auth Test {Guid.NewGuid():N}",
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
            IsActive = widgetActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Businesses.Add(business);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(business.Id);

        return (business, widget);
    }

    private async Task<string> InitAndGetTokenAsync(HttpClient client, string publicKey)
    {
        var response = await client.PostAsJsonAsync("/api/widget/init", new WidgetInitRequest(publicKey, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WidgetInitResponse>();
        Assert.NotNull(body);
        return body!.Token;
    }

    [Fact]
    public async Task WidgetJwt_AuthenticatesWidgetEndpoint_AndTenantAccessorResolvesCorrectBusinessId()
    {
        var (business, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var token = await InitAndGetTokenAsync(client, widget.PublicKey);

        var response = await client.SendAsync(WithJsonBody(
            Bearer(HttpMethod.Post, "/api/widget/messages", token), new PostWidgetMessageRequest("Hello from a real widget JWT")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Proves ClaimsCurrentTenantAccessor resolved BusinessId correctly
        // for this WidgetBearer-authenticated request: the global tenant
        // query filter (keyed off that accessor) is what makes the
        // find-or-create query inside WidgetConversationService only ever
        // see this business's rows in the first place, but the stronger,
        // direct proof is that the persisted Message actually landed under
        // the right BusinessId — checked here via a raw, filter-bypassing
        // query, independent of whatever the accessor did.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = await db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Content == "Hello from a real widget JWT");
        Assert.Equal(business.Id, message.BusinessId);
    }

    [Fact]
    public async Task StaffCookie_DoesNotSatisfyWidgetPolicy()
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, "Widget Auth Negative Business", email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        using var client = NewClient();
        var (loginResponse, _, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // A valid staff cookie, no bearer token at all, against a
        // WidgetBearer-only endpoint.
        var response = await client.SendAsync(WithJsonBody(
            Authed(HttpMethod.Post, "/api/widget/messages", cookie!), new PostWidgetMessageRequest("should not work")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WidgetJwt_DoesNotSatisfyAnyStaff()
    {
        var (_, widget) = await CreateBusinessAndWidgetAsync();
        using var client = NewClient();
        var token = await InitAndGetTokenAsync(client, widget.PublicKey);

        // A valid widget JWT, no staff cookie at all, against an
        // AnyStaff-only endpoint.
        var response = await client.SendAsync(Bearer(HttpMethod.Get, "/api/customers", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExistingStaffAuthentication_StillWorksAfterWidgetBearerScheme()
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        using var anonClient = NewClient();
        var (registerResponse, registerBody) = await RegisterAsync(anonClient, "Widget Auth Regression Business", email);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        _businessIdsToClean.Add(registerBody!.BusinessId);
        await ConfirmEmailAsync(anonClient, registerBody.UserId);

        using var client = NewClient();
        var (loginResponse, loginBody, cookie) = await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.NotNull(loginBody);

        // The staff cookie still authenticates a real AnyStaff endpoint —
        // adding the WidgetBearer scheme did not disturb the default.
        var response = await client.SendAsync(Authed(HttpMethod.Get, "/api/customers", cookie!));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage WithJsonBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }
}
