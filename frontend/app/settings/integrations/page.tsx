"use client";

import { Suspense, useEffect, useRef, useState, type FormEvent } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError } from "../../components/auth/FormField";
import { IconStore } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import { formatRelativeTime } from "../../lib/relative-time";
import {
  ApiError,
  connectIntegration,
  disconnectIntegration,
  listIntegrations,
  setCancelOrderToolEnabled,
  type Integration,
} from "../../lib/api";

// backend/Services/ShopifyIntegrationOAuthProvider.cs's PlatformName — the
// only registered IIntegrationOAuthProvider today. No endpoint lists which
// platforms are connectable, so this is a literal, not user input (see
// docs/handoffs/step-49-...-design.md §3a/§3c).
const SHOPIFY_PLATFORM = "shopify";

// Mirrors ShopifyIntegrationOAuthProvider.ShopNameRegex exactly. A UX
// nicety only, not a security boundary — Connect below re-validates
// unconditionally on every call regardless of what this allows through.
const SHOP_NAME_REGEX = /^[a-zA-Z0-9]([a-zA-Z0-9-]{0,98}[a-zA-Z0-9])?$/;

// The 5 integrationError codes IntegrationsController.Callback can redirect
// back with — see the Step 49 design doc §4. Never invent additional codes:
// an unrecognized value (shouldn't happen given the backend contract) falls
// through to the generic fallback below rather than being given its own
// meaning here. Copy deliberately describes the outcome, never the
// mechanism (no "HMAC"/"state token" wording) — see design doc §8.
const OAUTH_ERROR_COPY: Record<string, string> = {
  hmac_invalid: "This connection attempt couldn't be verified. Please try connecting again.",
  invalid_state: "This connection attempt couldn't be verified. Please try connecting again.",
  state_mismatch: "This connection attempt couldn't be verified. Please try connecting again.",
  exchange_failed: "Couldn't complete the connection with Shopify. Please try again.",
  conflict: "Another connection attempt for Shopify was already in progress. Please try again.",
};

// Step shopify-auth-reauth (frontend design doc §3b) — IsActive takes
// priority over ReauthorizationRequiredAt exactly as the backend design
// locked it: a disconnected integration is always "disconnected", never
// "requires_reauth", regardless of what reauthorizationRequiredAt holds.
type IntegrationStatus = "connected" | "requires_reauth" | "disconnected";

const INTEGRATION_STATUS_BADGE: Record<IntegrationStatus, string> = {
  connected: "bg-accent-soft text-accent",
  requires_reauth: "bg-orange-soft text-orange",
  disconnected: "bg-muted text-muted-foreground",
};

const INTEGRATION_STATUS_LABEL: Record<IntegrationStatus, string> = {
  connected: "Connected",
  requires_reauth: "Requires reauthorization",
  disconnected: "Disconnected",
};

// useSearchParams requires a Suspense boundary — same shape as
// verify-email/page.tsx, the codebase's existing precedent for this.
export default function SettingsIntegrationsPage() {
  return (
    <Suspense fallback={null}>
      <SettingsIntegrationsContent />
    </Suspense>
  );
}

