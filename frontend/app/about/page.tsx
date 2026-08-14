import type { Metadata } from "next";
import { IconLayers, IconScale, IconShieldCheck } from "../components/landing/icons";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

const values = [
  {
    icon: IconShieldCheck,
    tone: "success",
    title: "Human in the loop, always",
    description:
      "The AI proposes. It never gets unilateral authority over money or a customer's account — your rules decide.",
  },
  {
    icon: IconLayers,
    tone: "accent",
    title: "Grounded, not generative fiction",
    description:
      "Every answer traces back to something you actually wrote. If it's not in your docs, Nivora says so.",
  },
  {
    icon: IconScale,
    tone: "purple",
    title: "Compliant by default, not by request",
    description:
      "EU hosting, GDPR-aligned retention, and AI Act disclosure aren't add-ons — they're the default for every business on Nivora.",
  },
];

export const metadata: Metadata = {
  title: "About — Nivora",
  description:
    "Nivora builds AI customer support for e-commerce that's grounded in your knowledge and safe enough to actually act.",
};

export default function AboutPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="About"
        title="We think support automation should be trustworthy first."
        subtitle="Nivora started from a simple frustration: most AI support tools either can't do anything useful, or do too much without asking. We built the middle path."
      />

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <div className="grid gap-12 lg:grid-cols-[1fr_1.1fr]">
            <div>
              <p className="text-sm font-medium text-accent">Our approach</p>
              <h2 className="mt-3 text-3xl font-semibold tracking-tight text-foreground">
                Built for e-commerce teams in the EU.
              </h2>
              <p className="mt-5 text-sm leading-relaxed text-muted-foreground">
                We work closely with Shopify merchants across Europe, which
                shapes almost every decision — from EU-first hosting, to
                built-in EU AI Act disclosure, to a refund flow that respects
                your actual limits instead of guessing at them.
              </p>
              <p className="mt-4 text-sm leading-relaxed text-muted-foreground">
                Nivora is still early. We&apos;d rather ship something narrow that
                works exactly as promised than something broad that almost
                does.
              </p>
            </div>

            <div className="grid gap-6">
              {values.map((v) => {
                const Icon = v.icon;
                const toneBg: Record<string, string> = {
                  accent: "bg-accent-soft text-accent",
                  purple: "bg-purple-soft text-purple",
                  success: "bg-success-soft text-success",
                };
                return (
                  <div
                    key={v.title}
                    className="flex items-start gap-5 rounded-3xl border border-border bg-card p-6 shadow-sm shadow-slate-900/5"
                  >
                    <span
                      className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-xl ${toneBg[v.tone]}`}
                    >
                      <Icon className="h-5 w-5" />
                    </span>
                    <div>
                      <h3 className="text-base font-semibold text-foreground">
                        {v.title}
                      </h3>
                      <p className="mt-1.5 text-sm leading-relaxed text-muted-foreground">
                        {v.description}
                      </p>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </section>

      <section>
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <div className="rounded-3xl bg-gradient-to-br from-accent to-purple px-8 py-14 text-center sm:px-14">
            <h2 className="mx-auto max-w-xl text-2xl font-semibold tracking-tight text-white sm:text-3xl">
              Want to talk to the team building it?
            </h2>
            <a
              href="/contact"
              className="mt-7 inline-flex items-center gap-2 rounded-full bg-white px-7 py-3.5 text-sm font-medium text-foreground shadow-sm transition-opacity hover:opacity-90"
            >
              Get in touch &rarr;
            </a>
          </div>
        </div>
      </section>
    </MarketingShell>
  );
}
