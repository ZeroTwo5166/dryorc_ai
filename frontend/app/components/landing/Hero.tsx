import brand from "@/brand.json";
import { StatusPill } from "./Mockups";

export function Hero() {
  return (
    <section
      id="top"
      className="border-b border-border bg-gradient-to-b from-accent-soft/60 to-background"
    >
      <div className="mx-auto grid max-w-[1440px] gap-16 px-6 py-20 sm:px-10 lg:grid-cols-2 lg:items-center lg:px-14 lg:py-28">
        <div>
          <span className="inline-flex items-center gap-2 rounded-full border border-border bg-card px-4 py-1.5 text-xs font-medium text-muted-foreground shadow-sm">
            AI customer support
            <span className="text-muted-foreground">·</span>
            <span className="font-semibold text-success">EU-hosted</span>
          </span>

          <h1 className="mt-8 max-w-xl text-4xl font-semibold leading-[1.1] tracking-tight text-foreground sm:text-5xl lg:text-6xl">
            Customer support that{" "}
            <span className="bg-gradient-to-r from-accent to-purple bg-clip-text text-transparent">
              actually acts.
            </span>
          </h1>

          <p className="mt-6 max-w-md text-lg leading-relaxed text-muted-foreground">
            {brand.name} answers from your own knowledge base, verifies who
            it&apos;s talking to, and takes real action on orders and
            refunds — with a human in the loop for anything risky.
          </p>

          <div className="mt-10 flex flex-wrap items-center gap-4">
            <a
              href={`mailto:${brand.email}`}
              className="inline-flex items-center gap-2 rounded-full bg-primary px-7 py-3.5 text-sm font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90"
            >
              Book a demo &rarr;
            </a>
            <a
              href="#how-it-works"
              className="text-sm font-medium text-foreground underline decoration-border underline-offset-4 transition-colors hover:decoration-accent"
            >
              See how it works
            </a>
          </div>

          <div className="mt-12 flex flex-wrap gap-3">
            <StatusPill label="No action without approval" tone="accent" />
            <StatusPill label="Every action audit-logged" tone="purple" />
            <StatusPill label="AI disclosure built in" tone="success" />
          </div>
        </div>

        <div className="relative mx-auto w-full max-w-md lg:mx-0">
          <div className="absolute -left-4 -top-6 z-10 hidden rotate-[-4deg] sm:block">
            <StatusPill label="Refund approved · €42" tone="success" />
          </div>
          <div className="absolute -right-14 top-1/3 z-10 hidden translate-x-1/2 rotate-[3deg] sm:block">
            <StatusPill label="AI replied in 12s" tone="purple" />
          </div>

          <div className="w-full rounded-3xl border border-border bg-card p-1.5 shadow-xl shadow-slate-900/10">
            <div className="overflow-hidden rounded-[1.25rem] border border-border">
              <div className="flex items-center justify-between border-b border-border px-5 py-4">
                <span className="text-sm font-semibold text-foreground">
                  Conversation #10482
                </span>
                <StatusPill label="Level 2 verified" tone="accent" />
              </div>

              {[
                ["Channel", "Website"],
                ["Identity", "Verified · email OTP"],
                ["Intent", "refund_order"],
                ["Proposed by", "AI"],
              ].map(([label, value]) => (
                <div
                  key={label}
                  className="flex items-center justify-between border-b border-border px-5 py-3.5 text-sm"
                >
                  <span className="text-muted-foreground">{label}</span>
                  <span className="font-medium text-foreground">{value}</span>
                </div>
              ))}

              <div className="flex items-center justify-between border-b border-border bg-muted px-5 py-3.5">
                <span className="text-sm text-muted-foreground">Status</span>
                <StatusPill label="Pending human approval" tone="orange" />
              </div>

              <div className="flex items-center justify-between px-5 py-3.5 text-sm">
                <span className="text-muted-foreground">Audit log</span>
                <span className="font-medium text-foreground">
                  Appended · 3 events
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}