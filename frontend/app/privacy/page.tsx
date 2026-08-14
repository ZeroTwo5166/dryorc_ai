import type { Metadata } from "next";
import { LegalDocument } from "../components/marketing/LegalDocument";
import { MarketingShell } from "../components/marketing/MarketingShell";

export const metadata: Metadata = {
  title: "Privacy Policy — Nivora",
  description: "How Nivora collects, uses, and retains data, and your rights under GDPR.",
};

export default function PrivacyPage() {
  return (
    <MarketingShell>
      <LegalDocument
        eyebrow="Legal"
        title="Privacy Policy"
        updated="August 2026"
        intro="This policy explains what Nivora collects when a business uses our platform, and when that business's customers interact with the AI widget — and how you can exercise your rights over it."
        sections={[
          {
            heading: "1. Who this applies to",
            body: [
              "This policy covers two groups: the staff of a business using Nivora's dashboard (\"Staff\"), and that business's own customers who interact with the AI widget (\"End Customers\"). Each business remains the data controller for its End Customers; Nivora acts as a processor for that data.",
            ],
          },
          {
            heading: "2. What we collect",
            body: [
              "Staff accounts: name, work email, and authentication data needed to sign in.",
              "End Customer conversations: messages sent to the widget, verified identity signals (such as a one-time code confirmation), and metadata needed to resolve a request — for example, an order number referenced in a message.",
              "We do not collect payment card data. Refunds and order actions are executed through your connected commerce platform, not stored by Nivora as card data.",
            ],
          },
          {
            heading: "3. Why we collect it",
            body: [
              "To operate the support widget and dashboard, ground AI answers in a business's own knowledge base, verify identity before account-specific actions, and maintain an audit trail of any action taken on a business's behalf.",
            ],
          },
          {
            heading: "4. Retention",
            body: [
              "Conversation data is retained according to each business's configured retention window, with a sensible default if none is set. After that window, data is deleted or anonymized rather than kept indefinitely.",
            ],
          },
          {
            heading: "5. Where data is hosted",
            body: [
              "Nivora's infrastructure and customer data are hosted within the EU by default, aligned with GDPR data-residency expectations.",
            ],
          },
          {
            heading: "6. Your rights",
            body: [
              "Depending on your role and jurisdiction, you may have the right to access, correct, export, or delete your data. End Customers should start with the business they contacted; Staff can reach us directly at hello@nivora.app.",
            ],
          },
          {
            heading: "7. AI disclosure",
            body: [
              "In line with the EU AI Act's Article 50, End Customers are told they are interacting with an AI system by default — this is not an opt-in setting a business can silently disable.",
            ],
          },
        ]}
      />
    </MarketingShell>
  );
}