function SettingsIntegrationsContent() {
  const router = useRouter();
  const { user, status } = useAuth();
  // Precedent is OwnerOnly, not OwnerOrAdmin, for this controller (see
  // IntegrationsController's own doc comment and design doc §7) — so this
  // is deliberately not the copy-pasted `canWrite` idiom used elsewhere.
  const canConnect = user?.roles.includes("Owner") ?? false;
  const searchParams = useSearchParams();
  const connectedParam = searchParams.get("connected");
  const integrationErrorParam = searchParams.get("integrationError");

  const [shopify, setShopify] = useState<Integration | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);

  const [shopName, setShopName] = useState("");
  const [shopNameError, setShopNameError] = useState<string | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);

  const [disconnecting, setDisconnecting] = useState(false);
  const [disconnectError, setDisconnectError] = useState<string | null>(null);

  // Step 49b — Cancel Order Tool toggle. Local state, seeded once from
  // user.cancelOrderToolEnabled — never re-synced from `user` on every
  // render (see the seeding effect below). Mirrors the `shopify` state
  // pattern above, not the shared auth-context object directly.
  const [toolEnabled, setToolEnabled] = useState(false);
  const toolEnabledSeededRef = useRef(false);
  const [savingTool, setSavingTool] = useState(false);
  const [toolError, setToolError] = useState<string | null>(null);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  // Seeds `toolEnabled` exactly once, the first time `user` becomes
  // available — never again afterward, even if `user`'s reference later
  // changes for an unrelated reason (e.g. the session-expiry-extension
  // flow re-calling Me()). Without the ref guard, a plain `[user]`-keyed
  // effect would clobber an in-flight or already-completed optimistic
  // update with a stale value on every such refresh.
  useEffect(() => {
    if (!user || toolEnabledSeededRef.current) return;
    toolEnabledSeededRef.current = true;
    setToolEnabled(user.cancelOrderToolEnabled);
  }, [user]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    // Always re-fetched on mount regardless of the ?connected=shopify query
    // param — that param only drives the acknowledgment banner below, never
    // trusted alone as the source of connection state (design doc §9).
    listIntegrations()
      .then((result) => {
        if (cancelled) return;
        setShopify(result.find((i) => i.platform === SHOPIFY_PLATFORM) ?? null);
        setLoadError(false);
      })
      .catch(() => {
        if (!cancelled) setLoadError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user]);

  // Step shopify-auth-reauth (frontend design doc §3d/owner decision #3) —
  // pre-fills the shop-name input from the existing identifier exactly
  // once, the first time the loaded integration is found to require
  // reauthorization. Never re-seeds afterward, same ref-guard idiom as
  // toolEnabledSeededRef above — a value the merchant has already edited
  // must never be clobbered by a later render.
  const shopNameSeededRef = useRef(false);
  useEffect(() => {
    if (shopNameSeededRef.current) return;
    if (shopify?.isActive !== true || shopify.reauthorizationRequiredAt == null || !shopify.externalAccountIdentifier) return;
    shopNameSeededRef.current = true;
    // Syncing the input to an external signal (the just-loaded integration
    // record needing reauthorization) — the legitimate effect case, not the
    // cascading-render anti-pattern this lint rule otherwise guards against.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setShopName(shopify.externalAccountIdentifier);
  }, [shopify]);

  async function handleConnect(e: FormEvent) {
    e.preventDefault();
    const trimmed = shopName.trim();
    if (!SHOP_NAME_REGEX.test(trimmed)) {
      setShopNameError('Enter a bare shop name (e.g. "example-store"), not a URL or domain.');
      return;
    }
    setShopNameError(null);
    setConnectError(null);
    setConnecting(true);
    try {
      const { authorizationUrl } = await connectIntegration(SHOPIFY_PLATFORM, {
        externalAccountIdentifier: trimmed,
      });
      // Full top-level navigation away from Nivora, not a fetch — nothing
      // on this page survives it, so there's no further state to set here
      // (design doc §4, point 3).
      window.location.href = authorizationUrl;
    } catch (err) {
      setConnectError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
      setConnecting(false);
    }
  }

  async function handleDisconnect() {
    setDisconnectError(null);
    setDisconnecting(true);
    try {
      await disconnectIntegration(SHOPIFY_PLATFORM);
      // 204 with no body — optimistic local flip, same idiom as
      // handleToggleExtendedLogging (customers/[id]/page.tsx), not a
      // re-fetch (design doc §9). reauthorizationRequiredAt is cleared here
      // too — IsActive takes priority over it everywhere it's read
      // (integrationStatus above), so its value is inert once disconnected,
      // but null is the accurate fact regardless.
      setShopify((prev) =>
        prev ? { ...prev, isActive: false, disconnectedAt: new Date().toISOString(), reauthorizationRequiredAt: null } : prev,
      );
    } catch (err) {
      // Leave state as-is — the control remains available to retry.
      setDisconnectError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setDisconnecting(false);
    }
  }

  // Step 49b. The backend endpoint is authoritative — this is not a
  // substitute for its own precondition check, only a secondary UI guard
  // (design doc §7). On success, the local value is set from the
  // response's persisted value, not assumed from the optimistic toggle.
  async function handleToggleCancelOrderTool() {
    const next = !toolEnabled;
    setToolError(null);
    setSavingTool(true);
    try {
      const result = await setCancelOrderToolEnabled(next);
      setToolEnabled(result.cancelOrderToolEnabled);
    } catch (err) {
      // Leave state as-is — the control remains available to retry. This is
      // also where the backend's 409 precondition rejection surfaces.
      setToolError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSavingTool(false);
    }
  }

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  const isConnected = shopify?.isActive === true;

  // Step shopify-auth-reauth (frontend design doc §3b) — drives the badge
  // and the connection detail panel only. The cancel-order-tool section
  // below is deliberately left keyed on `isConnected` alone (unchanged).
  const integrationStatus: IntegrationStatus =
    shopify?.isActive !== true
      ? "disconnected"
      : shopify.reauthorizationRequiredAt != null
        ? "requires_reauth"
        : "connected";

  // Shared between the `disconnected` state (first-time connect) and the
  // `requires_reauth` state (reconnect) — same handler, same API call
  // (connectIntegration), per design doc §3d. shopName is pre-filled from
  // the existing identifier only in the requires_reauth case (see the
  // seeding effect above); this markup itself has no branch on that.
  function renderConnectForm() {
    return (
      <form onSubmit={handleConnect} className="mt-4 border-t border-border pt-4">
        <label htmlFor="shopName" className="mb-1.5 block text-[13px] font-medium text-foreground">
          Shop name
        </label>
        <div className="flex items-start gap-2">
          <div className="flex-1">
            <input
              id="shopName"
              type="text"
              required
              placeholder="example-store"
              value={shopName}
              onChange={(e) => {
                setShopName(e.target.value);
                setShopNameError(null);
              }}
              className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20"
            />
            <p className="mt-1 text-[12.5px] text-muted-foreground/70">
              The bare shop name from your Shopify admin URL — not the full domain.
            </p>
          </div>
          <button
            type="submit"
            disabled={connecting}
            className="shrink-0 rounded-full bg-primary px-4 py-2.5 text-[13px] font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {connecting ? "Connecting…" : "Connect"}
          </button>
        </div>
        {shopNameError && <p className="mt-2 text-[13px] text-destructive">{shopNameError}</p>}
        {connectError && <p className="mt-2 text-[13px] text-destructive">{connectError}</p>}
      </form>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[720px] px-6 py-12 sm:px-10">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
          Integrations
        </h1>
        <p className="mt-2 text-sm text-muted-foreground">
          Connect external platforms so {user.businessName}&apos;s AI and team can act on real orders and data.
        </p>

        {connectedParam === SHOPIFY_PLATFORM && (
          <div className="mt-6 rounded-xl border border-success-soft bg-success-soft px-3.5 py-3 text-[13px] text-success">
            Shopify connected successfully.
          </div>
        )}

        {integrationErrorParam && (
          <div className="mt-6">
            <FormError
              message={OAUTH_ERROR_COPY[integrationErrorParam] ?? "Something went wrong connecting Shopify. Please try again."}
            />
          </div>
        )}

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : loadError ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load your integrations</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : (
          <>
            <section className="mt-8 rounded-2xl border border-border bg-card px-5 py-4">
              <div className="flex items-start justify-between gap-4">
                <div className="flex items-start gap-3">
                  <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                    <IconStore className="h-4 w-4" />
                  </span>
                  <div>
                    <p className="text-sm font-medium text-foreground">Shopify</p>
                    <p className="mt-1 text-[13px] text-muted-foreground">
                      Lets the AI look up and cancel Shopify orders on your business&apos;s behalf.
                    </p>
                  </div>
                </div>
                <span
                  className={`shrink-0 rounded-full px-2.5 py-1 text-[10.5px] font-semibold uppercase tracking-wider ${INTEGRATION_STATUS_BADGE[integrationStatus]}`}
                >
                  {INTEGRATION_STATUS_LABEL[integrationStatus]}
                </span>
              </div>

              {integrationStatus !== "disconnected" ? (
                <>
                  <div className="mt-4 flex items-center justify-between gap-4 border-t border-border pt-4">
                    <div className="min-w-0">
                      <p className="truncate text-[13px] text-foreground">{shopify?.externalAccountIdentifier}</p>
                      {shopify?.connectedAt && (
                        <p className="mt-0.5 text-[12.5px] text-muted-foreground/70">
                          Connected {formatRelativeTime(shopify.connectedAt)}
                        </p>
                      )}
                      {integrationStatus === "requires_reauth" && (
                        <p className="mt-2 text-[12.5px] text-muted-foreground/70">
                          Shopify needs to be reconnected — your last connection to Shopify stopped working. Reconnect
                          below to restore order lookups and cancellations.
                        </p>
                      )}
                      {disconnectError && <p className="mt-2 text-[13px] text-destructive">{disconnectError}</p>}
                    </div>
                    {canConnect && (
                      <button
                        type="button"
                        onClick={handleDisconnect}
                        disabled={disconnecting}
                        className="shrink-0 rounded-full border border-destructive-soft px-3.5 py-1.5 text-[13px] font-medium text-destructive transition-colors hover:bg-destructive-soft disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {disconnecting ? "Disconnecting…" : "Disconnect"}
                      </button>
                    )}
                  </div>
                  {integrationStatus === "requires_reauth" && canConnect && renderConnectForm()}
                </>
              ) : canConnect ? (
                renderConnectForm()
              ) : (
                <p className="mt-4 border-t border-border pt-4 text-[13px] text-muted-foreground">
                  Not connected. An Owner can connect Shopify from this page.
                </p>
              )}
            </section>

            <section className="mt-6 rounded-2xl border border-border bg-card px-5 py-4">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <p className="text-sm font-medium text-foreground">Cancel order tool</p>
                  <p className="mt-1 text-[13px] text-muted-foreground">
                    Lets the AI propose Shopify order cancellations for staff to approve.
                  </p>
                </div>
                {canConnect ? (
                  <button
                    type="button"
                    onClick={handleToggleCancelOrderTool}
                    disabled={savingTool || (!toolEnabled && !isConnected)}
                    aria-pressed={toolEnabled}
                    className={`shrink-0 rounded-full px-4 py-2 text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                      toolEnabled
                        ? "bg-accent-soft text-accent"
                        : "border border-border text-foreground hover:bg-muted"
                    }`}
                  >
                    {savingTool ? "Working…" : toolEnabled ? "Enabled" : "Disabled"}
                  </button>
                ) : (
                  <span className="shrink-0 text-[13px] font-medium text-muted-foreground">
                    {toolEnabled ? "Enabled" : "Disabled"}
                  </span>
                )}
              </div>

              {!toolEnabled && !isConnected && (
                <p className="mt-3 text-[12.5px] text-muted-foreground/70">Connect Shopify first to enable this.</p>
              )}
              {toolEnabled && !isConnected && (
                <p className="mt-3 text-[12.5px] text-muted-foreground/70">
                  Shopify is disconnected — the AI can propose cancellations, but they won&apos;t execute until Shopify is reconnected.
                </p>
              )}
              {toolError && <p className="mt-3 text-[13px] text-destructive">{toolError}</p>}
            </section>
          </>
        )}
      </main>
    </DashboardShell>
  );
}
