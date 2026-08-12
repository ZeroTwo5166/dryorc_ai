using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 4 cross-tenant sanity test, run against the real app + real Neon
/// dev DB (never prod) via NivoraApiFactory. Exercises the real HTTP
/// pipeline end to end: cookie auth, TenantAwareUserStore's login-lookup
/// override, ClaimsCurrentTenantAccessor, the global EF Core query filter,
/// and the SameTenant/role authorization policies — not just unit-level
/// reasoning about any one of them in isolation.
///
/// All test data is cleaned up in DisposeAsync via raw SQL (IgnoreQueryFilters
/// doesn't apply to raw SQL, and no authenticated HttpContext exists at
/// cleanup time anyway) so this suite leaves the dev DB exactly as it found it.
/// </summary>
public class CrossTenantIsolationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count == 0)
        {
            await _factory.DisposeAsync();
            return;
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = _businessIdsToClean.ToArray();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Customers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids}))");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY({ids})");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");

        await _factory.DisposeAsync();
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpResponseMessage Response, RegisterResponse? Body)> RegisterAsync(
        HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, password));
        var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterResponse>() : null;
        return (response, body);
    }

    /// <summary>Logs in and returns the raw Set-Cookie value to attach to later requests — WebApplicationFactory's HttpClient does not auto-manage cookies.</summary>
    private static async Task<(HttpResponseMessage Response, LoginResponse? Body, string? Cookie)> LoginAsync(
        HttpClient client, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        LoginResponse? body = null;
        string? cookie = null;
        if (response.IsSuccessStatusCode)
        {
            body = await response.Content.ReadFromJsonAsync<LoginResponse>();
            cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.First().Split(';')[0]
                : null;
        }
        return (response, body, cookie);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    [Fact]
    public async Task CrossTenantIsolation_FullScenario()
    {
        // ---- Setup: Business A + Staff User A, Business B + Staff User B ----
        var emailA = $"staffA-{Guid.NewGuid():N}@example.com";
        var emailB = $"staffB-{Guid.NewGuid():N}@example.com";

        using var anonClient = NewClient();

        var (registerAResponse, registerABody) = await RegisterAsync(anonClient, "Business A", emailA);
        Assert.Equal(HttpStatusCode.OK, registerAResponse.StatusCode);
        Assert.NotNull(registerABody);
        _businessIdsToClean.Add(registerABody!.BusinessId);

        var (registerBResponse, registerBBody) = await RegisterAsync(anonClient, "Business B", emailB);
        Assert.Equal(HttpStatusCode.OK, registerBResponse.StatusCode);
        Assert.NotNull(registerBBody);
        _businessIdsToClean.Add(registerBBody!.BusinessId);

        Assert.NotEqual(registerABody.BusinessId, registerBBody.BusinessId);

        // ---- "User A can authenticate." / "User B can authenticate." ----
        using var clientA = NewClient();
        using var clientB = NewClient();

        var (loginAResponse, loginABody, cookieA) = await LoginAsync(clientA, emailA);
        Assert.Equal(HttpStatusCode.OK, loginAResponse.StatusCode);
        Assert.NotNull(loginABody);
        Assert.NotNull(cookieA);

        var (loginBResponse, loginBBody, cookieB) = await LoginAsync(clientB, emailB);
        Assert.Equal(HttpStatusCode.OK, loginBResponse.StatusCode);
        Assert.NotNull(loginBBody);
        Assert.NotNull(cookieB);

        // ---- "After User A logs in, ICurrentTenantAccessor returns Business A's ID." (and B) ----
        // The login response itself is built from the authenticated principal
        // via UserManager after PasswordSignInAsync succeeds, so this is a
        // direct assertion on the resolved tenant, not an inference.
        Assert.Equal(registerABody.BusinessId, loginABody!.BusinessId);
        Assert.Equal(registerBBody.BusinessId, loginBBody!.BusinessId);
        Assert.Contains(RoleNames.Owner, loginABody.Roles);
        Assert.Contains(RoleNames.Owner, loginBBody.Roles);

        // ---- "User A can access Business A's protected resources." (create + list + get) ----
        var createAResponse = await clientA.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/customers", cookieA!),
            new CreateCustomerRequest("customerA@example.com", "Customer A")));
        Assert.Equal(HttpStatusCode.Created, createAResponse.StatusCode);
        var customerA = await createAResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(customerA);
        Assert.Equal(registerABody.BusinessId, customerA!.BusinessId); // never client-supplied — client sent no BusinessId at all

        var createBResponse = await clientB.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/customers", cookieB!),
            new CreateCustomerRequest("customerB@example.com", "Customer B")));
        Assert.Equal(HttpStatusCode.Created, createBResponse.StatusCode);
        var customerB = await createBResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(customerB);
        Assert.Equal(registerBBody.BusinessId, customerB!.BusinessId);

        // ---- "the tenant accessor correctly scopes subsequent queries to that business" ----
        var listAResponse = await clientA.SendAsync(Authed(HttpMethod.Get, "/api/customers", cookieA!));
        Assert.Equal(HttpStatusCode.OK, listAResponse.StatusCode);
        var listA = await listAResponse.Content.ReadFromJsonAsync<List<CustomerResponse>>();
        Assert.NotNull(listA);
        Assert.Contains(listA!, c => c.Id == customerA.Id);
        Assert.DoesNotContain(listA!, c => c.Id == customerB.Id); // Business B's customer must not leak into A's list

        var listBResponse = await clientB.SendAsync(Authed(HttpMethod.Get, "/api/customers", cookieB!));
        Assert.Equal(HttpStatusCode.OK, listBResponse.StatusCode);
        var listB = await listBResponse.Content.ReadFromJsonAsync<List<CustomerResponse>>();
        Assert.NotNull(listB);
        Assert.Contains(listB!, c => c.Id == customerB.Id);
        Assert.DoesNotContain(listB!, c => c.Id == customerA.Id);

        // ---- "Direct resource IDs belonging to another business cannot be used to retrieve ... that resource." ----
        var aGetsBsCustomer = await clientA.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerB.Id}", cookieA!));
        Assert.Equal(HttpStatusCode.NotFound, aGetsBsCustomer.StatusCode);

        var bGetsAsCustomer = await clientB.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerA.Id}", cookieB!));
        Assert.Equal(HttpStatusCode.NotFound, bGetsAsCustomer.StatusCode);

        // A can still get its own resource by ID (proves the 404 above is tenant isolation, not a broken endpoint)
        var aGetsOwnCustomer = await clientA.SendAsync(Authed(HttpMethod.Get, $"/api/customers/{customerA.Id}", cookieA!));
        Assert.Equal(HttpStatusCode.OK, aGetsOwnCustomer.StatusCode);

        // ---- "Supplying another BusinessId manually in the request cannot bypass tenant isolation." ----
        // CreateCustomerRequest has no BusinessId property at all, so this
        // sends one as an extra, unmapped raw JSON field to confirm it's
        // silently ignored rather than honored.
        var spoofRequest = Authed(HttpMethod.Post, "/api/customers", cookieA!);
        spoofRequest.Content = JsonContent.Create(new
        {
            email = "spoofed@example.com",
            name = "Spoofed Customer",
            businessId = registerBBody.BusinessId, // attempted spoof — must be ignored
        });
        var spoofResponse = await clientA.SendAsync(spoofRequest);
        Assert.Equal(HttpStatusCode.Created, spoofResponse.StatusCode);
        var spoofedCustomer = await spoofResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(spoofedCustomer);
        Assert.Equal(registerABody.BusinessId, spoofedCustomer!.BusinessId); // landed in A, not the spoofed B

        // ---- "Unauthenticated requests cannot access protected endpoints." ----
        using var anonRequestClient = NewClient();
        var unauthedList = await anonRequestClient.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthedList.StatusCode);

        var unauthedCreate = await anonRequestClient.PostAsJsonAsync("/api/customers", new CreateCustomerRequest("x@example.com", "X"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthedCreate.StatusCode);

        // ---- Bonus: role permission enforcement (checklist item 4 — "does the user's role permit the operation") ----
        // Registration only ever produces an Owner. Seed an Agent directly
        // via UserManager (legitimate test-only use — not a route through
        // the tenant-filtered login path) to prove OwnerOrAdmin actually
        // rejects a role outside that set, not just Owner/Admin passing.
        var agentEmail = $"agentA-{Guid.NewGuid():N}@example.com";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var agent = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                BusinessId = registerABody.BusinessId,
                UserName = agentEmail,
                Email = agentEmail,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var createResult = await userManager.CreateAsync(agent, "Passw0rd!!");
            Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
            var roleResult = await userManager.AddToRoleAsync(agent, RoleNames.Agent);
            Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        }

        using var agentClient = NewClient();
        var (agentLoginResponse, agentLoginBody, agentCookie) = await LoginAsync(agentClient, agentEmail);
        Assert.Equal(HttpStatusCode.OK, agentLoginResponse.StatusCode);
        Assert.Contains(RoleNames.Agent, agentLoginBody!.Roles);

        // Agent CAN read (AnyStaff policy)...
        var agentList = await agentClient.SendAsync(Authed(HttpMethod.Get, "/api/customers", agentCookie!));
        Assert.Equal(HttpStatusCode.OK, agentList.StatusCode);

        // ...but CANNOT create (OwnerOrAdmin policy) — authentication succeeded, role forbids the operation.
        var agentCreateAttempt = await agentClient.SendAsync(WithBody(Authed(HttpMethod.Post, "/api/customers", agentCookie!),
            new CreateCustomerRequest("blocked@example.com", "Should Be Blocked")));
        Assert.Equal(HttpStatusCode.Forbidden, agentCreateAttempt.StatusCode);
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }
}
