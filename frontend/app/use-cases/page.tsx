import type { Metadata } from "next";
import { CTA } from "../components/landing/CTA";
import {
  IconLayers,
  IconRadar,
  IconScale,
  IconStore,
  IconUserCheck,
  IconWorkflow,
} from "../components/landing/icons";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

const cases = [
  {
    icon: IconStore,
    tone: "accent",
    title: "Order status & tracking",
    description:
      "Pulled live from Shopify — no more \"let me check on that for you\" while a customer waits.",
    example: {
      q: "Where's order #10482?",
      a: "It shipped yesterday via DHL, arriving Thu. Here's the tracking link.",
    },
  },
  {
    icon: IconScale,
    tone: "purple",
    title: "Returns & refunds",
    description:
      "Calculates the real refundable amount from your platform, proposes it, and executes only what your rules allow.",
    example: {
      q: "This shirt arrived damaged, I want a refund.",
      a: "I can refund €42.00 for that item — approving now under your auto-refund limit.",
    },
  },
  {
    icon: IconLayers,
    tone: "success",
    title: "Product & policy questions",
    description:
      "Answers are grounded in your own docs and shown with the exact source, so nothing is invented.",
    example: {
      q: "Do you ship to Norway, and how long does it take?",
      a: "Yes — 3-5 business days. Sourced from your Shipping Policy doc.",
    },
  },
  {
    icon: IconUserCheck,
    tone: "orange",
    title: "Account & identity changes",
    description:
      "Anything tied to a specific customer's account waits for verification first — no exceptions.",
    example: {
      q: "Can you update my shipping address?",
      a: "First I need to verify it's you — I've sent a one-time code to your email.",
    },
  },
  {
    icon: IconRadar,
    tone: "purple",
    title: "Proactive issue detection",
    description:
      "Flags patterns — a delayed carrier, a spike in one product's complaints — before they become a ticket flood.",
    example: {
      q: "(internal)",
      a: "12 customers this week reported the same delay from one carrier — flagged to your team.",
    },
  },
  {
    icon: IconWorkflow,
    tone: "accent",
    title: "Escalation & human handoff",
    description:
      "Anything outside policy, ambiguous, or emotionally charged routes straight to your queue — with full context attached.",
    example: {
      q: "This is the third time I'm writing about this. I want to speak to someone.",
      a: "Routed to your team as high priority, with the full conversation history attached.",
    },
  },
];

const toneBg: Record<string, string> = {
  accent: "bg-accent-soft text-accent",
  purple: "bg-purple-soft text-purple",
  success: "bg-success-soft text-success",
  orange: "bg-orange-soft text-orange",
};

export const metadata: Metadata = {
  title: "Use cases — Nivora",
  description:
    "Order status, returns and refunds, product questions, account changes, proactive detection, and human escalation — see how Nivora handles each.",
};

export default function UseCasesPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="Use cases"
        tone="purple"
        title="Built for the tickets that actually fill your inbox."
        subtitle="From a tracking question to a refund dispute, here's how Nivora handles the volume — and where it hands off to your team."
      />

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
            {cases.map((c) => {
              const Icon = c.icon;
              return (
                <div
                  key={c.title}
                  className="flex flex-col rounded-3xl border border-border bg-card p-7 shadow-sm shadow-slate-900/5"
                >
                  <span
                    className={`inline-flex h-10 w-10 items-center justify-center rounded-xl ${toneBg[c.tone]}`}
                  >
                    <Icon className="h-5 w-5" />
                  </span>
                  <h3 className="mt-5 text-lg font-semibold tracking-tight text-foreground">
                    {c.title}
                  </h3>
                  <p className="mt-2.5 text-sm leading-relaxed text-muted-foreground">
                    {c.description}
                  </p>

                  <div className="mt-6 space-y-2.5 rounded-2xl bg-muted/60 p-4 text-[13px] leading-relaxed">
                    <p className="text-muted-foreground">
                      <span className="font-medium text-foreground">Customer </span>
                      {c.example.q}
                    </p>
                    <p className="text-foreground">
                      <span className="font-medium text-accent">Nivora </span>
                      {c.example.a}
                    </p>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </section>

      <CTA />
    </MarketingShell>
  );
}
