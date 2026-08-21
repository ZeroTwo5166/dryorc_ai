import type { Metadata } from "next";
import brand from "@/brand.json";
import { LegalDocument } from "../components/marketing/LegalDocument";
import { MarketingShell } from "../components/marketing/MarketingShell";

export const metadata: Metadata = {
  title: `Terms of Service — ${brand.name}`,
  description: `The agreement covering a business's use of ${brand.name}'s platform and widget.`,
};

export default function TermsPage() {
  return (
    <MarketingShell>
      <LegalDocument
        eyebrow="Legal"
        title="Terms of Service"
        updated="August 2026"
        intro={`These terms govern a business's use of the ${brand.name} platform, dashboard, and widget. By creating an account, you agree to them on behalf of your business.`}
        sections={[
          {
            heading: "1. The service",
            body: [
              `${brand.name} provides an AI-assisted customer support platform: a widget for your storefront, a dashboard for your team, and integrations with your commerce platform. Some capabilities may be limited or unavailable depending on your plan.`,
            ],
          },
          {
            heading: "2. Your responsibilities",
            body: [
              "You're responsible for the accuracy of the knowledge base and policies you provide, for configuring approval limits appropriate to your business, and for keeping staff accounts secure.",
            ],
          },
          {
            heading: "3. AI-proposed actions",
            body: [
              `${brand.name}'s AI can propose actions such as refunds or order cancellations. ${brand.name}'s systems — not the AI model itself — execute those actions, and only within the limits and approval rules you configure. You remain responsible for the limits you set.`,
            ],
          },
          {
            heading: "4. Availability",
            body: [
              "We aim for high availability but don't guarantee uninterrupted service. Planned maintenance and incidents will be communicated where reasonably possible.",
            ],
          },
          {
            heading: "5. Fees",
            body: [
              "Fees are based on the plan you select at signup or as agreed in a separate order form for Enterprise. Trial access does not require payment and may be limited in scope or duration.",
            ],
          },
          {
            heading: "6. Termination",
            body: [
              "You may cancel at any time; access continues through the end of your current billing period. We may suspend accounts that violate these terms or applicable law.",
            ],
          },
          {
            heading: "7. Liability",
            body: [
              `${brand.name} is provided on an "as is" basis. To the extent permitted by law, our liability is limited to the fees paid for the service in the twelve months preceding a claim.`,
            ],
          },
          {
            heading: "8. Changes to these terms",
            body: [
              "We may update these terms from time to time. Material changes will be communicated to account owners before they take effect.",
            ],
          },
        ]}
      />
    </MarketingShell>
  );
}
