"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { DashboardShell } from "../components/layout/DashboardShell";
import { DistributionChart } from "../components/analytics/DistributionChart";
import { HandledBySnapshotPanel } from "../components/analytics/HandledBySnapshotPanel";
import { TrendChart } from "../components/analytics/TrendChart";
import { useAuth } from "../lib/auth-context";
import { getAnalytics, type AnalyticsRange, type AnalyticsResponse } from "../lib/api";

const RANGE_FILTERS: { value: AnalyticsRange; label: string }[] = [
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
  { value: "90d", label: "90 days" },
];

// Color assignment below is deliberately not "pick whatever looks nice" —
// each set was chosen and verified with the dataviz skill's
// validate_palette.js (CVD-safe adjacent pairs, both light and dark chart
// surfaces) before being wired in here. ConversationStatus/TicketStatus are
// this page's two *period-scoped distributions*; the ordering below is the
// actual display order (enum declaration order, matching what the backend
// already zero-fills in), which is exactly the adjacency the validator
// checked — reordering these arrays without re-validating would silently
// invalidate that check.
const CONVERSATION_STATUS_COLOR: Record<string, string> = {
  Open: "bg-accent",
  Closed: "bg-success",
};

const TICKET_STATUS_COLOR: Record<string, string> = {
  Open: "bg-accent",
  InProgress: "bg-chart-orange",
  Resolved: "bg-success",
  Reopened: "bg-purple",
};

const TICKET_STATUS_LABEL: Record<string, string> = {
  Open: "Open",
  InProgress: "In progress",
  Resolved: "Resolved",
  Reopened: "Reopened",
};

export default function AnalyticsPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [range, setRange] = useState<AnalyticsRange>("7d");
  const [data, setData] = useState<AnalyticsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  // Same latest-request-wins guard as /tickets, and the same "only the very
  // first load shows the full-page spinner" tolerance — a range change
  // re-fetches in place and leaves the previously-loaded charts visible
  // until the new range's data resolves, rather than flashing back to a
  // spinner. A slow first request could otherwise still resolve after a
  // fast second one and clobber it with stale data.
  const latestRequestId = useRef(0);
  const fetchAnalytics = useCallback((selectedRange: AnalyticsRange) => {
    const requestId = ++latestRequestId.current;
    return getAnalytics(selectedRange)
      .then((result) => {
        if (latestRequestId.current !== requestId) return;
        setData(result);
        setError(false);
      })
      .catch(() => {
        if (latestRequestId.current !== requestId) return;
        setError(true);
      })
      .finally(() => {
        if (latestRequestId.current !== requestId) return;
        setLoading(false);
      });
  }, []);

  useEffect(() => {
    if (!user) return;
    void fetchAnalytics(range);
  }, [user, range, fetchAnalytics]);

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[1100px] px-6 py-12 sm:px-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Analytics</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            How {user.businessName}&apos;s conversations, handoffs, and tickets have been trending.
          </p>
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load analytics</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : data ? (
          <>
            {/* All-time snapshot — placed above the range selector,
                deliberately, so its own "not affected by the range below"
                caption reads as literally true, not as a claim contradicted
                by its own position on the page. */}
            <div className="mt-8">
              <HandledBySnapshotPanel entries={data.handledBySnapshot} />
            </div>

            <div className="mt-8 flex flex-wrap items-center gap-2">
              <span className="text-[13px] font-medium text-muted-foreground">Period:</span>
              {RANGE_FILTERS.map((f) => (
                <button
                  key={f.value}
                  type="button"
                  onClick={() => setRange(f.value)}
                  className={`rounded-full border px-3.5 py-1.5 text-[13px] font-medium transition-colors ${
                    range === f.value
                      ? "border-accent bg-accent-soft text-accent"
                      : "border-border bg-card text-muted-foreground hover:bg-muted"
                  }`}
                >
                  {f.label}
                </button>
              ))}
            </div>

            <section className="mt-6">
              <h2 className="text-[13px] font-semibold uppercase tracking-wider text-muted-foreground">Trends</h2>
              <div className="mt-3 grid gap-4 sm:grid-cols-2">
                <TrendChart title="Conversations by day" points={data.conversationsByDay} />
                <TrendChart title="Handoffs by day" points={data.handoffsByDay} />
              </div>
            </section>

            <section className="mt-8">
              <h2 className="text-[13px] font-semibold uppercase tracking-wider text-muted-foreground">
                This period&apos;s composition
              </h2>
              <div className="mt-3 grid gap-4 sm:grid-cols-2">
                <DistributionChart
                  title="Conversation status"
                  entries={data.conversationStatusDistribution}
                  colorFor={(s) => CONVERSATION_STATUS_COLOR[s] ?? "bg-muted-foreground"}
                />
                <DistributionChart
                  title="Ticket status"
                  entries={data.ticketStatusDistribution}
                  colorFor={(s) => TICKET_STATUS_COLOR[s] ?? "bg-muted-foreground"}
                  labelFor={(s) => TICKET_STATUS_LABEL[s] ?? s}
                />
              </div>
            </section>
          </>
        ) : null}
      </main>
    </DashboardShell>
  );
}
