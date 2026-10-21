using System.Net;
using System.Net.Http.Json;
using backend.Auth;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Staff invite slice 5a (supplementary tests S5a-1 to S5a-3 and S5a-5 of
/// docs/handoffs/staff-invite-implementation-design-v8.md, prompt 5a):
/// StaffInviteOptions defaults and start-up validation, the
/// StaffInvitesEnabledFilter, OptionsStaffInviteSwitch, and the
/// MeResponse.StaffInvitesEnabled flag. The endpoint-level switch tests
/// (the T-I7 family) need the controllers and arrive in slice 5c.
/// </summary>
public class StaffInviteSwitchAndOptionsTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // S5a-1
    [Fact]
    public void Options_Defaults_AndValidateOnStart_RejectsZero()
    {
        var defaults = new StaffInviteOptions();
        Assert.False(defaults.Enabled);
        Assert.Equal(60, defaults.ResendCooldownSeconds);
        Assert.Equal(50, defaults.MaxRetainedPerBusiness);
        Assert.Equal(10, defaults.AnonymousPermitLimitPerMinute);
        Assert.Equal(20, defaults.SendPermitLimitPerMinute);

        using (var root = new NivoraApiFactory())
        using (var zeroCooldown = root.WithWebHostBuilder(b => b.UseSetting($"{StaffInviteOptions.SectionName}:ResendCooldownSeconds", "0")))
        {
            var thrown = Record.Exception(() => zeroCooldown.CreateClient());

            Assert.NotNull(thrown);
            var validation = Find<OptionsValidationException>(thrown);
            Assert.NotNull(validation);
            Assert.Contains("StaffInvites: every number must be greater than zero.", validation!.Message, StringComparison.Ordinal);
        }

        using var defaultClient = _factory.CreateClient();
        Assert.NotNull(defaultClient);
    }

    // S5a-2
    [Fact]
    public async Task Filter_Off_Returns404_WithoutCallingNext()
    {
        var filter = new StaffInvitesEnabledFilter(new FixedStaffInviteSwitch(false));
        var context = NewResourceExecutingContext();
        var nextCalls = 0;

        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalls++;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });

        Assert.IsType<NotFoundResult>(context.Result);
        Assert.Equal(0, nextCalls);
    }

    // S5a-2
    [Fact]
    public async Task Filter_On_CallsNext()
    {
        var filter = new StaffInvitesEnabledFilter(new FixedStaffInviteSwitch(true));
        var context = NewResourceExecutingContext();
        var nextCalls = 0;

        await filter.OnResourceExecutionAsync(context, () =>
        {
            nextCalls++;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });

        Assert.Equal(1, nextCalls);
        Assert.Null(context.Result);
    }

    // S5a-3
    [Fact]
    public void OptionsSwitch_ReadsCurrentValue()
    {
        var monitor = new MutableOptionsMonitor(new StaffInviteOptions { Enabled = true });
        var staffInviteSwitch = new OptionsStaffInviteSwitch(monitor);
        Assert.True(staffInviteSwitch.IsEnabled);

        monitor.CurrentValue = new StaffInviteOptions { Enabled = false };
        Assert.False(staffInviteSwitch.IsEnabled);
    }

    // S5a-5
    [Fact]
    public async Task Me_StaffInvitesEnabled_TrueInTestHost_FalseWhenSwitchedOff()
    {
        var email = $"staff-invite-{Guid.NewGuid():N}@example.test";
        using (var anonClient = NewClient(_factory))
        {
            var registerResponse = await anonClient.PostAsJsonAsync(
                "/api/auth/register", new RegisterRequest("Staff Invite Me Flag Business", "Europe/Copenhagen", email, Password));
            Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
            var registered = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>();
            Assert.NotNull(registered);
            _businessIdsToClean.Add(registered!.BusinessId);
            await ConfirmEmailAsync(anonClient, registered.UserId);
        }

        Assert.True(await GetMeStaffInvitesEnabledAsync(_factory, email));

        using var switchedOff = _factory.WithWebHostBuilder(b => b.UseSetting($"{StaffInviteOptions.SectionName}:Enabled", "false"));
        Assert.False(await GetMeStaffInvitesEnabledAsync(switchedOff, email));
    }

    private const string Password = "Passw0rd!!";

    private static HttpClient NewClient<TEntryPoint>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

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

    private static async Task<bool> GetMeStaffInvitesEnabledAsync<TEntryPoint>(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntryPoint> factory, string email)
        where TEntryPoint : class
    {
        using var client = NewClient(factory);
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = loginResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        var meResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        return me!.StaffInvitesEnabled;
    }

    private static ResourceExecutingContext NewResourceExecutingContext() =>
        new(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());

    private static T? Find<T>(Exception? exception) where T : Exception
    {
        while (exception is not null)
        {
            if (exception is T match)
            {
                return match;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = Find<T>(inner);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private sealed class FixedStaffInviteSwitch(bool isEnabled) : IStaffInviteSwitch
    {
        public bool IsEnabled { get; } = isEnabled;
    }

    private sealed class MutableOptionsMonitor(StaffInviteOptions initial) : IOptionsMonitor<StaffInviteOptions>
    {
        public StaffInviteOptions CurrentValue { get; set; } = initial;

        public StaffInviteOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<StaffInviteOptions, string?> listener) => null;
    }
}
