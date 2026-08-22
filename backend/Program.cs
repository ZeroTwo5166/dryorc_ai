using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Options;
using backend.Auth;
using backend.Data;
using backend.Email;
using backend.Interfaces;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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

// AddCors itself is NOT dev-only (the "Widget" policy below must exist in
// every environment — a widget embed happens in production too) — only the
// "DashboardDev" policy's own registration stays conditional, unchanged
// from before.
builder.Services.AddCors(options =>
{
    // Dev-only: lets the Next.js dashboard (a different origin even on the
    // same scheme/host — different port) call this API with cookies
    // attached. AllowCredentials requires an explicit origin list, never
    // AllowAnyOrigin.
    if (builder.Environment.IsDevelopment())
    {
        options.AddPolicy("DashboardDev", policy => policy
            .WithOrigins("http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    }

    // Widget endpoints are embedded on arbitrary third-party storefronts,
    // unknown ahead of time — real per-business origin restriction is the
    // application-level Widget.AllowedDomains check inside
    // WidgetController.Init, not CORS. Auth here is a bearer token in the
    // Authorization header, never a cookie, so AllowCredentials is never
    // needed — which is exactly what makes AllowAnyOrigin safe to use (the
    // two cannot be combined in any case). Applied via [EnableCors("Widget")]
    // on WidgetController specifically, not via app.UseCors() globally, so
    // the existing "DashboardDev" policy's own behavior is untouched.
    options.AddPolicy("Widget", policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Real as of Step 4 — resolves BusinessId from the authenticated user's
// claims (see Auth/ClaimsCurrentTenantAccessor.cs). Fail-closed: null
// (unauthenticated, missing/invalid claim) means the global EF Core tenant
// query filter matches zero rows, never everything.
builder.Services.AddScoped<ICurrentTenantAccessor, ClaimsCurrentTenantAccessor>();

// ─────────────────────────────────────────────────────────────
// Customer data lifecycle (GDPR erasure + retention) — see
// Services/ConversationPurgeService.cs for why CustomerErasureService and
// RetentionEnforcementService share one purge operation instead of two.
// IPurgeFailurePoint is a real DI-registered no-op in production; tests
// substitute a fake to deterministically fail PurgeConversationAsync
// partway through, proving its transaction rolls back.
builder.Services.AddScoped<IPurgeFailurePoint, NoOpPurgeFailurePoint>();
builder.Services.AddScoped<IConversationPurgeService, ConversationPurgeService>();
builder.Services.AddScoped<ICustomerErasureService, CustomerErasureService>();
builder.Services.AddScoped<IRetentionEnforcementService, RetentionEnforcementService>();
builder.Services.AddScoped<IVerificationCodeCleanupService, VerificationCodeCleanupService>();
builder.Services.AddHostedService<DataLifecycleBackgroundService>();

// Notification system — no live caller yet, registered as ready-to-call
// infrastructure. See Services/NotificationService.cs.
builder.Services.AddScoped<INotificationService, NotificationService>();

// ─────────────────────────────────────────────────────────────
// Widget (Steps 6-7): chat widget init + message posting. No AI reply yet
// (Step 8) and no dashboard inbox UI yet (Step 9) — see Widget/ and
// Controllers/WidgetController.cs.
//
// Read once, here, and reused for both issuing (WidgetTokenService) and
// validating (AddJwtBearer below) tokens — same fail-fast-at-startup
// pattern as ConnectionStrings:DefaultConnection/Resend:ApiKey above, and
// avoids the two call sites drifting to different keys.
var widgetTokenSigningKey = builder.Configuration["WidgetToken:SigningKey"]
    ?? throw new InvalidOperationException(
        "Missing WidgetToken:SigningKey. Set it via `dotnet user-secrets set " +
        "\"WidgetToken:SigningKey\" \"<random-secret-at-least-32-chars>\"` (Development) or the " +
        "hosting platform's configuration (production) — never commit it to appsettings.json.");

builder.Services.AddSingleton(Options.Create(new WidgetTokenOptions { SigningKey = widgetTokenSigningKey }));
builder.Services.AddScoped<IWidgetTokenService, WidgetTokenService>();
builder.Services.AddScoped<IWidgetConversationService, WidgetConversationService>();

// Step 8: Claude integration (non-streaming, conversation-only — no tools,
// no RAG/knowledge-base grounding yet, both later steps). Model is
// claude-sonnet-5 per NIVORA_DEVELOPMENT_PLAN_V2.md's own recorded stack
// decision — see Services/ClaudeReplyService.cs.
//
// Deliberately lazy: startup must succeed whether or not Anthropic:ApiKey
// is configured (Claude is one feature, not a hard dependency of the whole
// app) — IClaudeReplyService is always registered and resolvable either
// way. When the key is absent, IMessageService is backed by
// MissingAnthropicApiKeyMessageService, which throws a distinct,
// impossible-to-swallow AnthropicNotConfiguredException the moment Claude
// is actually invoked, not at startup, and never a real client constructed
// with an empty key.
var anthropicApiKey = builder.Configuration["Anthropic:ApiKey"];
if (!string.IsNullOrWhiteSpace(anthropicApiKey))
{
    builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicApiKey });
    builder.Services.AddScoped<IMessageService>(sp => sp.GetRequiredService<AnthropicClient>().Messages);
}
else
{
    builder.Services.AddScoped<IMessageService, MissingAnthropicApiKeyMessageService>();
}
builder.Services.AddScoped<IClaudeReplyService, ClaudeReplyService>();

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
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        // A staff account exists but can't sign in until its email is
        // confirmed — SignInManager.PasswordSignInAsync returns
        // SignInResult.NotAllowed for it automatically (PreSignInCheck),
        // so AuthController.Login only needs to branch on that result, not
        // re-check EmailConfirmed itself.
        options.SignIn.RequireConfirmedEmail = true;
    })
    .AddUserStore<TenantAwareUserStore>()
    .AddRoleStore<RoleStore<IdentityRole<Guid>, AppDbContext, Guid>>()
    .AddClaimsPrincipalFactory<NivoraClaimsPrincipalFactory>()
    // Confirmed empirically, not assumed: AddIdentity does NOT register the
    // default token providers on its own here — GenerateEmailConfirmationTokenAsync
    // threw "No IUserTwoFactorTokenProvider<TUser> named 'Default' is registered"
    // without this call.
    .AddDefaultTokenProviders();

// ─────────────────────────────────────────────────────────────
// Widget JWT bearer scheme — a second, independent authentication scheme
// alongside the staff cookie above, not a replacement for it.
//
// Deliberately `.AddAuthentication()` with NO arguments here, not a second
// `.AddAuthentication(someDefaultScheme)` call — AddIdentity above already
// called the argument-taking overload once (internally, via
// AddAuthentication(o => { o.DefaultAuthenticateScheme = ...ApplicationScheme; ... })),
// which set DefaultAuthenticateScheme/DefaultChallengeScheme/DefaultSignInScheme
// to the staff cookie scheme. Calling that overload again here would very
// likely stomp those defaults. The parameterless overload just returns the
// existing AuthenticationBuilder without touching DefaultScheme at all —
// safe to call repeatedly to register additional schemes.
builder.Services.AddAuthentication().AddJwtBearer(NivoraAuthSchemes.WidgetBearer, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = WidgetTokenService.Issuer,
        ValidateAudience = true,
        ValidAudience = WidgetTokenService.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(widgetTokenSigningKey)),
        ClockSkew = TimeSpan.FromMinutes(1),
    };

    // Same reasoning as the staff cookie's OnRedirectToLogin/AccessDenied
    // below — this is a JSON API, never redirect to a page that doesn't exist.
    options.Events = new JwtBearerEvents
    {
        OnChallenge = ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
    };
});

