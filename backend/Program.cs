using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var neonConnectionUri = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:DefaultConnection. Set it via `dotnet user-secrets set " +
        "\"ConnectionStrings:DefaultConnection\" \"postgresql://...\"` (Development) or the " +
        "hosting platform's configuration (production) — never commit it to appsettings.json.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(PostgresConnectionString.FromUri(neonConnectionUri), o => o.UseVector()));

builder.Services.AddHttpContextAccessor();

// Dev-only: lets the Next.js dashboard (a different origin even on the same
// scheme/host — different port) call this API with cookies attached.
// AllowCredentials requires an explicit origin list, never AllowAnyOrigin.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("DashboardDev", policy => policy
            .WithOrigins("http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    });
}

// Real as of Step 4 — resolves BusinessId from the authenticated user's
// claims (see Auth/ClaimsCurrentTenantAccessor.cs). Fail-closed: null
// (unauthenticated, missing/invalid claim) means the global EF Core tenant
// query filter matches zero rows, never everything.
builder.Services.AddScoped<ICurrentTenantAccessor, ClaimsCurrentTenantAccessor>();

// ─────────────────────────────────────────────────────────────
// Identity — cookie-based auth for staff/dashboard users.
//
// AddIdentity<TUser,TRole>(), not AddIdentityCore: confirmed empirically
// (not guessed) that AddIdentityCore alone registers no SignInManager, no
// authentication scheme, and no cookie handler at all — building that back
// up manually (AddSignInManager + AddAuthentication().AddCookie(...)) is
// exactly what AddIdentity already does correctly, including the
// IdentityConstants.ApplicationScheme cookie scheme this app's sign-in flow
// depends on. AddIdentity also already wires role support (RoleManager)
// once IdentityRole<Guid> is supplied as its second type argument — no
// separate .AddRoles<>() call needed on top of it.
//
// Explicit .AddUserStore<TenantAwareUserStore>()/.AddRoleStore<...>()
// instead of .AddEntityFrameworkStores<AppDbContext>() — this is what
// actually wires the tenant-filter/login-conflict fix (see
// Auth/TenantAwareUserStore.cs) in place of the default EF Core store.
// ─────────────────────────────────────────────────────────────
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>()
    .AddUserStore<TenantAwareUserStore>()
    .AddRoleStore<RoleStore<IdentityRole<Guid>, AppDbContext, Guid>>()
    .AddClaimsPrincipalFactory<NivoraClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    // Distinct name/scope from the widget's future visitor-session cookie
    // (not built in Step 4) — these are two separate cookies with two
    // separate security postures, never to be conflated.
    options.Cookie.Name = "Nivora.Staff.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    // SameAsRequest, not Always: lets local HTTP dev/testing (e.g.
    // WebApplicationFactory's TestServer) still exercise the cookie, while
    // any real deployment — always served over HTTPS — gets Secure
    // naturally from that. Never SameSite=None here; that's reserved for
    // the widget's separate cross-site cookie, not this one.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // This is a JSON API, not a Razor/MVC app — an unauthenticated or
    // forbidden request must get a real status code, not Identity's
    // default 302 redirect to a login *page* that doesn't exist here.
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// ─────────────────────────────────────────────────────────────
// Authorization — the 4-step checklist (who / which business / does the
// resource belong to that business / does the role permit it) lives across
// [Authorize(Policy = ...)] for steps 1+4 and the SameTenant resource-based
// policy for step 3. See Auth/NivoraPolicies.cs.
// ─────────────────────────────────────────────────────────────
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(NivoraPolicies.AnyStaff, p => p.RequireAuthenticatedUser())
    .AddPolicy(NivoraPolicies.OwnerOrAdmin, p => p.RequireRole(RoleNames.Owner, RoleNames.Admin))
    .AddPolicy(NivoraPolicies.OwnerOnly, p => p.RequireRole(RoleNames.Owner))
    .AddPolicy(NivoraPolicies.SameTenant, p => p.Requirements.Add(new SameTenantRequirement()));

builder.Services.AddScoped<IAuthorizationHandler, SameTenantAuthorizationHandler>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await RoleSeeder.SeedAsync(scope.ServiceProvider);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseCors("DashboardDev");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
