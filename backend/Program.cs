using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using backend.Auth;
using backend.Data;
using backend.Email;
using backend.Hubs;
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

// TRANSACTION/SAVEPOINT DEPENDENCY (Step 21, signed-token half — see
// docs/handoffs/step-21-signed-token-implementation-ready.md §4B):
// SignedIdentityTokenService's jti-replay + Customer/ExternalIdentity
// atomicity relies on EF Core's default automatic-savepoint behavior
// across multiple SaveChangesAsync calls inside one BeginTransactionAsync
// block — active ONLY because no resilient/retrying execution strategy is
// configured below. Do not add EnableRetryOnFailure (or any other
// execution strategy) to this UseNpgsql call without first re-reviewing
// SignedIdentityTokenService's transaction handling — doing so silently
// could change or disable that savepoint behavior and reopen the replay/
// atomicity guarantee it depends on. The regression guard for this is
// SignedIdentityTokenServiceTests.ValidateAndResolveAsync_FailureAfterJtiInsert_RollsBackJti_TokenRetryableAfterward
// — a failure there means this exact dependency broke.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(PostgresConnectionString.FromUri(neonConnectionUri), o => o.UseVector()));

// Step 18: persist the Data Protection key ring in Postgres (AppDbContext's
// own DB) instead of the framework's filesystem default. This is what
// Integration access/refresh tokens (IIntegrationTokenProtector) are
// encrypted under, and it's also now what Identity's own cookie protection
// rides on — a positive side effect (staff cookies survive restarts/
// scale-out too), not a goal being separately pursued.
//
// OPERATIONAL REQUIREMENT, not optional: the resulting DataProtectionKeys
// table MUST be included in the production database backup scope before any
// real integration is ever connected. Losing this key ring permanently
// prevents decryption of every already-stored Integration.EncryptedAccessToken/
// EncryptedRefreshToken — there is no recovery path other than every
// affected business reconnecting the integration from scratch. This was
// chosen over the filesystem default specifically because the deployment
// target is still undecided (see NIVORA_DEVELOPMENT_PLAN_V2.md) and a
// filesystem key ring would not durably survive a restart/scale-out on most
// hosting platforms — recorded as an operational dependency in
// NIVORA_FULL_PLAN_CHECKLIST_V2.md's Smaller Known Loose Ends.
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

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

// Step 19: tool-dispatch infrastructure — see
// docs/handoffs/step-19-tool-dispatch-design-approved.md and
// docs/handoffs/step-19-idempotency-key-derivation-approved.md. Resolves
// IEnumerable<IDispatchTool> the same way IntegrationsController already
// resolves IEnumerable<IIntegrationOAuthProvider> above.
//
// [Step 23K] No longer dormant/unreachable — ClaudeReplyService now calls
// RunAsync (for conversations with a resolved CustomerId; see
// ClaudeReplyService's own doc comment) and one concrete read-only tool
// (GetCustomerTool) is registered below. Still no write tool anywhere in
// this codebase — every write-tool workstream remains exactly as blocked as
// before (see docs/handoffs/step-23k-dispatch-wiring-proof.md for the full
// scope of what this pass proved vs. did not).
builder.Services.AddScoped<IToolDispatchService, ToolDispatchService>();
builder.Services.AddScoped<IDispatchTool, GetCustomerTool>();

// Step 48: registered unconditionally at the DI level, mirroring
// GetCustomerTool above — the per-business kill switch
// (Business.CancelOrderToolEnabled) is enforced entirely at the
// tool-exposure layer (ClaudeReplyService, before ToolDispatchService.RunAsync
// is ever called), not here. Registration alone does not make this tool
// live for any business whose flag is false.
builder.Services.AddScoped<IDispatchTool, OrderCancelProposalTool>();

// Step getOrder — registered unconditionally at the DI level, mirroring
// GetCustomerTool/OrderCancelProposalTool above. The shared per-business
// kill switch (Business.ShopifyReadToolsEnabled) is enforced entirely at the
// tool-exposure layer (ClaudeReplyService, before ToolDispatchService.RunAsync
// is ever called), not here — registration alone does not make this tool
// live for any business whose flag is false. See
// docs/handoffs/step-getorder-implementation-design.md.
builder.Services.AddScoped<IDispatchTool, GetOrderTool>();

