import type { Metadata } from "next";
import brand from "@/brand.json";
import {
  IconLayers,
  IconLock,
  IconScale,
  IconShieldCheck,
} from "../components/landing/icons";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

const resources = [
  {
    icon: IconShieldCheck,
    tone: "success",
    title: "Trust Center",
    description:
      "EU hosting, GDPR alignment, tenant isolation, and how audit logging works under the hood.",
    href: "/product#security",
    cta: "View security",
  },
  {
    icon: IconScale,
    tone: "purple",
    title: "Privacy Policy",
    description:
      "What we collect, why, and how long it's retained — in plain language.",
    href: "/privacy",
    cta: "Read policy",
  },
  {
    icon: IconLock,
    tone: "accent",
    title: "Terms of Service",
    description: `The agreement covering your use of ${brand.name}'s platform and widget.`,
    href: "/terms",
    cta: "Read terms",
  },
  {
    icon: IconLayers,
    tone: "orange",
    title: "Documentation & API reference",
    description:
      "Setup guides, widget integration, and the full API reference for developers.",
    href: null,
    cta: "Coming soon",
  },
];

const toneBg: Record<string, string> = {
  accent: "bg-accent-soft text-accent",
  purple: "bg-purple-soft text-purple",
  success: "bg-success-soft text-success",
  orange: "bg-orange-soft text-orange",
};

export const metadata: Metadata = {
  title: `Resources — ${brand.name}`,
  description:
    `Trust Center, privacy policy, terms of service, and documentation for building on ${brand.name}.`,
};

export default function ResourcesPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="Resources"
        tone="success"
        title={`Everything behind how ${brand.name} works.`}
        subtitle="Policies, security posture, and documentation — for the people who ask before they buy."
      />

      <section className="border-b border-border">
        <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
          <div className="grid gap-6 sm:grid-cols-2">
            {resources.map((r) => {
              const Icon = r.icon;
              const content = (
                <>
                  <span
                    className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl ${toneBg[r.tone]}`}
                  >
                    <Icon className="h-5 w-5" />
                  </span>
                  <div>
                    <h3 className="text-base font-semibold tracking-tight text-foreground">
                      {r.title}
                    </h3>
                    <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
                      {r.description}
                    </p>
                    <span
                      className={`mt-4 inline-flex items-center gap-1.5 text-[13px] font-medium ${
                        r.href ? "text-accent" : "text-muted-foreground"
                      }`}
                    >
                      {r.cta}
                      {r.href && (
                        <span className="transition-transform group-hover:translate-x-0.5">
                          &rarr;
                        </span>
                      )}
                    </span>
                  </div>
                </>
              );

              return r.href ? (
                <a
                  key={r.title}
                  href={r.href}
                  className="group flex items-start gap-5 rounded-3xl border border-border bg-card p-7 shadow-sm shadow-slate-900/5 transition-all hover:-translate-y-0.5 hover:border-accent/40 hover:shadow-md"
                >
                  {content}
                </a>
              ) : (
                <div
                  key={r.title}
                  className="flex items-start gap-5 rounded-3xl border border-border bg-card p-7 opacity-70 shadow-sm shadow-slate-900/5"
                >
                  {content}
                </div>
              );
            })}
          </div>
        </div>
      </section>
    </MarketingShell>
  );
}
