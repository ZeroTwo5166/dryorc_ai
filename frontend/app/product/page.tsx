import type { Metadata } from "next";
import brand from "@/brand.json";
import { CTA } from "../components/landing/CTA";
import {
  IconArrowRight,
  IconLayers,
  IconLock,
  IconMail,
  IconMessageSquare,
  IconSend,
  IconShieldCheck,
  IconTarget,
  IconUserCheck,
} from "../components/landing/icons";
import {
  AuditLogPanel,
  CitationPanel,
  IdentityVerificationPanel,
  IntegrationPanel,
  KnowledgeBasePanel,
  RefundApprovalPanel,
  StatusPill,
  TenantIsolationPanel,
} from "../components/landing/Mockups";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

const toneBg: Record<string, string> = {
  accent: "bg-accent-soft text-accent",
  purple: "bg-purple-soft text-purple",
  success: "bg-success-soft text-success",
  orange: "bg-orange-soft text-orange",
  pink: "bg-pink-soft text-pink",
};

const pipeline = [
  {
    step: "01",
    tone: "accent",
    icon: IconTarget,
    title: "Answer — grounded in your own docs",
    description:
      "Your policies and docs are indexed into searchable chunks. Every reply is built from what's actually there.",
    details: [
      "Cites the exact document and chunk behind every claim",
      "Says it doesn't know rather than filling the gap",
      "Re-indexes automatically when a doc changes",
    ],
    panel: <CitationPanel />,
  },
  {
    step: "02",
    tone: "purple",
    icon: IconUserCheck,
    title: "Verify — before anything account-specific",
    description:
      "Order status and general questions need nothing. Anything tied to a person's account needs proof it's them first.",
    details: [
      "One-time code by email, or a signed session token from your store",
      "Verification is scoped per conversation, not stored as a standing login",
      "Unverified visitors never see another customer's data — by construction",
    ],
    panel: <IdentityVerificationPanel />,
  },
  {
    step: "03",
    tone: "success",
    icon: IconShieldCheck,
    title: "Act — proposed, never assumed",
    description:
      `${brand.name} can't call your commerce platform directly. It proposes an action; your rules decide what happens next.`,
    details: [
      "Auto-approved under the refund limit you set",
      "Routed to a human above it, with the full context attached",
      "Every decision — automatic or human — lands in the audit trail",
    ],
    panel: <RefundApprovalPanel />,
  },
];

const channels = [
  {
    icon: IconMessageSquare,
    tone: "accent",
    title: "Website widget",
    status: "Live",
    statusTone: "success" as const,
    description:
      "Embeds on your storefront. Answers, verification, and proposed actions happen in one chat window.",
  },
  {
    icon: IconMail,
    tone: "purple",
    title: "Email",
    status: "Live",
    statusTone: "success" as const,
    description:
      "Inbound email becomes a conversation automatically, handled by the same pipeline as chat.",
  },
  {
    icon: IconSend,
    tone: "orange",
    title: "Instagram DMs",
    status: "Coming soon",
    statusTone: "muted" as const,
    description:
      "Same grounded answers and verification, without leaving Instagram.",
  },
  {
    icon: IconSend,
    tone: "pink",
    title: "WhatsApp",
    status: "Coming soon",
    statusTone: "muted" as const,
    description:
      "For merchants whose customers live in WhatsApp, not their inbox.",
  },
];

const infrastructure = [
  {
    icon: IconLock,
    tone: "purple",
    title: "Tenant isolation, fail-closed",
    description:
      "Every query is scoped to a business by default. If tenant context can't be resolved, it returns nothing — never someone else's data.",
    panel: <TenantIsolationPanel />,
  },
  {
    icon: IconShieldCheck,
    tone: "success",
    title: "Append-only audit trail",
    description:
      "Approvals, refunds, handoffs — every authorization-relevant action is logged and kept, so you can reconstruct exactly what happened.",
    panel: <AuditLogPanel />,
  },
  {
    icon: IconLayers,
    tone: "accent",
    title: "Live commerce data",
    description:
      `Orders, customers, and products sync from Shopify — ${brand.name} answers from current data, not a stale copy.`,
    panel: <IntegrationPanel />,
  },
  {
    icon: IconTarget,
    tone: "orange",
    title: "Indexed knowledge base",
    description:
      "Docs and policies you upload are chunked and indexed automatically, ready to be cited the moment they're in.",
    panel: <KnowledgeBasePanel />,
  },
];

