"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError, SubmitButton } from "../../components/auth/FormField";
import { IconArrowRight } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import { formatRelativeTime } from "../../lib/relative-time";
import {
  ApiError,
  getCustomer,
  linkCustomerIdentity,
  listCustomerIdentities,
  revokeCustomerIdentity,
  setCustomerExtendedLogging,
  type CustomerDetail,
  type ExternalIdentity,
  type IdentityChannel,
  type IdentityVerificationMethod,
} from "../../lib/api";

const CHANNEL_OPTIONS: { value: IdentityChannel; label: string }[] = [
  { value: "Website", label: "Website" },
  { value: "Email", label: "Email" },
];

const METHOD_OPTIONS: { value: IdentityVerificationMethod; label: string }[] = [
  { value: "SignedToken", label: "Signed token" },
  { value: "Otp", label: "OTP" },
];

function IdentityStatusBadge({ identity }: { identity: ExternalIdentity }) {
  if (identity.revokedAt) {
    return (
      <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
        Revoked
      </span>
    );
  }
  if (!identity.isCurrentlyValid) {
    return (
      <span className="rounded-full bg-destructive-soft px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-destructive">
        Expired
      </span>
    );
  }
  return (
    <span className="rounded-full bg-accent-soft px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-accent">
      Valid
    </span>
  );
}

