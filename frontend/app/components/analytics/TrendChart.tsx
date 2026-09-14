import type { DayCountEntry } from "../../lib/api";

// Renders a genuine day-by-day trend (Conversations/Handoffs by day) —
// only DayCountEntry[]-shaped fields should ever go through this component.
// StatusCountEntry[]-shaped fields (distributions, the all-time snapshot)
// are a different metric kind and use DistributionChart/
// HandledBySnapshotPanel instead — never this component, even though both
// are "just an array of counts."
export function TrendChart({ title, points }: { title: string; points: DayCountEntry[] }) {
  const max = Math.max(1, ...points.map((p) => p.count));
  const barWidth = 6;
  const gap = 2;
  const chartHeight = 64;
  const viewWidth = points.length * (barWidth + gap);

  return (
    <div className="rounded-2xl border border-border bg-card p-5 shadow-sm shadow-slate-900/5">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>

      {/* Color (fill-accent) is decorative only here — every bar carries its
          exact date/count as an accessible <title>, and the single-series
          case needs no legend per the dataviz skill's own rule (a lone
          series' identity is already named by the chart title above). */}
      <svg
        role="img"
        aria-label={`${title}, one bar per day`}
        viewBox={`0 0 ${viewWidth} ${chartHeight}`}
        preserveAspectRatio="none"
        className="mt-4 h-16 w-full"
      >
        {points.map((p, i) => {
          const height = Math.max(2, (p.count / max) * (chartHeight - 4));
          return (
            <rect
              key={p.day}
              x={i * (barWidth + gap)}
              y={chartHeight - height}
              width={barWidth}
              height={height}
              rx={2}
              className="fill-accent"
            >
              <title>{`${p.day}: ${p.count}`}</title>
            </rect>
          );
        })}
      </svg>

      <div className="mt-2 flex justify-between text-[11px] text-muted-foreground">
        <span>{points[0]?.day ?? "—"}</span>
        <span>{points[points.length - 1]?.day ?? "—"}</span>
      </div>
    </div>
  );
}
