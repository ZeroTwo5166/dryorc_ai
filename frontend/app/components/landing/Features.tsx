import brand from "@/brand.json";
import {
  CitationPanel,
  IdentityVerificationPanel,
  RefundApprovalPanel,
} from "./Mockups";
import { IconShieldCheck, IconTarget, IconUserCheck } from "./icons";

const toneBg: Record<string, string> = {
  accent: "bg-accent-soft text-accent",
  purple: "bg-purple-soft text-purple",
  success: "bg-success-soft text-success",
};

const features = [
  {
    icon: IconTarget,
    tone: "accent",
    eyebrow: "Grounded, not guessed",
    title: "Every answer cites its source.",
    description:
      `${brand.name} answers from your own docs and policies, and shows exactly which chunk it pulled from — so a disputed answer can always be traced back.`,
    panel: <CitationPanel />,
  },
  {
    icon: IconUserCheck,
    tone: "purple",
    eyebrow: "Verified before it acts",
    title: "Anonymous visitors get general answers only.",
    description:
      "Anything account-specific — an order, a refund — requires a verified identity first: a one-time code, or a signed session token from your store.",
    panel: <IdentityVerificationPanel />,
  },
  {
    icon: IconShieldCheck,
    tone: "success",
    eyebrow: "Proposed, never assumed",
    title: "The AI proposes. Your rules decide.",
    description:
      "Refunds and cancellations are proposed with the full context attached — auto-approved under your limit, or routed to a human above it.",
    panel: <RefundApprovalPanel />,
  },
];

export function Features() {
  return (
    <section id="product" className="border-b border-border bg-muted/40">
      <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
        <p className="text-sm font-medium text-accent">Features</p>
        <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          Support that resolves things, not just replies.
        </h2>

        <div className="mt-12 space-y-6">
          {features.map((f, i) => {
            const Icon = f.icon;
            return (
              <div
                key={f.title}
                className="grid gap-8 rounded-3xl border border-border bg-card p-8 shadow-sm shadow-slate-900/5 md:grid-cols-2 md:items-center md:p-10"
              >
                <div className={i % 2 === 1 ? "md:order-2" : undefined}>
                  <span
                    className={`inline-flex h-10 w-10 items-center justify-center rounded-xl ${toneBg[f.tone]}`}
                  >
                    <Icon className="h-5 w-5" />
                  </span>
                  <p className="mt-4 text-sm font-medium text-muted-foreground">
                    {f.eyebrow}
                  </p>
                  <h3 className="mt-2 max-w-md text-xl font-semibold tracking-tight text-foreground">
                    {f.title}
                  </h3>
                  <p className="mt-3 max-w-md text-sm leading-relaxed text-muted-foreground">
                    {f.description}
                  </p>
                </div>
                <div className={i % 2 === 1 ? "md:order-1" : undefined}>
                  {f.panel}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}