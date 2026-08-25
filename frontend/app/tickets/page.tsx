"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconMessageSquare } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { formatRelativeTime } from "../lib/relative-time";
import { listTickets, type Ticket, type TicketCursor, type TicketStatus } from "../lib/api";

const STATUS_BADGE: Record<TicketStatus, string> = {
  Open: "bg-accent-soft text-accent",
  InProgress: "bg-muted text-foreground",
  Resolved: "bg-muted text-muted-foreground",
  Reopened: "bg-destructive-soft text-destructive",
};

const PRIORITY_LABEL: Record<string, string> = {
  Low: "Low",
  Medium: "Medium",
  High: "High",
  Urgent: "Urgent",
};

const STATUS_FILTERS: { value: TicketStatus | ""; label: string }[] = [
  { value: "", label: "All" },
  { value: "Open", label: "Open" },
  { value: "InProgress", label: "In progress" },
  { value: "Resolved", label: "Resolved" },
  { value: "Reopened", label: "Reopened" },
];

export default function TicketsPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [statusFilter, setStatusFilter] = useState<TicketStatus | "">("");
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [nextCursor, setNextCursor] = useState<TicketCursor | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    // Only the very first load shows the full-page spinner (`loading`
    // starts true and is only ever set false below). Switching the status
    // filter afterward re-fetches in place — the previous page's tickets
    // stay visible until the new filtered list resolves, same tolerance
    // "Load more" already has for its own in-flight window.
    listTickets({ take: 20, status: statusFilter || undefined })
      .then((result) => {
        if (cancelled) return;
        setTickets(result.tickets);
        setNextCursor(result.nextCursor);
        setError(false);
      })
      .catch(() => {
        if (!cancelled) setError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user, statusFilter]);

  async function handleLoadMore() {
    if (!nextCursor || loadingMore) return;
    setLoadingMore(true);
    try {
      const result = await listTickets({ take: 20, status: statusFilter || undefined, before: nextCursor });
      setTickets((items) => [...items, ...result.tickets]);
      setNextCursor(result.nextCursor);
    } catch {
      // Leave the already-loaded page intact — the "Load more" button
      // simply stays available to retry.
    } finally {
      setLoadingMore(false);
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
      <main className="mx-auto max-w-[820px] px-6 py-12 sm:px-10">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Tickets</h1>
            <p className="mt-2 text-sm text-muted-foreground">
              Support cases for {user.businessName}, claimed and tracked by your team.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            {STATUS_FILTERS.map((f) => (
              <button
                key={f.value}
                type="button"
                onClick={() => setStatusFilter(f.value)}
                className={`rounded-full border px-3.5 py-1.5 text-[13px] font-medium transition-colors ${
                  statusFilter === f.value
                    ? "border-accent bg-accent-soft text-accent"
                    : "border-border bg-card text-muted-foreground hover:bg-muted"
                }`}
              >
                {f.label}
              </button>
            ))}
          </div>
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load tickets</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : tickets.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconMessageSquare className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No tickets yet</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Tickets created for customer escalations will show up here.
            </p>
          </div>
        ) : (
          <>
            <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
              {tickets.map((t) => (
                <Link
                  key={t.id}
                  href={`/tickets/${t.id}`}
                  className="flex items-start gap-4 px-6 py-5 transition-colors hover:bg-muted/50"
                >
                  <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                    <IconMessageSquare className="h-4 w-4" />
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-medium text-foreground">{t.channel}</span>
                      <span className={`rounded-full px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider ${STATUS_BADGE[t.status]}`}>
                        {t.status}
                      </span>
                      <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
                        {PRIORITY_LABEL[t.priority] ?? t.priority}
                      </span>
                    </div>
                    {t.summary && <p className="mt-1.5 truncate text-[13px] text-muted-foreground">{t.summary}</p>}
                    <p className="mt-1 text-[12.5px] text-muted-foreground/70">Opened {formatRelativeTime(t.createdAt)}</p>
                  </div>
                </Link>
              ))}
            </div>

            {nextCursor && (
              <div className="mt-6 flex justify-center">
                <button
                  type="button"
                  onClick={handleLoadMore}
                  disabled={loadingMore}
                  className="rounded-full border border-border bg-card px-5 py-2 text-[13px] font-medium text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {loadingMore ? "Loading…" : "Load more"}
                </button>
              </div>
            )}
          </>
        )}
      </main>
    </DashboardShell>
  );
}
