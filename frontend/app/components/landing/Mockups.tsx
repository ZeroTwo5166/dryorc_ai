"use client";

import { useState } from "react";
import brand from "@/brand.json";
import { IconStore } from "./icons";

type Tone = "accent" | "success" | "purple" | "orange" | "pink" | "muted" | "destructive";

const toneClasses: Record<Tone, { dot: string; pillBg: string; pillText: string }> = {
  accent: { dot: "bg-accent", pillBg: "bg-accent-soft", pillText: "text-accent" },
  success: { dot: "bg-success", pillBg: "bg-success-soft", pillText: "text-success" },
  purple: { dot: "bg-purple", pillBg: "bg-purple-soft", pillText: "text-purple" },
  orange: { dot: "bg-orange", pillBg: "bg-orange-soft", pillText: "text-orange" },
  pink: { dot: "bg-pink", pillBg: "bg-pink-soft", pillText: "text-pink" },
  destructive: { dot: "bg-destructive", pillBg: "bg-destructive-soft", pillText: "text-destructive" },
  muted: { dot: "bg-muted-foreground", pillBg: "bg-muted", pillText: "text-muted-foreground" },
};

export function StatusPill({ label, tone = "accent" }: { label: string; tone?: Tone }) {
  const t = toneClasses[tone];
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full ${t.pillBg} px-2.5 py-1 text-[11px] font-medium ${t.pillText}`}
    >
      <span className={`h-1.5 w-1.5 rounded-full ${t.dot}`} />
      {label}
    </span>
  );
}

function PanelShell({
  title,
  status,
  statusTone = "accent",
  secondaryStatus,
  secondaryStatusTone = "success",
  children,
}: {
  title: string;
  status?: string;
  statusTone?: Tone;
  secondaryStatus?: string;
  secondaryStatusTone?: Tone;
  children: React.ReactNode;
}) {
  return (
    <div className="w-full overflow-hidden rounded-2xl border border-border bg-card shadow-sm shadow-slate-900/5">
      <div className="flex items-center justify-between border-b border-border px-5 py-4">
        <span className="text-sm font-semibold text-foreground">
          {title}
        </span>

        <div className="flex items-center gap-2">
          {status && (
            <StatusPill label={status} tone={statusTone} />
          )}

          {secondaryStatus && (
            <StatusPill
              label={secondaryStatus}
              tone={secondaryStatusTone}
            />
          )}
        </div>
      </div>

      {children}
    </div>
  );
}

function PanelRow({
  label,
  value,
  valueTone = "default",
}: {
  label: string;
  value: string;
  valueTone?: "default" | "success" | "destructive";
}) {
  const toneClass = {
    default: "text-foreground",
    success: "text-success",
    destructive: "text-destructive",
  }[valueTone];

  return (
    <div className="flex items-center justify-between border-b border-border px-5 py-3.5 text-sm last:border-b-0">
      <span className="text-muted-foreground">{label}</span>
      <span className={`font-medium ${toneClass}`}>{value}</span>
    </div>
  );
}

/** Features — "Verified before it acts" */
export function IdentityVerificationPanel() {
  return (
    <PanelShell title="Visitor #7c2f" status="Awaiting code" statusTone="muted">
      <PanelRow label="Channel" value="Email" />
      <PanelRow label="Method" value="One-time code" />
      <PanelRow label="Sent to" value="j***@gmail.com" />
      <div className="flex items-center justify-between px-5 py-3.5">
        <span className="text-sm text-muted-foreground">Status</span>
        <StatusPill label="Level 2 pending" tone="orange" />
      </div>
    </PanelShell>
  );
}

/** Features — "Grounded, not guessed" */
export function CitationPanel() {
  return (
    <div className="w-full overflow-hidden rounded-2xl border border-border bg-card shadow-sm shadow-slate-900/5">
      <div className="flex items-center justify-between border-b border-border px-5 py-4">
        <span className="text-sm font-semibold text-foreground">AI reply</span>
        <span className="text-xs text-muted-foreground">Conversation #10482</span>
      </div>
      <p className="border-b border-border px-5 py-5 text-[15px] leading-relaxed text-foreground">
        &quot;Standard returns are accepted within 30 days of delivery,
        unworn and with tags attached.&quot;
      </p>
      <div className="flex items-center justify-between px-5 py-3.5">
        <span className="text-sm text-muted-foreground">Source</span>
        <span className="rounded-full bg-accent-soft px-2.5 py-1 text-[11px] font-medium text-accent">
          returns-policy.md &middot; chunk 3
        </span>
      </div>
    </div>
  );
}

/** Features — "Proposed, never assumed" */
export function RefundApprovalPanel() {
  const [result, setResult] = useState<"approved" | "declined" | null>(null);

  const handleAction = (action: "approved" | "declined") => {
    setResult(action);

    setTimeout(() => {
      setResult(null);
    }, 3000);
  };

  return (
    <PanelShell
      title="Refund order"
      status="Proposed by AI"
      statusTone="accent"
      secondaryStatus={
        result
          ? result === "approved"
            ? "Approved"
            : "Declined"
          : undefined
      }
      secondaryStatusTone={
        result === "approved" ? "success" : "destructive"
      }
    >
      <PanelRow label="Order" value="#NV-88214" />
      <PanelRow label="Amount" value="€42.00" />
      <PanelRow label="Method" value="Original payment" />

      {result ? (
        <div className="flex items-center gap-2 px-5 py-4">
          <StatusPill
            label={result === "approved" ? "Refund approved" : "Refund declined"}
            tone={result === "approved" ? "success" : "destructive"}
          />

          <span
            className={
              result === "approved"
                ? "text-sm font-medium text-success"
                : "text-sm font-medium text-destructive"
            }
          >
            {result === "approved"
              ? "Refund approved successfully"
              : "Refund declined"}
          </span>
        </div>
      ) : (
        <div className="flex items-center gap-2 px-5 py-4">
          <button
            type="button"
            onClick={() => handleAction("approved")}
            className="flex-1 cursor-pointer rounded-full bg-success px-4 py-2.5 text-sm font-medium text-success-foreground transition-opacity hover:opacity-90"
          >
            Approve
          </button>

          <button
            type="button"
            onClick={() => handleAction("declined")}
            className="flex-1 cursor-pointer rounded-full border border-destructive px-4 py-2.5 text-sm font-medium text-destructive transition-colors hover:bg-destructive hover:text-destructive-foreground"
          >
            Decline
          </button>
        </div>
      )}
    </PanelShell>
  );
}

/** HowItWorks — "Keep humans in control" */
export function TicketQueuePanel() {
  const tickets: Array<{ id: string; priority: string; tone: Tone; sla: string }> = [
    { id: "#T-2291", priority: "Urgent", tone: "destructive", sla: "4m left" },
    { id: "#T-2288", priority: "High", tone: "orange", sla: "22m left" },
    { id: "#T-2276", priority: "Medium", tone: "accent", sla: "1h 10m left" },
  ];

  return (
    <PanelShell title="Team queue" status="3 open" statusTone="muted">
      {tickets.map((t) => (
        <div
          key={t.id}
          className="flex items-center justify-between border-b border-border px-5 py-3.5 text-sm last:border-b-0"
        >
          <span className="font-medium text-foreground">{t.id}</span>
          <StatusPill label={t.priority} tone={t.tone} />
          <span className="text-muted-foreground">{t.sla}</span>
        </div>
      ))}
    </PanelShell>
  );
}

/** HowItWorks — "Connect your store" */
export function IntegrationPanel() {
  return (
    <PanelShell title="Integrations" status="1 connected" statusTone="success">
      <div className="flex items-center justify-between border-b border-border px-5 py-3.5">
        <span className="flex items-center gap-2 text-sm font-medium text-foreground">
          <span className="flex h-7 w-7 items-center justify-center rounded-lg bg-success-soft">
            <IconStore className="h-4 w-4 text-success" />
          </span>
          Shopify
        </span>
        <StatusPill label="Synced" tone="success" />
      </div>
      <PanelRow label="Orders" value="4,812" />
      <PanelRow label="Customers" value="2,105" />
    </PanelShell>
  );
}

/** HowItWorks — "Train it on your knowledge" */
export function KnowledgeBasePanel() {
  const docs = [
    { name: "returns-policy.md", chunks: 12 },
    { name: "shipping-faq.md", chunks: 8 },
    { name: "size-guide.pdf", chunks: 21 },
  ];
  return (
    <PanelShell title="Knowledge base" status="41 chunks indexed" statusTone="success">
      {docs.map((d) => (
        <div
          key={d.name}
          className="flex items-center justify-between border-b border-border px-5 py-3.5 text-sm last:border-b-0"
        >
          <span className="font-medium text-foreground">{d.name}</span>
          <span className="text-muted-foreground">{d.chunks} chunks</span>
        </div>
      ))}
    </PanelShell>
  );
}

/** HowItWorks — "Let it talk to customers" */
export function ChatWidgetPanel() {
  return (
    <div className="w-full overflow-hidden rounded-2xl border border-border bg-card shadow-sm shadow-slate-900/5">
      <div className="flex items-center justify-between border-b border-border px-5 py-4">
        <span className="flex items-center gap-2 text-sm font-semibold text-foreground">
          <span className="h-2 w-2 rounded-full bg-success" />
          {brand.name} widget
        </span>
        <span className="text-xs text-muted-foreground">Website</span>
      </div>
      <div className="space-y-3 px-5 py-5 text-sm">
        <p className="w-fit max-w-[80%] rounded-2xl rounded-tl-sm bg-muted px-4 py-2.5 text-foreground">
          Hi — can I return a hoodie I bought two weeks ago?
        </p>
        <p className="ml-auto w-fit max-w-[80%] rounded-2xl rounded-tr-sm bg-accent px-4 py-2.5 text-accent-foreground">
          Yes — items within 30 days are eligible. Want me to start the
          return?
        </p>
      </div>
    </div>
  );
}

/** Security — tenant isolation */
export function TenantIsolationPanel() {
  return (
    <div className="grid grid-cols-2 divide-x divide-border overflow-hidden rounded-2xl border border-border shadow-sm shadow-slate-900/5">
      <div className="bg-card">
        <div className="border-b border-border px-5 py-3.5 text-xs font-medium text-muted-foreground">
          business_a query
        </div>
        <div className="px-5 py-3.5 text-sm text-foreground">3 customers</div>
        <div className="px-5 py-3.5 text-sm text-foreground">12 tickets</div>
      </div>
      <div className="bg-card">
        <div className="border-b border-border px-5 py-3.5 text-xs font-medium text-muted-foreground">
          business_b query
        </div>
        <div className="px-5 py-3.5 text-sm text-foreground">0 rows</div>
        <div className="px-5 py-3.5 text-sm font-medium text-destructive">
          Access denied
        </div>
      </div>
    </div>
  );
}

/** Security — audit log */
export function AuditLogPanel() {
  const entries = [
    { t: "14:02:11", e: "RefundIssued", who: "AI" },
    { t: "14:02:09", e: "Level2VerificationSucceeded", who: "System" },
    { t: "14:01:58", e: "ConversationHandoff", who: "Staff" },
  ];
  return (
    <div className="w-full overflow-hidden rounded-2xl border border-border bg-card shadow-sm shadow-slate-900/5">
      <div className="flex items-center justify-between border-b border-border px-5 py-4">
        <span className="text-sm font-semibold text-foreground">Audit log</span>
        <StatusPill label="Live" tone="success" />
      </div>
      {entries.map((row) => (
        <div
          key={row.t}
          className="flex items-center justify-between border-b border-border px-5 py-3.5 text-sm last:border-b-0"
        >
          <span className="text-muted-foreground">{row.t}</span>
          <span className="font-medium text-foreground">{row.e}</span>
          <span className="text-muted-foreground">{row.who}</span>
        </div>
      ))}
    </div>
  );
}