// ─────────────────────────────────────────────────────────────
// Rate limiting — built into the ASP.NET Core shared framework, no
// external package. Both limits are conservative placeholders, not tuned
// production values — expected to be revisited once real widget traffic/
// observability exists.
// ─────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("WidgetInit", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
        }));

    // Partitioned by the authenticated widget JWT's VisitorSessionId claim
    // — never an arbitrary request-body visitor id, which would let one
    // visitor exhaust another's limit (or dodge their own) just by lying
    // about who they are.
    options.AddPolicy("WidgetMessages", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(NivoraClaimTypes.VisitorSessionId)?.Value ?? "unauthenticated",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
        }));
});

// Resend (https://resend.com) — transactional email for the account
// verification flow. FromEmail defaults to Resend's shared onboarding@resend.dev
// sender (see ResendOptions); ApiKey is never in appsettings.json — set via
// `dotnet user-secrets set "Resend:ApiKey" "re_..."` (Development) or the
// hosting platform's configuration (production), same rule as the DB
// connection string above.
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection(ResendOptions.SectionName));
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com/");

    var resendApiKey = builder.Configuration["Resend:ApiKey"]
        ?? throw new InvalidOperationException(
            "Missing Resend:ApiKey. Set it via `dotnet user-secrets set \"Resend:ApiKey\" \"re_...\"` " +
            "(Development) or the hosting platform's configuration (production) — never commit it.");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resendApiKey);
});

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
    // Absolute, not sliding: a session expires exactly 8 hours after
    // sign-in no matter how active the user is — activity must never
    // silently extend it. The only way to get more time is the explicit
    // POST /api/auth/extend-session (AuthController.ExtendSession), which
    // re-issues the cookie with its own new absolute (+2h) expiry — also
    // never sliding, since that's a property of this handler, not of any
    // one ticket.
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;

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
    .AddPolicy(NivoraPolicies.SameTenant, p => p.Requirements.Add(new SameTenantRequirement()))
    // Explicitly bound to WidgetBearer — not the default (staff cookie)
    // scheme. This binding lives here, once, rather than depending on every
    // future widget endpoint remembering to add
    // [Authorize(AuthenticationSchemes = ...)] itself.
    .AddPolicy(NivoraPolicies.Widget, p => p
        .AddAuthenticationSchemes(NivoraAuthSchemes.WidgetBearer)
        .RequireAuthenticatedUser());

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

// After UseAuthorization(), deliberately: the "WidgetMessages" rate-limit
// policy partitions by the authenticated widget JWT's VisitorSessionId
// claim, which is only present on HttpContext.User once policy evaluation
// (a non-default scheme) has run — placing this any earlier would see an
// unauthenticated principal for every widget request.
app.UseRateLimiter();

app.MapControllers();

app.Run();

public partial class Program;
