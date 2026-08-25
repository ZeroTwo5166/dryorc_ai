"use client";

import { useEffect, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError } from "../../components/auth/FormField";
import { IconArrowRight } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import { formatRelativeTime } from "../../lib/relative-time";
import {
  ApiError,
  claimTicket,
  getCustomer,
  getTicket,
  reopenTicket,
  resolveTicket,
  unclaimTicket,
  updateTicketPriority,
  type CustomerDetail,
  type Ticket,
  type TicketPriority,
} from "../../lib/api";

const PRIORITY_OPTIONS: { value: TicketPriority; label: string }[] = [
  { value: "Low", label: "Low" },
  { value: "Medium", label: "Medium" },
  { value: "High", label: "High" },
  { value: "Urgent", label: "Urgent" },
];

const STATUS_BADGE: Record<string, string> = {
  Open: "bg-accent-soft text-accent",
  InProgress: "bg-muted text-foreground",
  Resolved: "bg-muted text-muted-foreground",
  Reopened: "bg-destructive-soft text-destructive",
};

export default function TicketDetailPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const { user, status } = useAuth();

  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [customer, setCustomer] = useState<CustomerDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);

  const [mutating, setMutating] = useState(false);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const [priorityDraft, setPriorityDraft] = useState<TicketPriority>("Low");

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    getTicket(params.id)
      .then(async (result) => {
        if (cancelled) return;
        setTicket(result);
        setPriorityDraft(result.priority);
        setLoadError(false);
        try {
          const customerResult = await getCustomer(result.customerId);
          if (!cancelled) setCustomer(customerResult);
        } catch {
          // Customer info is a nice-to-have on this page — the ticket
          // itself already loaded successfully, so this failure alone
          // must not block the page.
        }
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

  async function runMutation(action: () => Promise<null>) {
    if (!ticket) return;
    setMutationError(null);
    setMutating(true);
    try {
      await action();
      const refreshed = await getTicket(ticket.id);
      setTicket(refreshed);
      setPriorityDraft(refreshed.priority);
    } catch (err) {
      setMutationError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setMutating(false);
    }
  }

  async function handlePriorityChange(next: TicketPriority) {
    if (!ticket || next === ticket.priority) {
      setPriorityDraft(next);
      return;
    }
    setPriorityDraft(next);
    await runMutation(() => updateTicketPriority(ticket.id, next));
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
      <main className="mx-auto max-w-[680px] px-6 py-12 sm:px-10">
        <Link
          href="/tickets"
          className="inline-flex items-center gap-1.5 text-[13px] font-medium text-muted-foreground hover:text-foreground"
        >
          <IconArrowRight className="h-3.5 w-3.5 rotate-180" />
          Back to tickets
        </Link>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : loadError || !ticket ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load this ticket</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server, or it doesn&apos;t belong to your business.
            </p>
          </div>
        ) : (
          <>
            <div className="mt-4 flex items-start justify-between gap-4">
              <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
                {ticket.summary ?? `${ticket.channel} ticket`}
              </h1>
              <span className={`mt-1 shrink-0 rounded-full px-2.5 py-1 text-[10.5px] font-semibold uppercase tracking-wider ${STATUS_BADGE[ticket.status] ?? "bg-muted text-muted-foreground"}`}>
                {ticket.status}
              </span>
            </div>

            {/* ── Details ───────────────────────────────────────────── */}
            <section className="mt-8 grid grid-cols-2 gap-4 rounded-2xl border border-border bg-card p-5 text-[13px]">
              <div>
                <p className="text-muted-foreground">Customer</p>
                <p className="mt-1 font-medium text-foreground">
                  {customer ? (customer.name ?? customer.email ?? "Unnamed customer") : ticket.customerId}
                </p>
              </div>
              <div>
                <p className="text-muted-foreground">Channel</p>
                <p className="mt-1 font-medium text-foreground">{ticket.channel}</p>
              </div>
              <div>
                <p className="text-muted-foreground">Priority reason</p>
                <p className="mt-1 font-medium text-foreground">{ticket.priorityReason}</p>
              </div>
              <div>
                <p className="text-muted-foreground">SLA deadline</p>
                <p className="mt-1 font-medium text-foreground">
                  {ticket.slaDeadline ? formatRelativeTime(ticket.slaDeadline) : "No SLA configured"}
                </p>
              </div>
              <div>
                <p className="text-muted-foreground">First response</p>
                <p className="mt-1 font-medium text-foreground">
                  {ticket.firstRespondedAt ? formatRelativeTime(ticket.firstRespondedAt) : "Not yet responded"}
                </p>
              </div>
              <div>
                <p className="text-muted-foreground">Opened</p>
                <p className="mt-1 font-medium text-foreground">{formatRelativeTime(ticket.createdAt)}</p>
              </div>
              {ticket.conversationId && (
                <div className="col-span-2">
                  <Link href={`/conversations/${ticket.conversationId}`} className="text-accent hover:underline">
                    View source conversation
                  </Link>
                </div>
              )}
            </section>

            {/* ── Priority ──────────────────────────────────────────── */}
            <section className="mt-6 flex items-center justify-between gap-4 rounded-2xl border border-border bg-card px-5 py-4">
              <p className="text-sm font-medium text-foreground">Priority</p>
              <select
                value={priorityDraft}
                onChange={(e) => handlePriorityChange(e.target.value as TicketPriority)}
                disabled={mutating}
                className="cursor-pointer rounded-xl border border-border bg-card px-3.5 py-2 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {PRIORITY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </section>

            {/* ── Lifecycle actions ─────────────────────────────────── */}
            <section className="mt-6 flex flex-wrap gap-3">
              {ticket.status === "Open" && (
                <button
                  type="button"
                  onClick={() => runMutation(() => claimTicket(ticket.id))}
                  disabled={mutating}
                  className="rounded-full bg-primary px-4 py-2 text-[13px] font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {mutating ? "Working…" : "Claim"}
                </button>
              )}
              {ticket.status === "InProgress" && (
                <>
                  <button
                    type="button"
                    onClick={() => runMutation(() => unclaimTicket(ticket.id))}
                    disabled={mutating}
                    className="rounded-full border border-border px-4 py-2 text-[13px] font-medium text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {mutating ? "Working…" : "Unclaim"}
                  </button>
                  <button
                    type="button"
                    onClick={() => runMutation(() => resolveTicket(ticket.id))}
                    disabled={mutating}
                    className="rounded-full bg-primary px-4 py-2 text-[13px] font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {mutating ? "Working…" : "Resolve"}
                  </button>
                </>
              )}
              {ticket.status === "Resolved" && (
                <button
                  type="button"
                  onClick={() => runMutation(() => reopenTicket(ticket.id))}
                  disabled={mutating}
                  className="rounded-full border border-destructive-soft px-4 py-2 text-[13px] font-medium text-destructive transition-colors hover:bg-destructive-soft disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {mutating ? "Working…" : "Reopen"}
                </button>
              )}
            </section>

            {mutationError && (
              <div className="mt-4">
                <FormError message={mutationError} />
              </div>
            )}
          </>
        )}
      </main>
    </DashboardShell>
  );
}