// Step 11: Voyage embeddings for KnowledgeDocument chunking/indexing — same
// lazy pattern as Anthropic above. Startup must succeed whether or not
// Voyage:ApiKey is configured; IKnowledgeIndexingService is always
// registered and resolvable either way. When the key is absent,
// IVoyageEmbeddingClient is backed by MissingVoyageApiKeyEmbeddingClient,
// which throws a distinct VoyageNotConfiguredException the moment embedding
// is actually attempted, not at startup, and never a real client
// constructed with an empty key.
var voyageApiKey = builder.Configuration["Voyage:ApiKey"];
if (!string.IsNullOrWhiteSpace(voyageApiKey))
{
    builder.Services.AddHttpClient<IVoyageEmbeddingClient, VoyageEmbeddingClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.voyageai.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", voyageApiKey);
    });
}
else
{
    builder.Services.AddScoped<IVoyageEmbeddingClient, MissingVoyageApiKeyEmbeddingClient>();
}
builder.Services.AddScoped<IKnowledgeIndexingService, KnowledgeIndexingService>();

// Step 12: RAG retrieval, over the same lazily-registered IVoyageEmbeddingClient
// above — reused as-is, no second Voyage client/registration. Degrades
// internally on Voyage failure (see KnowledgeRetrievalService), so it's
// always safe to register unconditionally here regardless of whether a key
// is configured.
builder.Services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();

// Step 13A: ConversationModeService/TicketSlaService — standalone domain
// logic, no production caller yet (see each service's own doc comment).
// AuditLogService — minimal, deliberately narrower than Step 29's eventual
// PII-allowlist validation; exists only so TagModeMappingsController has
// something real to call.
builder.Services.AddScoped<IConversationModeService, ConversationModeService>();
builder.Services.AddScoped<IConversationClassificationService, ConversationClassificationService>();
builder.Services.AddScoped<IConversationTagCorrectionService, ConversationTagCorrectionService>();
builder.Services.AddScoped<ITicketSlaService, TicketSlaService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<IAnalystCapability, GetConversationsByDayCapability>();
builder.Services.AddScoped<IAnalystCapability, GetConversationStatusDistributionCapability>();
builder.Services.AddScoped<IAnalystCapability, GetHandledBySnapshotCapability>();
builder.Services.AddScoped<IAnalystCapability, GetHandoffsByDayCapability>();
builder.Services.AddScoped<IAnalystCapability, GetTicketStatusDistributionCapability>();
builder.Services.AddScoped<IAnalystOrchestrationService, AnalystOrchestrationService>();

// Step 18: Business Integration Framework (generic OAuth connect/callback/
// disconnect — see Controllers/IntegrationsController.cs). Encryption rides
// on IDataProtectionProvider (registered via AddDataProtection() above).
//
// Deliberately NO IIntegrationOAuthProvider is registered here — Step 18
// ships the framework only, zero concrete providers. IntegrationsController
// resolves providers via IEnumerable<IIntegrationOAuthProvider>, which the
// built-in DI container correctly resolves to an empty sequence with none
// registered, so every {platform} route responds 404 in production today,
// by design. Step 19 adds the first real registration (Shopify).
var integrationStateTokenSigningKey = builder.Configuration["IntegrationStateToken:SigningKey"]
    ?? throw new InvalidOperationException(
        "Missing IntegrationStateToken:SigningKey. Set it via `dotnet user-secrets set " +
        "\"IntegrationStateToken:SigningKey\" \"<random-secret-at-least-32-chars>\"` (Development) or the " +
        "hosting platform's configuration (production) — never commit it to appsettings.json.");
builder.Services.AddSingleton(Options.Create(new IntegrationStateTokenOptions { SigningKey = integrationStateTokenSigningKey }));
builder.Services.AddScoped<IIntegrationStateTokenService, IntegrationStateTokenService>();
builder.Services.AddScoped<IIntegrationTokenProtector, IntegrationTokenProtector>();

