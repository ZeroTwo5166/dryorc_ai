import type { StatusCountEntry } from "../../lib/api";

// Renders a period-scoped composition breakdown (Conversation/Ticket status
// distribution) — NOT a trend. Every slice's category name and count are
// real DOM text, independent of `colorClass`, so identity/value never
// depends on color alone. `colorClass` order/choice is the caller's
// responsibility — see analytics/page.tsx for the CVD-validated assignment
// per metric (validated with the dataviz skill's validate_palette.js, not
// eyeballed).
export function DistributionChart({
  title,
  entries,
  colorFor,
  labelFor,
}: {
  title: string;
  entries: StatusCountEntry[];
  colorFor: (status: string) => string;
  labelFor?: (status: string) => string;
}) {
  const max = Math.max(1, ...entries.map((e) => e.count));

  return (
    <div className="rounded-2xl border border-border bg-card p-5 shadow-sm shadow-slate-900/5">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      <p className="mt-1 text-[12px] text-muted-foreground">This period</p>

      <div className="mt-4 space-y-3">
        {entries.map((e) => (
          <div key={e.status}>
            <div className="flex items-center justify-between gap-2 text-[13px]">
              <span className="flex items-center gap-1.5 font-medium text-foreground">
                <span className={`h-2 w-2 shrink-0 rounded-full ${colorFor(e.status)}`} />
                {labelFor ? labelFor(e.status) : e.status}
              </span>
              <span className="text-muted-foreground">{e.count}</span>
            </div>
            <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-muted">
              <div
                className={`h-full rounded-full ${colorFor(e.status)}`}
                style={{ width: `${(e.count / max) * 100}%` }}
              />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
