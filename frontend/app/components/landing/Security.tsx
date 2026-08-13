import { AuditLogPanel, TenantIsolationPanel } from "./Mockups";

export function Security() {
  return (
    <section
      id="security"
      className="border-b border-border bg-gradient-to-b from-purple-soft/50 to-background"
    >
      <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
        <p className="text-sm font-medium text-purple">Security</p>
        <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          Built to be trusted with your customers&apos; data.
        </h2>

        <div className="mt-12 grid gap-10 md:grid-cols-2">
          <div>
            <h3 className="text-base font-semibold text-foreground">
              Tenant isolation, fail-closed
            </h3>
            <p className="mt-2 max-w-sm text-sm leading-relaxed text-muted-foreground">
              Every query is scoped to a business by default. If tenant
              context can&apos;t be resolved, it returns nothing — never
              someone else&apos;s data.
            </p>
            <div className="mt-6">
              <TenantIsolationPanel />
            </div>
          </div>
          <div>
            <h3 className="text-base font-semibold text-foreground">
              Append-only audit trail
            </h3>
            <p className="mt-2 max-w-sm text-sm leading-relaxed text-muted-foreground">
              Approvals, refunds, handoffs — every authorization-relevant
              action is logged and kept, so you can reconstruct exactly what
              happened.
            </p>
            <div className="mt-6">
              <AuditLogPanel />
            </div>
          </div>
        </div>

        <div className="mt-12 flex flex-col gap-6 border-t border-border pt-8 sm:flex-row">
          <p className="max-w-sm text-sm leading-relaxed text-muted-foreground">
            <span className="font-semibold text-foreground">
              Nivora executes, the model never does.
            </span>{" "}
            The AI can propose a cancellation or refund. It cannot call your
            commerce platform directly.
          </p>
          <p className="max-w-sm text-sm leading-relaxed text-muted-foreground">
            <span className="font-semibold text-foreground">
              Disclosed, not disguised.
            </span>{" "}
            Customers are told they&apos;re talking to an AI, per the EU AI
            Act&apos;s Article 50 — by default.
          </p>
        </div>
      </div>
    </section>
  );
}