// Step 19: Shopify — the first real IIntegrationOAuthProvider registration
// (see IIntegrationOAuthProvider's own doc comment for why Step 18 shipped
// none). Fail-fast, not the lazy Anthropic/Voyage pattern above: there is no
// "integration feature is optional" framing here — IntegrationsController
// expects exactly one concrete provider once Shopify is wired in.
//
// Registered as AddHttpClient<ShopifyIntegrationOAuthProvider> (the concrete
// type, not the interface) plus a single explicit forwarding registration
// for IIntegrationOAuthProvider, rather than
// AddHttpClient<IIntegrationOAuthProvider, ShopifyIntegrationOAuthProvider>
// directly — this is what lets backend.Tests override just the underlying
// HttpClient's handler (to fake Shopify's token endpoint) via a second
// AddHttpClient<ShopifyIntegrationOAuthProvider> call in test host
// configuration, without ending up with two IIntegrationOAuthProvider
// registrations in IEnumerable<IIntegrationOAuthProvider>.
var shopifyClientId = builder.Configuration["Shopify:ClientId"]
    ?? throw new InvalidOperationException(
        "Missing Shopify:ClientId. Set it via `dotnet user-secrets set " +
        "\"Shopify:ClientId\" \"...\"` (Development) or the hosting platform's " +
        "configuration (production) — never commit it to appsettings.json.");
var shopifyClientSecret = builder.Configuration["Shopify:ClientSecret"]
    ?? throw new InvalidOperationException(
        "Missing Shopify:ClientSecret. Set it via `dotnet user-secrets set " +
        "\"Shopify:ClientSecret\" \"...\"` (Development) or the hosting platform's " +
        "configuration (production) — never commit it to appsettings.json.");
var shopifyRedirectUri = builder.Configuration["Shopify:RedirectUri"]
    ?? throw new InvalidOperationException(
        "Missing Shopify:RedirectUri. Set it via `dotnet user-secrets set " +
        "\"Shopify:RedirectUri\" \"https://<this-backend>/api/integrations/shopify/callback\"` " +
        "(Development) or the hosting platform's configuration (production) — " +
        "must exactly match the redirect URL registered for this app in Shopify's " +
        "Partner Dashboard.");
// Step 20: the Admin API version ShopifyOrderLookupService targets.
// Configuration-driven and fail-fast, same reasoning as ClientId/
// ClientSecret/RedirectUri above — never guessed or hardcoded in
// application logic. Confirmed directly against Shopify (two independent
// live shopify.dev /latest/ documentation-page resolutions, cross-checked
// against the official @shopify/shopify-api library's own ApiVersion enum)
// as "2026-07" at the time this was set; re-verify independently before
// ever changing this value.
var shopifyAdminApiVersion = builder.Configuration["Shopify:AdminApiVersion"]
    ?? throw new InvalidOperationException(
        "Missing Shopify:AdminApiVersion. Set it via `dotnet user-secrets set " +
        "\"Shopify:AdminApiVersion\" \"2026-07\"` (Development) or the hosting platform's " +
        "configuration (production) — verify the current stable version directly against " +
        "Shopify before setting it, never guess.");
builder.Services.AddSingleton(Options.Create(new ShopifyOptions
{
    ClientId = shopifyClientId,
    ClientSecret = shopifyClientSecret,
    RedirectUri = shopifyRedirectUri,
    AdminApiVersion = shopifyAdminApiVersion,
}));
builder.Services.AddHttpClient<ShopifyIntegrationOAuthProvider>();
builder.Services.AddTransient<IIntegrationOAuthProvider>(sp => sp.GetRequiredService<ShopifyIntegrationOAuthProvider>());

// Step 20: order-lookup + ownership-authorization, designed together as one
// unit but implemented as two separate services (see the Step 20 handoff).
// Same split-registration pattern as ShopifyIntegrationOAuthProvider above
// — AddHttpClient<TImpl> plus a single forwarding registration for the
// interface — so tests can override the HTTP handler without a duplicate
// IShopifyOrderLookupService registration.
builder.Services.AddHttpClient<ShopifyOrderLookupService>();
builder.Services.AddTransient<IShopifyOrderLookupService>(sp => sp.GetRequiredService<ShopifyOrderLookupService>());
builder.Services.AddScoped<IOrderOwnershipAuthorizationService, OrderOwnershipAuthorizationService>();

