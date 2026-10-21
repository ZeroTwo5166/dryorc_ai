using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using backend.Auth;
using backend.Constants;
using backend.Controllers;
using backend.Data;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5c: the release switch at the HTTP edge (DEC D7, I7,
/// erratum E2; docs/handoffs/staff-invite-design.md Section 8 "Switch check"
/// and Section 10; Section T rows T-I7a to T-I7g).
///
/// The switched-off host is built once, in SwitchedOffHost, with the
/// StaffInvites Enabled setting false and a spy IStaffInvitationService that
/// records every call; T-I7a to T-I7e and T-I7g use it. T-I7f uses its own
/// host with a private mutable IStaffInviteSwitch and the real service.
/// </summary>
public class StaffInvitationSwitchOffTests : IAsyncLifetime
{
    private const string Password = "Passw0rd!!";

    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];
    private readonly SpyStaffInvitationService _spy = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // T-I7a
    [Fact]
    public async Task Anonymous_404()
    {
        var host = SwitchedOffHost();

        var results = await RunAnonymousCasesAsync(host);

        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.NotFound, $"{r.Case}: {(int)r.Status}"));
    }

    // T-I7b
    [Fact]
    public async Task Staff_404_ForOwnerAdminAgent()
    {
        var host = SwitchedOffHost();

        var results = await RunStaffCasesAsync(host);

        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.NotFound, $"{r.Case}: {(int)r.Status}"));
    }

    // T-I7c
    [Fact]
    public async Task Staff_401_Unauthenticated()
    {
        var host = SwitchedOffHost();
        var id = Guid.NewGuid();

        var results = new List<CaseResult>
        {
            await SendAsync(host, "create", HttpMethod.Post, "/api/staff-invitations", null,
                JsonContent.Create(new CreateStaffInvitationRequest(NewEmail(), RoleNames.Agent))),
            await SendAsync(host, "list", HttpMethod.Get, "/api/staff-invitations", null),
            await SendAsync(host, "resend", HttpMethod.Post, $"/api/staff-invitations/{id}/resend", null),
            await SendAsync(host, "revoke", HttpMethod.Delete, $"/api/staff-invitations/{id}", null),
        };

        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.Unauthorized, $"{r.Case}: {(int)r.Status}"));
    }

    // T-I7d: well over both limits, and still no 429.
    [Fact]
    public async Task OverLimit_Still404()
    {
        var host = SwitchedOffHost();
        var businessId = await SeedBusinessAsync(host);
        var owner = await SeedLoggedInStaffAsync(host, businessId, RoleNames.Owner);

        var results = new List<CaseResult>();
        for (var i = 0; i < 25; i++)
        {
            results.Add(await SendAsync(host, $"preview {i + 1}", HttpMethod.Post, "/api/staff-invitations/preview", null,
                JsonContent.Create(new PreviewStaffInvitationRequest(NewWellFormedToken()))));
        }

        for (var i = 0; i < 25; i++)
        {
            results.Add(await SendAsync(host, $"create {i + 1}", HttpMethod.Post, "/api/staff-invitations", owner.Cookie,
                JsonContent.Create(new CreateStaffInvitationRequest(NewEmail(), RoleNames.Agent))));
        }

        Assert.Equal(50, results.Count);
        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.NotFound, $"{r.Case}: {(int)r.Status}"));
    }

    // T-I7e
    [Fact]
    public async Task MemberList_StillAvailable_MeFlagFalse()
    {
        var host = SwitchedOffHost();
        var businessId = await SeedBusinessAsync(host);
        var agent = await SeedLoggedInStaffAsync(host, businessId, RoleNames.Agent);

        var members = await SendAsync(host, "members", HttpMethod.Get, "/api/team/members", agent.Cookie);
        Assert.Equal(HttpStatusCode.OK, members.Status);
        var list = System.Text.Json.JsonSerializer.Deserialize<List<TeamMemberResponse>>(members.Body, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Contains(list!, m => m.UserId == agent.UserId);

        var me = await SendAsync(host, "me", HttpMethod.Get, "/api/auth/me", agent.Cookie);
        Assert.Equal(HttpStatusCode.OK, me.Status);
        var meBody = System.Text.Json.JsonSerializer.Deserialize<MeResponse>(me.Body, System.Text.Json.JsonSerializerOptions.Web);
        Assert.False(meBody!.StaffInvitesEnabled);
    }

    // T-I7f: a limiter filled while on is not reused once the switch is off.
    [Fact]
    public async Task FlipOff_AfterLimitHit_Gives404Not429()
    {
        var staffInviteSwitch = new MutableStaffInviteSwitch { IsEnabled = true };
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStaffInviteSwitch>(staffInviteSwitch)));
        using var client = NewClient(host);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            var response = await client.PostAsJsonAsync("/api/staff-invitations/preview", new PreviewStaffInvitationRequest(NewWellFormedToken()));
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);

        staffInviteSwitch.IsEnabled = false;
        var afterFlip = await client.PostAsJsonAsync("/api/staff-invitations/preview", new PreviewStaffInvitationRequest(NewWellFormedToken()));

        Assert.Equal(HttpStatusCode.NotFound, afterFlip.StatusCode);
    }

    // T-I7g: across every T-I7a and T-I7b request, no invitation logic ran.
    [Fact]
    public async Task NoInvitationLogicReached()
    {
        var host = SwitchedOffHost();

        var results = new List<CaseResult>();
        results.AddRange(await RunAnonymousCasesAsync(host));
        results.AddRange(await RunStaffCasesAsync(host));

        Assert.NotEmpty(results);
        Assert.Equal(0, _spy.Calls);
    }

    // ── Harness (private, per-file doubles; CustomersApiTests.cs convention) ──

    private sealed record CaseResult(string Case, HttpStatusCode Status, string Body);

    private sealed record StaffLogin(Guid UserId, string Email, string Cookie);

    /// <summary>T-I7f: a switch the test can flip while the host runs.</summary>
    private sealed class MutableStaffInviteSwitch : IStaffInviteSwitch
    {
        private volatile bool _isEnabled;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => _isEnabled = value;
        }
    }

    /// <summary>
    /// T-I7g: records every call and then fails it, so a request that reached
    /// the service could not pass as a 404 either.
    /// </summary>
    private sealed class SpyStaffInvitationService : IStaffInvitationService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CreateResult> CreateAsync(StaffCaller caller, string email, string role, CancellationToken ct) => Record<CreateResult>();

        public Task<ListResult> ListAsync(StaffCaller caller, CancellationToken ct) => Record<ListResult>();

        public Task<ResendResult> ResendAsync(StaffCaller caller, Guid id, CancellationToken ct) => Record<ResendResult>();

        public Task<RevokeResult> RevokeAsync(StaffCaller caller, Guid id, CancellationToken ct) => Record<RevokeResult>();

        public Task<PreviewResult> PreviewAsync(string token, CancellationToken ct) => Record<PreviewResult>();

        public Task<AcceptResult> AcceptAsync(string token, string password, CancellationToken ct) => Record<AcceptResult>();

        private Task<T> Record<T>()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The staff invitation service was reached while the switch is off.");
        }
    }

    /// <summary>The one switched-off host of this class (erratum 1 E1-1, erratum 2 E2-3).</summary>
    private WebApplicationFactory<Program> SwitchedOffHost() =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting($"{StaffInviteOptions.SectionName}:Enabled", "false");
            b.ConfigureTestServices(s => s.AddScoped<IStaffInvitationService>(_ => _spy));
        });

    /// <summary>T-I7a: preview and accept with every kind of body.</summary>
    private static async Task<List<CaseResult>> RunAnonymousCasesAsync(WebApplicationFactory<Program> host)
    {
        var results = new List<CaseResult>();
        foreach (var endpoint in new[] { "preview", "accept" })
        {
            var url = $"/api/staff-invitations/{endpoint}";
            object Body(string token) => endpoint == "preview"
                ? new PreviewStaffInvitationRequest(token)
                : new AcceptStaffInvitationRequest(token, Password);

            results.Add(await SendAsync(host, $"{endpoint} valid-looking", HttpMethod.Post, url, null,
                JsonContent.Create(Body(NewWellFormedToken()), Body("x").GetType())));
            results.Add(await SendAsync(host, $"{endpoint} invalid", HttpMethod.Post, url, null,
                JsonContent.Create(Body("not-a-token"), Body("x").GetType())));
            results.Add(await SendAsync(host, $"{endpoint} empty body", HttpMethod.Post, url, null,
                new StringContent("", Encoding.UTF8, "application/json")));
            results.Add(await SendAsync(host, $"{endpoint} malformed JSON", HttpMethod.Post, url, null,
                new StringContent("{\"token\":", Encoding.UTF8, "application/json")));
            results.Add(await SendAsync(host, $"{endpoint} text/plain", HttpMethod.Post, url, null,
                new StringContent("token", Encoding.UTF8, "text/plain")));
        }

        return results;
    }

    /// <summary>T-I7b: the four staff actions as Owner, Admin and Agent, with good and malformed bodies.</summary>
    private async Task<List<CaseResult>> RunStaffCasesAsync(WebApplicationFactory<Program> host)
    {
        var businessId = await SeedBusinessAsync(host);
        var results = new List<CaseResult>();
        foreach (var role in new[] { RoleNames.Owner, RoleNames.Admin, RoleNames.Agent })
        {
            var login = await SeedLoggedInStaffAsync(host, businessId, role);
            var id = Guid.NewGuid();
            results.Add(await SendAsync(host, $"{role} create", HttpMethod.Post, "/api/staff-invitations", login.Cookie,
                JsonContent.Create(new CreateStaffInvitationRequest(NewEmail(), RoleNames.Agent))));
            results.Add(await SendAsync(host, $"{role} create malformed JSON", HttpMethod.Post, "/api/staff-invitations", login.Cookie,
                new StringContent("{\"email\":", Encoding.UTF8, "application/json")));
            results.Add(await SendAsync(host, $"{role} create text/plain", HttpMethod.Post, "/api/staff-invitations", login.Cookie,
                new StringContent("email", Encoding.UTF8, "text/plain")));
            results.Add(await SendAsync(host, $"{role} list", HttpMethod.Get, "/api/staff-invitations", login.Cookie));
            results.Add(await SendAsync(host, $"{role} resend", HttpMethod.Post, $"/api/staff-invitations/{id}/resend", login.Cookie));
            results.Add(await SendAsync(host, $"{role} revoke", HttpMethod.Delete, $"/api/staff-invitations/{id}", login.Cookie));
        }

        return results;
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<CaseResult> SendAsync(
        WebApplicationFactory<Program> host, string name, HttpMethod method, string url, string? cookie, HttpContent? content = null)
    {
        using var client = NewClient(host);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        using var response = await client.SendAsync(request);
        return new CaseResult(name, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string NewEmail() => $"staff-invite-{Guid.NewGuid():N}@example.test";

    /// <summary>43 base64url characters that decode to 32 bytes, matching no row.</summary>
    private static string NewWellFormedToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private async Task<Guid> SeedBusinessAsync(WebApplicationFactory<Program> host)
    {
        var businessId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Businesses.Add(new Business
            {
                Id = businessId,
                Name = $"Invite Co {businessId:N}",
                TimeZone = "Europe/Copenhagen",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    /// <summary>BusinessApiTests.SeedStaffIntoBusinessAsync and LoginAsync shapes.</summary>
    private static async Task<StaffLogin> SeedLoggedInStaffAsync(WebApplicationFactory<Program> host, Guid businessId, string role)
    {
        var email = NewEmail();
        var userId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = userId,
                BusinessId = businessId,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var createResult = await userManager.CreateAsync(user, Password);
            Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
            var roleResult = await userManager.AddToRoleAsync(user, role);
            Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        }

        using var client = NewClient(host);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new StaffLogin(userId, email, response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
    }
}