export const metadata: Metadata = {
  title: `Product — ${brand.name}`,
  description:
    `How ${brand.name} answers, verifies, and acts — grounded in your own knowledge, connected to your store, with a human always in the loop.`,
};

export default function ProductPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="Product"
        title="One AI agent. Every channel. Real actions."
        subtitle={`${brand.name} reads your docs, verifies who it's talking to, and can safely act on orders and refunds — not just answer questions about them.`}
      />

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <p className="text-sm font-medium text-accent">The pipeline</p>
          <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
            Every conversation moves through the same three checks.
          </h2>

          <div className="mt-14 flex flex-col">
            {pipeline.map((p, i) => {
              const Icon = p.icon;
              return (
                <div key={p.step}>
                  <div className="grid gap-10 lg:grid-cols-[auto_1fr_1fr] lg:items-center">
                    <div className="flex items-center gap-4 lg:flex-col lg:items-start lg:gap-3">
                      <span
                        className={`flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl text-sm font-semibold ${toneBg[p.tone]}`}
                      >
                        <Icon className="h-5 w-5" />
                      </span>
                      <span className="text-xs font-semibold tracking-[0.2em] text-muted-foreground">
                        STEP {p.step}
                      </span>
                    </div>

                    <div>
                      <h3 className="text-xl font-semibold tracking-tight text-foreground">
                        {p.title}
                      </h3>
                      <p className="mt-3 max-w-md text-sm leading-relaxed text-muted-foreground">
                        {p.description}
                      </p>
                      <ul className="mt-5 flex flex-col gap-2.5">
                        {p.details.map((d) => (
                          <li
                            key={d}
                            className="flex items-start gap-2.5 text-sm text-muted-foreground"
                          >
                            <span
                              className={`mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full ${toneBg[p.tone]}`}
                            />
                            {d}
                          </li>
                        ))}
                      </ul>
                    </div>

                    <div>{p.panel}</div>
                  </div>

                  {i < pipeline.length - 1 && (
                    <div className="my-10 flex items-center gap-3 pl-6 text-muted-foreground/60 lg:pl-[3.25rem]">
                      <IconArrowRight className="h-4 w-4 rotate-90" />
                      <div className="h-px flex-1 bg-border" />
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      </section>

      <section className="border-b border-border bg-muted/40">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <p className="text-sm font-medium text-orange">Where it runs</p>
          <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
            One agent, wherever your customers already are.
          </h2>

          <div className="mt-12 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
            {channels.map((c) => {
              const Icon = c.icon;
              return (
                <div
                  key={c.title}
                  className="flex flex-col rounded-3xl border border-border bg-card p-6 shadow-sm shadow-slate-900/5"
                >
                  <div className="flex items-center justify-between">
                    <span
                      className={`flex h-10 w-10 items-center justify-center rounded-xl ${toneBg[c.tone]}`}
                    >
                      <Icon className="h-5 w-5" />
                    </span>
                    <StatusPill label={c.status} tone={c.statusTone} />
                  </div>
                  <h3 className="mt-5 text-base font-semibold tracking-tight text-foreground">
                    {c.title}
                  </h3>
                  <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
                    {c.description}
                  </p>
                </div>
              );
            })}
          </div>
        </div>
      </section>

      <section
        id="security"
        className="border-b border-border bg-gradient-to-b from-purple-soft/50 to-background"
      >
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <p className="text-sm font-medium text-purple">Under the hood</p>
          <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
            The infrastructure behind every answer and every action.
          </h2>

          <div className="mt-12 grid gap-6 md:grid-cols-2">
            {infrastructure.map((f) => {
              const Icon = f.icon;
              return (
                <div
                  key={f.title}
                  className="flex flex-col gap-5 rounded-3xl border border-border bg-card p-7 shadow-sm shadow-slate-900/5"
                >
                  <span
                    className={`flex h-10 w-10 items-center justify-center rounded-xl ${toneBg[f.tone]}`}
                  >
                    <Icon className="h-5 w-5" />
                  </span>
                  <div>
                    <h3 className="text-base font-semibold text-foreground">
                      {f.title}
                    </h3>
                    <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
                      {f.description}
                    </p>
                  </div>
                  {f.panel}
                </div>
              );
            })}
          </div>

          <div className="mt-12 flex flex-col gap-6 border-t border-border pt-8 sm:flex-row">
            <p className="max-w-sm text-sm leading-relaxed text-muted-foreground">
              <span className="font-semibold text-foreground">
                {brand.name} executes, the model never does.
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

      <CTA />
    </MarketingShell>
  );
}
