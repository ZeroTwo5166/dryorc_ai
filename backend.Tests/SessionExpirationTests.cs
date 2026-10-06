using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Controllers;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Covers the absolute (non-sliding) 8-hour session + 2-hour extension model
/// added on top of Step 4's auth. Run against the real app + real Neon dev
/// DB via NivoraApiFactory, same as CrossTenantIsolationTests — the point is
/// proving the real cookie handler's expiry/validation behavior, not a
/// mocked stand-in for it.
/// </summary>
public class SessionExpirationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    private HttpClient NewClient() => _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task<RegisterResponse> RegisterAsync(HttpClient client, string businessName, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(businessName, "Europe/Copenhagen", email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(body);
        return body!;
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

    private static async Task<(LoginResponse Body, string Cookie)> LoginAsync(HttpClient client, string email, string password = "Passw0rd!!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : null;
        Assert.NotNull(cookie);
        return (body!, cookie!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    /// <summary>Registers + confirms + logs in a fresh staff user, tracked for cleanup.</summary>
    private async Task<(string Email, string Cookie)> NewSignedInUserAsync(HttpClient client, string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var register = await RegisterAsync(client, $"Business {label}", email);
        _businessIdsToClean.Add(register.BusinessId);
        await ConfirmEmailAsync(client, register.UserId);
        var (_, cookie) = await LoginAsync(client, email);
        return (email, cookie);
    }

    [Fact]
    public async Task Login_CreatesAnAbsoluteSessionMatchingTheConfiguredDuration()
    {
        // Reads the actual configured ExpireTimeSpan rather than hardcoding
        // "8 hours" a second time — Program.cs is the one place that value
        // should live, so this stays correct whatever it's currently set to
        // (e.g. temporarily shortened for manual testing) instead of drifting
        // out of sync with it.
        var optionsMonitor = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var configuredDuration = optionsMonitor.Get(IdentityConstants.ApplicationScheme).ExpireTimeSpan;

        using var client = NewClient();
        var (_, cookie) = await NewSignedInUserAsync(client, "absolute-session");

        var before = DateTimeOffset.UtcNow;
        var meResponse = await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", cookie));
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);

        // The ticket's ExpiresUtc is IssuedUtc + configuredDuration, and
        // IssuedUtc was set at login (strictly before `before`) — so
        // ExpiresAtUtc must land within [before, after] + configuredDuration,
        // not just "roughly" that far from now.
        Assert.InRange(
            me!.ExpiresAtUtc,
            before.Add(configuredDuration).AddSeconds(-30),
            after.Add(configuredDuration).AddSeconds(30));
    }

    [Fact]
    public void SlidingExpiration_IsDisabled()
    {
        var optionsMonitor = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var cookieOptions = optionsMonitor.Get(IdentityConstants.ApplicationScheme);

        Assert.False(cookieOptions.SlidingExpiration);
    }

    [Fact]
    public async Task ExpiredSession_IsRejected()
    {
        using var client = NewClient();
        var register = await RegisterAsync(client, "Business Expired", $"expired-{Guid.NewGuid():N}@example.com");
        _businessIdsToClean.Add(register.BusinessId);
        await ConfirmEmailAsync(client, register.UserId);

        // Craft a ticket identical in shape to a real one — same principal
        // factory, same TicketDataFormat/protection the app itself uses —
        // except with an ExpiresUtc already in the past, then present it as
        // the cookie on a real request. This exercises the real
        // CookieAuthenticationHandler validation path, not an assumption
        // about how it behaves.
        var optionsMonitor = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var cookieOptions = optionsMonitor.Get(IdentityConstants.ApplicationScheme);

        string expiredCookieValue;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var principalFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(register.UserId.ToString());
            Assert.NotNull(user);
            var principal = await principalFactory.CreateAsync(user!);

            var expiredTicket = new AuthenticationTicket(
                principal,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-5) },
                IdentityConstants.ApplicationScheme);

            expiredCookieValue = cookieOptions.TicketDataFormat.Protect(expiredTicket);
        }

        var response = await client.SendAsync(
            Authed(HttpMethod.Get, "/api/auth/me", $"{cookieOptions.Cookie.Name}={expiredCookieValue}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanExtendSession()
    {
        using var client = NewClient();
        var (_, cookie) = await NewSignedInUserAsync(client, "extend");

        var before = DateTimeOffset.UtcNow;
        var extendResponse = await client.SendAsync(Authed(HttpMethod.Post, "/api/auth/extend-session", cookie));
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, extendResponse.StatusCode);
        var extendBody = await extendResponse.Content.ReadFromJsonAsync<ExtendSessionResponse>();
        Assert.NotNull(extendBody);
        Assert.InRange(extendBody!.ExpiresAtUtc, before.AddHours(2).AddSeconds(-30), after.AddHours(2).AddSeconds(30));

        // The new cookie must actually be the one in effect — not just a
        // number in the response body — so replay against /me using the
        // freshly re-issued Set-Cookie and confirm it reports the same
        // (extended) expiry, not the original 8h one.
        var newCookie = extendResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : null;
        Assert.NotNull(newCookie);

        var meResponse = await client.SendAsync(Authed(HttpMethod.Get, "/api/auth/me", newCookie!));
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        // Within a couple seconds, not exactly equal — the cookie ticket's
        // own serialization rounds ExpiresUtc to whole seconds, unlike the
        // in-memory value the extend-session response body was built from.
        // A ~2h difference would mean the old 8h expiry, not the extension,
        // is what's actually in effect — this tolerance would never hide that.
        Assert.True(
            Math.Abs((extendBody.ExpiresAtUtc - me!.ExpiresAtUtc).TotalSeconds) < 2,
            $"Expected {extendBody.ExpiresAtUtc} to be within 2s of {me.ExpiresAtUtc}.");
    }

    [Fact]
    public async Task UnauthenticatedUser_CannotExtendSession()
    {
        using var client = NewClient();
        var response = await client.PostAsync("/api/auth/extend-session", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
