"use client";

import { Suspense, useEffect, useState, type FormEvent } from "react";
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

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

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
      // re-fetch (design doc §9).
      setShopify((prev) => (prev ? { ...prev, isActive: false, disconnectedAt: new Date().toISOString() } : prev));
    } catch (err) {
      // Leave state as-is — the control remains available to retry.
      setDisconnectError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setDisconnecting(false);
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
                className={`shrink-0 rounded-full px-2.5 py-1 text-[10.5px] font-semibold uppercase tracking-wider ${
                  isConnected ? "bg-accent-soft text-accent" : "bg-muted text-muted-foreground"
                }`}
              >
                {isConnected ? "Connected" : "Disconnected"}
              </span>
            </div>

            {isConnected ? (
              <div className="mt-4 flex items-center justify-between gap-4 border-t border-border pt-4">
                <div className="min-w-0">
                  <p className="truncate text-[13px] text-foreground">{shopify?.externalAccountIdentifier}</p>
                  {shopify?.connectedAt && (
                    <p className="mt-0.5 text-[12.5px] text-muted-foreground/70">
                      Connected {formatRelativeTime(shopify.connectedAt)}
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
            ) : canConnect ? (
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
            ) : (
              <p className="mt-4 border-t border-border pt-4 text-[13px] text-muted-foreground">
                Not connected. An Owner can connect Shopify from this page.
              </p>
            )}
          </section>
        )}
      </main>
    </DashboardShell>
  );
}