// Step Group B — getProduct/searchProducts, a new sibling service to
// ShopifyOrderLookupService above (see docs/handoffs/step-group-b-implementation-design.md
// §4.4). Same split-registration pattern, for the identical test-override
// reason. Both tools are registered unconditionally at the DI level,
// mirroring GetOrderTool above — the shared per-business kill switch
// (Business.ShopifyReadToolsEnabled, already used by getOrder) is enforced
// entirely at the tool-exposure layer (ClaudeReplyService), not here.
builder.Services.AddHttpClient<ShopifyProductLookupService>();
builder.Services.AddTransient<IShopifyProductLookupService>(sp => sp.GetRequiredService<ShopifyProductLookupService>());
builder.Services.AddScoped<IDispatchTool, GetProductTool>();
builder.Services.AddScoped<IDispatchTool, SearchProductsTool>();

// Step Group C (getInventory) — registered unconditionally at the DI level,
// mirroring GetProductTool/SearchProductsTool above. IShopifyProductLookupService
// is already registered above (this tool reuses it, per Owner Decision 3 —
// no new service registration needed). The shared per-business kill switch
// (Business.ShopifyReadToolsEnabled, already used by getOrder/getProduct/
// searchProducts) is enforced entirely at the tool-exposure layer
// (ClaudeReplyService), not here.
builder.Services.AddScoped<IDispatchTool, GetInventoryTool>();
builder.Services.AddScoped<IOrderCancellationService, OrderCancellationService>();
builder.Services.AddScoped<ICustomerUpdateActionService, CustomerUpdateActionService>();

// Step refundOrder Phase 1 — persistence/idempotency foundation (see
// docs/handoffs/step-refundorder-design.md).
builder.Services.AddScoped<IRefundService, RefundService>();

// Step refundOrder Phase 2 — RefundProposalTool, the AI-facing proposal
// tool. Registered as IDispatchTool, mirroring OrderCancelProposalTool's own
// registration immediately above/below (ToolDispatchService resolves the
// concrete IActionProposalTool case itself — see ToolDispatchService.cs's
// existing switch, unmodified by this phase). Exposure-time gating
// (Business.RefundToolEnabled, Owner Decision 3 Layer 1) is applied in
// ClaudeReplyService's own disabledToolNames construction, mirroring
// CancelOrderToolEnabled's identical pattern — this registration itself is
// unconditional, same as OrderCancelProposalTool's own registration.
builder.Services.AddScoped<IDispatchTool, RefundProposalTool>();

// Pending-reconciliation design pass: ShopifyOrderCancellationExecutor
// actually calls orderCancel (no production trigger yet — deliberate, see
// docs/handoffs/pending-reconciliation-design-scoping.md; exercised only
// by tests). OrderCancellationReconciliationBackgroundService runs for
// real in every environment, same as DataLifecycleBackgroundService below
// — harmless when there are zero Pending rows, which is the current state
// since nothing production-facing creates one yet.
builder.Services.AddHttpClient<ShopifyOrderCancellationExecutor>();
builder.Services.AddTransient<IOrderCancellationExecutor>(sp => sp.GetRequiredService<ShopifyOrderCancellationExecutor>());
builder.Services.AddScoped<IOrderCancellationReconciliationService, OrderCancellationReconciliationService>();
builder.Services.AddHostedService<OrderCancellationReconciliationBackgroundService>();

// Step refundOrder Phase 3 — the refundable-quantity/suggestedRefund
// preflight lookup (design §8.3, research Q1/Q2) and the refundCreate
// executor (design §8.5/§9.5, research Q3/Q5/Q11). Same split-registration
// pattern as ShopifyOrderLookupService/ShopifyOrderCancellationExecutor
// above, for the identical test-override reason.
builder.Services.AddHttpClient<ShopifyRefundLookupService>();
builder.Services.AddTransient<IShopifyRefundLookupService>(sp => sp.GetRequiredService<ShopifyRefundLookupService>());
builder.Services.AddHttpClient<ShopifyRefundExecutor>();
builder.Services.AddTransient<IRefundExecutor>(sp => sp.GetRequiredService<ShopifyRefundExecutor>());