export default function CustomerDetailPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const { user, status } = useAuth();
  const canWrite = user?.roles.some((r) => r === "Owner" || r === "Admin") ?? false;

  const [customer, setCustomer] = useState<CustomerDetail | null>(null);
  const [identities, setIdentities] = useState<ExternalIdentity[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);

  const [channel, setChannel] = useState<IdentityChannel>("Website");
  const [method, setMethod] = useState<IdentityVerificationMethod>("Otp");
  const [externalUserId, setExternalUserId] = useState("");
  const [linking, setLinking] = useState(false);
  const [linkError, setLinkError] = useState<string | null>(null);

  const [revokingId, setRevokingId] = useState<string | null>(null);
  const [togglingLogging, setTogglingLogging] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    Promise.all([getCustomer(params.id), listCustomerIdentities(params.id)])
      .then(([customerResult, identitiesResult]) => {
        if (cancelled) return;
        setCustomer(customerResult);
        setIdentities(identitiesResult);
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
  }, [user, params.id]);

  async function handleLinkIdentity(e: FormEvent) {
    e.preventDefault();
    if (!customer) return;
    setLinkError(null);
    setLinking(true);
    try {
      const created = await linkCustomerIdentity(customer.id, {
        channel,
        verificationMethod: method,
        externalUserId,
      });
      setIdentities((items) => [created, ...items]);
      setExternalUserId("");
    } catch (err) {
      setLinkError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setLinking(false);
    }
  }

  async function handleRevoke(identity: ExternalIdentity) {
    if (!customer) return;
    setRevokingId(identity.id);
    try {
      await revokeCustomerIdentity(customer.id, identity.id);
      setIdentities((items) =>
        items.map((i) => (i.id === identity.id ? { ...i, revokedAt: new Date().toISOString(), isCurrentlyValid: false } : i)),
      );
    } catch {
      // Leave state as-is — the row remains available to retry.
    } finally {
      setRevokingId(null);
    }
  }

  async function handleToggleExtendedLogging() {
    if (!customer) return;
    const next = !customer.extendedLoggingEnabled;
    setTogglingLogging(true);
    try {
      await setCustomerExtendedLogging(customer.id, next);
      setCustomer({ ...customer, extendedLoggingEnabled: next });
    } catch {
      // Leave state as-is — the toggle remains available to retry.
    } finally {
      setTogglingLogging(false);
    }
  }

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[720px] px-6 py-12 sm:px-10">
        <Link
          href="/customers"
          className="inline-flex items-center gap-1.5 text-[13px] font-medium text-muted-foreground hover:text-foreground"
        >
          <IconArrowRight className="h-3.5 w-3.5 rotate-180" />
          Back to customers
        </Link>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : loadError || !customer ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load this customer</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server, or it doesn&apos;t belong to your business.
            </p>
          </div>
        ) : (
          <>
            <div className="mt-4 flex items-start justify-between gap-4">
              <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
                {customer.name ?? customer.email ?? "Unnamed customer"}
              </h1>
              {customer.isAnonymized && (
                <span className="mt-1 shrink-0 rounded-full bg-destructive-soft px-2.5 py-1 text-[10.5px] font-semibold uppercase tracking-wider text-destructive">
                  Anonymized
                </span>
              )}
            </div>
            {customer.name && customer.email && (
              <p className="mt-1 text-sm text-muted-foreground">{customer.email}</p>
            )}

            {/* ── Extended logging ─────────────────────────────────── */}
            <section className="mt-8 rounded-2xl border border-border bg-card px-5 py-4">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <p className="text-sm font-medium text-foreground">Extended fraud-monitoring logging</p>
                  <p className="mt-1 text-[13px] text-muted-foreground">
                    Flags this customer for closer read-activity logging when enabled.
                  </p>
                </div>
                {canWrite ? (
                  <button
                    type="button"
                    onClick={handleToggleExtendedLogging}
                    disabled={togglingLogging}
                    aria-pressed={customer.extendedLoggingEnabled}
                    className={`shrink-0 rounded-full px-4 py-2 text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                      customer.extendedLoggingEnabled
                        ? "bg-accent-soft text-accent"
                        : "border border-border text-foreground hover:bg-muted"
                    }`}
                  >
                    {togglingLogging ? "Working…" : customer.extendedLoggingEnabled ? "Enabled" : "Disabled"}
                  </button>
                ) : (
                  <span className="shrink-0 text-[13px] font-medium text-muted-foreground">
                    {customer.extendedLoggingEnabled ? "Enabled" : "Disabled"}
                  </span>
                )}
              </div>
            </section>

            {/* ── Identities ────────────────────────────────────────── */}
            <section className="mt-8">
              <h2 className="text-sm font-semibold text-foreground">Linked identities</h2>

              {identities.length === 0 ? (
                <p className="mt-3 text-[13px] text-muted-foreground">No identities linked yet.</p>
              ) : (
                <div className="mt-3 divide-y divide-border overflow-hidden rounded-2xl border border-border bg-card">
                  {identities.map((identity) => (
                    <div key={identity.id} className="flex items-center justify-between gap-4 px-5 py-4">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="text-sm font-medium text-foreground">{identity.channel}</span>
                          <span className="text-[13px] text-muted-foreground">{identity.verificationMethod}</span>
                          <IdentityStatusBadge identity={identity} />
                        </div>
                        <p className="mt-1 truncate text-[13px] text-muted-foreground">{identity.externalUserId}</p>
                        <p className="mt-0.5 text-[12.5px] text-muted-foreground/70">
                          Linked {formatRelativeTime(identity.linkedAt)}
                        </p>
                      </div>
                      {canWrite && !identity.revokedAt && (
                        <button
                          type="button"
                          onClick={() => handleRevoke(identity)}
                          disabled={revokingId === identity.id}
                          className="shrink-0 rounded-full border border-destructive-soft px-3.5 py-1.5 text-[13px] font-medium text-destructive transition-colors hover:bg-destructive-soft disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {revokingId === identity.id ? "Revoking…" : "Revoke"}
                        </button>
                      )}
                    </div>
                  ))}
                </div>
              )}

              {canWrite && (
                customer.isAnonymized ? (
                  <p className="mt-4 rounded-xl border border-border bg-muted/40 px-3.5 py-3 text-[13px] text-muted-foreground">
                    This customer has been anonymized and can no longer be linked to a new identity.
                  </p>
                ) : (
                  <form onSubmit={handleLinkIdentity} className="mt-4 flex flex-col gap-3 rounded-2xl border border-border bg-card p-5">
                    <p className="text-[13px] font-medium text-foreground">Link a new identity</p>
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <label htmlFor="channel" className="mb-1.5 block text-[13px] font-medium text-foreground">
                          Channel
                        </label>
                        <select
                          id="channel"
                          value={channel}
                          onChange={(e) => setChannel(e.target.value as IdentityChannel)}
                          className="w-full cursor-pointer rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
                        >
                          {CHANNEL_OPTIONS.map((opt) => (
                            <option key={opt.value} value={opt.value}>
                              {opt.label}
                            </option>
                          ))}
                        </select>
                      </div>
                      <div>
                        <label htmlFor="method" className="mb-1.5 block text-[13px] font-medium text-foreground">
                          Verification method
                        </label>
                        <select
                          id="method"
                          value={method}
                          onChange={(e) => setMethod(e.target.value as IdentityVerificationMethod)}
                          className="w-full cursor-pointer rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
                        >
                          {METHOD_OPTIONS.map((opt) => (
                            <option key={opt.value} value={opt.value}>
                              {opt.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    </div>
                    <div>
                      <label htmlFor="externalUserId" className="mb-1.5 block text-[13px] font-medium text-foreground">
                        External user ID
                      </label>
                      <input
                        id="externalUserId"
                        type="text"
                        required
                        value={externalUserId}
                        onChange={(e) => setExternalUserId(e.target.value)}
                        placeholder="e.g. the verified email or storefront customer ID"
                        className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20"
                      />
                    </div>
                    {linkError && <FormError message={linkError} />}
                    <SubmitButton loading={linking}>Link identity</SubmitButton>
                  </form>
                )
              )}
            </section>

            {/* ── Conversation history ─────────────────────────────── */}
            <section className="mt-8">
              <h2 className="text-sm font-semibold text-foreground">Recent conversations</h2>
              {customer.recentConversations.length === 0 ? (
                <p className="mt-3 text-[13px] text-muted-foreground">No conversations yet.</p>
              ) : (
                <div className="mt-3 divide-y divide-border overflow-hidden rounded-2xl border border-border bg-card">
                  {customer.recentConversations.map((conv) => (
                    <Link
                      key={conv.id}
                      href={`/conversations/${conv.id}`}
                      className="flex items-center justify-between gap-4 px-5 py-4 transition-colors hover:bg-muted/50"
                    >
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="text-sm font-medium text-foreground">{conv.channel}</span>
                          <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
                            {conv.status}
                          </span>
                          <span className="text-[12.5px] text-muted-foreground">{conv.handledBy}</span>
                        </div>
                        <p className="mt-1 text-[12.5px] text-muted-foreground/70">
                          {conv.lastMessageAt
                            ? `Active ${formatRelativeTime(conv.lastMessageAt)}`
                            : `Started ${formatRelativeTime(conv.createdAt)}`}
                        </p>
                      </div>
                    </Link>
                  ))}
                </div>
              )}
            </section>
          </>
        )}
      </main>
    </DashboardShell>
  );
}
