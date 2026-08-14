import type { Metadata } from "next";
import { CTA } from "../components/landing/CTA";
import { IconCheck } from "../components/landing/icons";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

const plans = [
  {
    name: "Starter",
    price: "Free",
    period: "for 14 days",
    description: "Get a real answer flow live on one storefront.",
    features: [
      "Up to 500 conversations / mo",
      "1 Shopify store",
      "Knowledge base & citations",
      "Email support",
    ],
    cta: "Start free",
    href: "/signup",
    highlighted: false,
  },
  {
    name: "Growth",
    price: "€299",
    period: "/ month",
    description: "For stores that need action, not just answers.",
    features: [
      "Up to 5,000 conversations / mo",
      "Refunds & cancellations, auto-approved under your limit",
      "Ticket queue for your team",
      "Identity verification (OTP)",
      "Priority support",
    ],
    cta: "Book a demo",
    href: "/contact",
    highlighted: true,
  },
  {
    name: "Enterprise",
    price: "Custom",
    period: "",
    description: "Custom volume, SLAs, and a dedicated rollout plan.",
    features: [
      "Unlimited conversations",
      "Multiple stores & brands",
      "Custom SLA targets",
      "Extended audit logging",
      "Dedicated onboarding",
    ],
    cta: "Talk to sales",
    href: "/contact",
    highlighted: false,
  },
];

const faqs = [
  {
    q: "Do you charge per seat or per conversation?",
    a: "Plans are priced on conversation volume, not seats — invite as many Admins and Agents as you need at no extra cost.",
  },
  {
    q: "What happens if I go over my plan's volume?",
    a: "We'll reach out before anything is interrupted. Most teams upgrade a tier rather than pay overage fees.",
  },
  {
    q: "Is my data hosted in the EU?",
    a: "Yes — Nivora's infrastructure and customer data are EU-hosted by default, aligned with GDPR.",
  },
  {
    q: "Can I cancel anytime?",
    a: "Yes, month to month on Starter and Growth. Enterprise follows the term in your agreement.",
  },
];

export const metadata: Metadata = {
  title: "Pricing — Nivora",
  description:
    "Simple, conversation-based pricing. Start free, upgrade to automate refunds and cancellations, or talk to us about Enterprise.",
};

export default function PricingPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="Pricing"
        tone="orange"
        title="Priced for what it resolves, not how many people log in."
        subtitle="Every plan includes the full product — knowledge, verification, and safe actions. Higher tiers just raise the ceiling."
      />

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <div className="grid gap-6 lg:grid-cols-3">
            {plans.map((plan) => (
              <div
                key={plan.name}
                className={`flex flex-col rounded-3xl border p-8 shadow-sm shadow-slate-900/5 ${
                  plan.highlighted
                    ? "border-accent bg-foreground text-white"
                    : "border-border bg-card"
                }`}
              >
                {plan.highlighted && (
                  <span className="mb-4 inline-flex w-fit items-center rounded-full bg-white/10 px-3 py-1 text-[11px] font-medium text-white">
                    Most popular
                  </span>
                )}

                <h3
                  className={`text-lg font-semibold tracking-tight ${plan.highlighted ? "text-white" : "text-foreground"}`}
                >
                  {plan.name}
                </h3>
                <p
                  className={`mt-2 text-sm leading-relaxed ${plan.highlighted ? "text-white/70" : "text-muted-foreground"}`}
                >
                  {plan.description}
                </p>

                <div className="mt-6 flex items-baseline gap-1.5">
                  <span
                    className={`text-4xl font-semibold tracking-tight ${plan.highlighted ? "text-white" : "text-foreground"}`}
                  >
                    {plan.price}
                  </span>
                  {plan.period && (
                    <span
                      className={`text-sm ${plan.highlighted ? "text-white/60" : "text-muted-foreground"}`}
                    >
                      {plan.period}
                    </span>
                  )}
                </div>

                <a
                  href={plan.href}
                  className={`mt-7 inline-flex items-center justify-center rounded-full px-5 py-3 text-sm font-medium shadow-sm transition-opacity hover:opacity-90 ${
                    plan.highlighted
                      ? "bg-white text-foreground"
                      : "bg-primary text-primary-foreground"
                  }`}
                >
                  {plan.cta}
                </a>

                <ul className="mt-8 flex flex-col gap-3">
                  {plan.features.map((f) => (
                    <li key={f} className="flex items-start gap-2.5">
                      <span
                        className={`mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full ${
                          plan.highlighted ? "bg-white/10 text-white" : "bg-accent-soft text-accent"
                        }`}
                      >
                        <IconCheck className="h-2.5 w-2.5" />
                      </span>
                      <span
                        className={`text-[13.5px] leading-relaxed ${plan.highlighted ? "text-white/85" : "text-muted-foreground"}`}
                      >
                        {f}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <p className="text-sm font-medium text-accent">FAQ</p>
          <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
            Common questions.
          </h2>

          <div className="mt-10 grid gap-6 sm:grid-cols-2">
            {faqs.map((f) => (
              <div key={f.q}>
                <h3 className="text-base font-semibold text-foreground">{f.q}</h3>
                <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
                  {f.a}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <CTA />
    </MarketingShell>
  );
}
