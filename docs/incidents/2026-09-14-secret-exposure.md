# Incident — secret exposure via `dotnet user-secrets list`, 2026-09-14

## What happened

During a Claude Code session working on Step 23 (AI Business Analyst) live-Claude
validation prep, the assistant ran `dotnet user-secrets list` intending to
pipe the output through a `sed` redaction pattern before displaying it. The
redaction regex assumed a bare `Key=value` format; the actual `dotnet
user-secrets list` output format is `Key = value` (spaces around `=`), which
the regex did not match. As a result, the command's full, unredacted output
was printed into the Claude Code session transcript.

## When

2026-09-14, during the same session that had just completed Step 23's
Analyst UI (frontend) and was preparing to run the live-Claude validation
pass.

## What was exposed

The following secret **types** were printed in full, unredacted, in the
session transcript:

- Anthropic API key
- Resend API key
- Shopify client secret
- `WidgetToken` signing key
- `IntegrationStateToken` signing key
- Verification hashing pepper (`Verification:HashingPepper`)
- Neon Postgres connection string, including the database password

No secret values are reproduced here or in any other artifact produced by
this incident response. See the Boundary note at the bottom of this file.

## Relationship to the prior Neon exposure

This **supersedes** the previously-recorded, unconfirmed single-secret
exposure (a Neon dev connection string briefly visible via `dotnet
user-secrets list` in an earlier session, flagged as "should be rotated if
it hasn't been already" and never confirmed either way). That earlier,
narrower item should not be tracked as a separate open item going
forward — this incident's Neon-credential rotation (see below) resolves it
too, and this file is the authoritative record for both.

## Rotation status

| Credential | Rotated | Verified | Notes |
|---|---|---|---|
| Anthropic API key | ✅ 2026-09-14 | ✅ 2026-09-14 | Rotated by the owner via the Anthropic Console; new value set via `dotnet user-secrets set`. **Note**: the new key was pasted directly into the Claude Code chat rather than set from a separate terminal as recommended — it landed in this session's transcript too (a second, smaller exposure of the same class, one secret rather than the full set). Verified without ever printing the value: a temporary, non-committed test resolved the app's real `IMessageService` through `LiveClaudeApiFactory` (the same factory `[LiveClaudeFact]` tests use) and confirmed it is the real `AnthropicClient`-backed implementation, not `MissingAnthropicApiKeyMessageService` — a type check on DI resolution, no real Anthropic API call made, no value read or displayed. The scratch test file was deleted immediately after. |
| Neon Postgres password / connection string | ✅ 2026-09-14 | ✅ 2026-09-14 | Rotated by the owner via the Neon Console; new connection string set via `dotnet user-secrets set`. Verified with a real DB round-trip: the 69-test subset (`WidgetAuthenticationTests`/`IntegrationStateTokenServiceTests`/`VerificationCodeServiceTests`/`WidgetVerificationApiTests`/`CustomersApiTests`, all running through `NivoraApiFactory`'s real, non-faked `AppDbContext`) passed 69/69 against the new credential. **This rotation also resolves the prior unconfirmed single-Neon-string exposure** noted in the Relationship section above — that item is now closed, not separately open. |
| Resend API key | ✅ 2026-09-14 | ✅ 2026-09-14 | Rotated by the owner via the Resend dashboard; new value set via `dotnet user-secrets set`. Verified without any real send: a temporary, non-committed test resolved the real `ResendEmailSender`-backed `IEmailSender` through `LiveClaudeApiFactory` (which, unlike `NivoraApiFactory`, does not fake this service) and confirmed `Program.cs`'s "Missing Resend:ApiKey" eager-configuration check did not throw — proving the key is present and considered valid without ever calling `SendEmailAsync` (no HTTP request made to Resend's API, no external side effect). Scratch test deleted immediately after. |
| Shopify client secret | ✅ 2026-09-14 | ✅ 2026-09-14 | Rotated by the owner via the Shopify Partner dashboard; new value set via `dotnet user-secrets set`. Verified without touching a live store: a temporary, non-committed test resolved the real, config-backed `ShopifyIntegrationOAuthProvider` through `NivoraApiFactory` (Shopify config isn't faked by that factory) and exercised `ValidateCallbackAuthenticity` end-to-end — computed the HMAC for a synthetic callback query using the real configured secret (read into a local variable for the HMAC computation only, never printed or logged), then confirmed the real provider's own independent HMAC computation validated it and returned the expected shop name. This proves the secret is present and the crypto path round-trips correctly without ever calling `ExchangeCodeAsync` (the real token-exchange network call) — no live Shopify store was touched. Scratch test deleted immediately after. |
| `WidgetToken` signing key | ✅ 2026-09-14 | ✅ 2026-09-14 | Locally-generated app secret (no external service) — regenerated via `openssl rand -base64 48` piped directly into `dotnet user-secrets set` (the new value never appeared as literal text in any command or output). Verified via the existing `WidgetAuthenticationTests` suite (issuance + validation round-trip through the real app config, not a hardcoded test value) — 47/47 passed. |
| `IntegrationStateToken` signing key | ✅ 2026-09-14 | ✅ 2026-09-14 | Same method and same reasoning as `WidgetToken:SigningKey` above. Verified via `IntegrationStateTokenServiceTests` (resolves the real `IOptions<IntegrationStateTokenOptions>` from DI, not a hardcoded value) — included in the same 47/47 passing run. |
| Verification hashing pepper | ✅ 2026-09-14 | ✅ 2026-09-14 | Source-reviewed first (see below) — safe to rotate. Regenerated via `openssl rand -hex 32` the same way. Verified via `VerificationCodeServiceTests`/`WidgetVerificationApiTests` — included in the same 47/47 passing run. |

**Source review for the hashing pepper, performed before rotating it** (per the containment plan's explicit requirement not to assume): a full-repository grep confirms `VerificationOptions.HashingPepper` has exactly one consumer anywhere in the codebase — `VerificationCodeService.ComputeCodeHash`, which HMAC-SHA256-hashes the raw OTP code into `VerificationCode.CodeHash` at creation and re-derives the same hash to compare at verification time. Nothing else reads this option or `CodeHash`. `VerificationCode.ExpiresAt` is `now.AddMinutes(VerificationOptions.CodeExpiryMinutes)`, and `CodeExpiryMinutes` defaults to `5` (`VerificationOptions.cs`). So the only real-world effect of rotating this pepper is: any OTP code issued in roughly the last 5 minutes and not yet submitted becomes unverifiable against the new pepper (the stored `CodeHash` was computed with the old one). That's the existing, already-designed self-healing path — `VerificationCodeService`'s own resend flow — not a new failure mode. No other consumer depends on this value at all, so there is nothing longer-lived at risk.

(Table updated as each remaining rotation completes.)

## Closeout

All 7 exposed credential types were rotated and verified on 2026-09-14, all within this same incident-response pass:

- Anthropic API key
- Neon Postgres password / connection string
- Resend API key
- Shopify client secret
- `WidgetToken` signing key
- `IntegrationStateToken` signing key
- Verification hashing pepper

**Verification coverage — all 7 succeeded through a safe existing path, none required inventing new test infrastructure and none created an unintended external side effect:**

- `WidgetToken`/`IntegrationStateToken` signing keys, verification hashing pepper: existing test suites (`WidgetAuthenticationTests`, `IntegrationStateTokenServiceTests`, `VerificationCodeServiceTests`, `WidgetVerificationApiTests`) resolving real, non-hardcoded config — 47/47 passed.
- Neon Postgres credential: the same suite plus `CustomersApiTests` (real `AppDbContext`, no faked DB) — 69/69 passed, confirming a genuine DB round-trip against the new password.
- Anthropic API key: a temporary, non-committed test confirmed `Program.cs`'s real-key branch is taken (an `AnthropicClient`-backed `IMessageService`, not the missing-key fallback) via DI type-resolution only — no real Anthropic API call made.
- Resend API key: a temporary, non-committed test confirmed the real `ResendEmailSender`'s `HttpClient` configuration succeeds (no "Missing Resend:ApiKey" throw) without ever calling `SendEmailAsync` — no real email sent.
- Shopify client secret: a temporary, non-committed test exercised the real `ShopifyIntegrationOAuthProvider.ValidateCallbackAuthenticity` HMAC round-trip against a synthetic callback query, using the real configured secret for the HMAC computation only (never printed) — no call to `ExchangeCodeAsync`, no live store touched.

**No flow was left unverified.** Every credential had a safe existing (or minimally, safely-constructed and immediately-deleted) verification path; none required a real external side effect to prove.

**Follow-up discovered, not fixed by this pass**: during rotation, the new Anthropic API key was pasted directly into the Claude Code chat rather than set from a separate terminal as recommended — it landed in this session's own transcript, a second, smaller exposure of the same class (one secret, one time, rather than the full set). No further action was taken on this within this incident response beyond noting it plainly, per the containment-only boundary below; if this key's presence in that transcript is itself a concern, it would need its own decision (e.g. rotating it once more from outside any chat session).

**Relationship to the prior Neon exposure**: as stated above, this incident's Neon Postgres credential rotation resolves the previously-recorded, unconfirmed single-Neon-string exposure. That earlier item is now closed and should not be tracked separately.

## Addendum — second Anthropic API key exposure and rotation, 2026-09-14

**What happened**: the follow-up item flagged in the Closeout above ("the new
Anthropic API key was pasted directly into the Claude Code chat... a second,
smaller exposure of the same class") was acted on rather than left open. The
Anthropic key set during the original rotation pass had itself landed in
this session's transcript when the owner pasted it directly into chat while
providing it for rotation — the same exposure class as the original
incident, narrower in scope (one credential, one time, rather than the full
set of seven).

**Rotated a second time**: the owner generated a new Anthropic API key via
the Anthropic Console and revoked the one that had been pasted into chat
(itself now treated as compromised, independent of the original leaked
key). The new value was set via `dotnet user-secrets set "Anthropic:ApiKey"
"<new-key>"`, run from a separate terminal window outside any chat
interface — never typed, pasted, or echoed into this or any other session
transcript.

**Verified the same way as the first rotation**: a temporary, non-committed
test (deleted immediately after) resolved the app's real `IMessageService`
through `LiveClaudeApiFactory` and confirmed it is the real
`AnthropicClient`-backed implementation, not
`MissingAnthropicApiKeyMessageService` — a type check on DI resolution only,
no real Anthropic API call made, no value read or displayed at any point.
Passed.

**Status**: this closes the one follow-up item left open at the end of the
original incident response. Anthropic's key has now been rotated twice
across this incident (once as part of the original 7-credential rotation,
once more here because the first rotation's own new value was itself
exposed) — both rotations are complete and verified; no further Anthropic
rotation is pending as of this addendum.

## Boundary

This incident response is containment only: rotate the exposed credentials,
verify affected systems through existing safe test paths, and document the
outcome. No secret-storage redesign, new tooling, or authentication changes
are in scope for this response.

**No actual secret values, connection strings, passwords, tokens, signing
keys, peppers, hashes, or other credential material are recorded anywhere in
this file.**