// IApprovedActionExecutor's implementation — all 16 steps of the execution
// ordering are implemented as of step-47 (3-13 committed earlier; 14-16,
// the pre-execution ground-truth check and the Shopify orderCancel mutation
// call, completed by step-47), plus step-48's pre-step-10 guard against a
// business with no active Shopify integration. Called by ApprovalsController's
// POST .../execute action (step-47) — no longer dormant.
builder.Services.AddScoped<IApprovedActionExecutor, OrderCancelApprovedActionExecutor>();

// Step refundOrder Phase 3 — the second concrete IApprovedActionExecutor.
// Resolved by ApprovalsController via IEnumerable<IApprovedActionExecutor> +
// ToolName matching (design §3.3, already implemented ahead of this phase).
builder.Services.AddScoped<IApprovedActionExecutor, RefundApprovedActionExecutor>();

// Step 24 Phase 7 — Scheduled AI Reports go-live
// (docs/handoffs/step-24-scheduled-ai-reports-design.md). TimeProvider.System
// is the real system clock, registered so ScheduledReportSchedulerService/
// ScheduledReportExecutionService can each take TimeProvider as an ordinary
// constructor dependency. This singleton registration itself is never
// touched by tests (NivoraApiFactory instead overrides the two scoped
// service registrations directly, each with its own hand-constructed
// TimeProvider — see that factory's own doc comment for why the shared
// TimeProvider singleton itself must stay untouched: ASP.NET Core Identity's
// cookie authentication also resolves it for session-expiry timestamps).
// Business.ScheduledReportsEnabled defaults to false and
// every existing business stays disabled at ship (locked Decision 3) — this
// block changes production behavior for zero real businesses until an
// Owner explicitly opts in via Phase 1's SetScheduledReportsEnabled
// endpoint, the same "ship dark, opt-in later" pattern already used for
// AiBusinessAnalystEnabled/CancelOrderToolEnabled/RefundToolEnabled above.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IScheduledReportSchedulerService, ScheduledReportSchedulerService>();
builder.Services.AddScoped<IScheduledReportExecutionService, ScheduledReportExecutionService>();
builder.Services.AddHostedService<ScheduledReportBackgroundService>();

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

    // Step 6C polling MVP. A separate policy from WidgetMessages, not a
    // shared budget — polling is a background, ~5-second-interval loop
    // while the widget is open, a different traffic shape from occasional
    // message sends; sharing one limit would let a chatty poll loop starve
    // a real send. 20/min is a placeholder matched loosely to the ~5s poll
    // interval plus headroom for tab-visibility catch-up polls — like both
    // limits above, not a tuned production value, expected to be revisited
    // once real widget traffic/observability exists.
    options.AddPolicy("WidgetPoll", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(NivoraClaimTypes.VisitorSessionId)?.Value ?? "unauthenticated",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
        }));

    // Step 21 (OTP half). Complementary, coarse volumetric controls —
    // VisitorSessionId-partitioned like the policies above, NOT the actual
    // resend-cooldown/resend-limit security boundary (that's the
    // scope-keyed check inside VerificationCodeService, since a fresh
    // Init call trivially gets a fresh VisitorSessionId and a fresh
    // partition here). Placeholder limits, same as the policies above.
    options.AddPolicy("WidgetOtpRequest", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst(NivoraClaimTypes.VisitorSessionId)?.Value ?? "unauthenticated",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
        }));

    options.AddPolicy("WidgetOtpVerify", httpContext => RateLimitPartition.GetFixedWindowLimiter(
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

// ─────────────────────────────────────────────────────────────
// Step 21 (OTP half): Level 2 verification. HashingPepper follows the same
// fail-fast-at-startup, never-in-appsettings.json rule as
// WidgetToken:SigningKey/Resend:ApiKey above. Depends on IEmailSender
// (registered immediately above) for OTP delivery.
// ─────────────────────────────────────────────────────────────
var verificationHashingPepper = builder.Configuration["Verification:HashingPepper"]
    ?? throw new InvalidOperationException(
        "Missing Verification:HashingPepper. Set it via `dotnet user-secrets set " +
        "\"Verification:HashingPepper\" \"<random-secret-at-least-32-chars>\"` (Development) or the " +
        "hosting platform's configuration (production) — never commit it to appsettings.json.");
builder.Services.Configure<VerificationOptions>(options =>
{
    builder.Configuration.GetSection(VerificationOptions.SectionName).Bind(options);
    options.HashingPepper = verificationHashingPepper;
});
builder.Services.AddScoped<IVerificationCodeService, VerificationCodeService>();
builder.Services.AddScoped<ILevel2VerificationChecker, Level2VerificationChecker>();

// Step 21 (signed-token half). Unlike VerificationOptions above, nothing
// here is a secret — Nivora only ever stores per-business PUBLIC keys
// (BusinessSigningKey), never a private signing key — so a plain
// configuration-section bind is sufficient, no dotnet user-secrets value
// to inject. No production trigger for BusinessSigningKey registration
// exists yet (manual/staff-mediated provisioning for v1, per the approved
// design) — ISignedIdentityTokenService itself is fully wired into
// WidgetController.Init below and is real, tested infrastructure, not
// dormant like ToolDispatchService.
builder.Services.Configure<SignedTokenOptions>(builder.Configuration.GetSection(SignedTokenOptions.SectionName));
builder.Services.AddScoped<ISignedIdentityTokenService, SignedIdentityTokenService>();
builder.Services.AddScoped<IConsumedSignedIdentityTokenCleanupService, ConsumedSignedIdentityTokenCleanupService>();

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

// Step 13B: TicketHub — server-side SignalR is already part of the
// ASP.NET Core shared framework referenced via Microsoft.NET.Sdk.Web, no
// extra PackageReference needed. Registered here, mapped below (after
// UseAuthentication/UseAuthorization/UseCors) so the hub's own
// [Authorize(Policy = NivoraPolicies.AnyStaff)] is enforced through the
// same middleware pipeline every other authenticated endpoint uses — no
// second auth scheme, no bypass.
builder.Services.AddSignalR();

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

// Skipped in Development: the dashboard (http://localhost:3000) and this
// API's http profile (http://localhost:5293) are deliberately both plain
// HTTP in dev, to keep the auth cookie same-site (see
// project_nivora_frontend_gotchas memory, schemeful-same-site entry) — if
// an HTTPS port is ever also bound (e.g. `--launch-profile https`), this
// middleware redirects every http:// request, including CORS preflight
// OPTIONS calls, before UseCors() below ever runs. Browsers reject any
// redirect response to a preflight outright, breaking every cross-origin
// call. Real deployments terminate TLS in front of the app either way, so
// skipping this in dev doesn't weaken production.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("DashboardDev");
}

// Step 6B: serves the built embeddable widget bundle from
// wwwroot/widget/nivora-widget.js at /widget/nivora-widget.js. A
// <script src> load isn't subject to CORS at all (unlike the widget's own
// fetch() calls back to /api/widget/*, which the existing "Widget" CORS
// policy above already covers) — no CORS change needed for this. Placed
// before authentication/authorization deliberately: this is a public,
// unauthenticated static asset, same as the script tag itself.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// After UseAuthorization(), deliberately: the "WidgetMessages" rate-limit
// policy partitions by the authenticated widget JWT's VisitorSessionId
// claim, which is only present on HttpContext.User once policy evaluation
// (a non-default scheme) has run — placing this any earlier would see an
// unauthenticated principal for every widget request.
app.UseRateLimiter();

app.MapControllers();

// Step 13B: mapped after UseAuthentication()/UseAuthorization()/UseCors()
// above, so the negotiate/connect handshake goes through the same staff
// cookie auth + CORS-with-credentials pipeline as every other authenticated
// endpoint — no bypass, no second auth mechanism.
app.MapHub<TicketHub>("/hubs/tickets");

app.Run();

public partial class Program;
