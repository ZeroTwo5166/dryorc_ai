import type { StatusCountEntry } from "../../lib/api";

const COLOR_FOR: Record<string, string> = {
  Ai: "bg-accent",
  Human: "bg-pink",
};

// Deliberately its own component, not a reuse of DistributionChart, even
// though both consume StatusCountEntry[] — HandledBySnapshot is an
// all-history current-state snapshot (backend/Services/AnalyticsQueryService.cs
// GetHandledBySnapshotAsync ignores the range window entirely; locked by
// docs/handoffs/step-21-analytics-design.md §9 Decision 1), not a period-
// scoped distribution like ConversationStatusDistribution/
// TicketStatusDistribution. Sharing one component for both would make an
// all-time snapshot and a this-period breakdown look like the same kind of
// metric — the dashed border/muted surface/"All time" badge/split-meter
// shape here is what keeps that from happening, not the label text alone.
export function HandledBySnapshotPanel({ entries }: { entries: StatusCountEntry[] }) {
  const total = entries.reduce((sum, e) => sum + e.count, 0) || 1;

  return (
    <div className="rounded-2xl border border-dashed border-border bg-muted/40 p-5">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold text-foreground">Current handling</h3>
        <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
          All time
        </span>
      </div>
      <p className="mt-1 text-[12px] text-muted-foreground">
        Every conversation this business has ever had — not affected by the date range below.
      </p>

      <div className="mt-4 flex h-3 w-full overflow-hidden rounded-full">
        {entries.map((e) => (
          <div
            key={e.status}
            className={COLOR_FOR[e.status] ?? "bg-muted-foreground"}
            style={{ width: `${(e.count / total) * 100}%` }}
          />
        ))}
      </div>

      <div className="mt-3 flex flex-wrap gap-4">
        {entries.map((e) => (
          <div key={e.status} className="flex items-center gap-1.5 text-[13px]">
            <span className={`h-2 w-2 rounded-full ${COLOR_FOR[e.status] ?? "bg-muted-foreground"}`} />
            <span className="font-medium text-foreground">{e.status}</span>
            <span className="text-muted-foreground">{e.count}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
