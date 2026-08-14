import type { Metadata } from "next";
import { CTA } from "../components/landing/CTA";
import { Features } from "../components/landing/Features";
import { HowItWorks } from "../components/landing/HowItWorks";
import { Security } from "../components/landing/Security";
import { MarketingShell } from "../components/marketing/MarketingShell";
import { PageHero } from "../components/marketing/PageHero";

export const metadata: Metadata = {
  title: "Product — Nivora",
  description:
    "How Nivora answers, verifies, and acts — grounded in your own knowledge, connected to your store, with a human always in the loop.",
};

export default function ProductPage() {
  return (
    <MarketingShell>
      <PageHero
        eyebrow="Product"
        title="One AI agent. Every channel. Real actions."
        subtitle="Nivora reads your docs, verifies who it's talking to, and can safely act on orders and refunds — not just answer questions about them."
      />
      <Features />
      <HowItWorks />
      <Security />
      <CTA />
    </MarketingShell>
  );
}
