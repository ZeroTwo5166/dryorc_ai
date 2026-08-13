import {
  ChatWidgetPanel,
  IntegrationPanel,
  KnowledgeBasePanel,
  TicketQueuePanel,
} from "./Mockups";

const toneBg: Record<string, string> = {
  accent: "bg-accent text-accent-foreground",
  purple: "bg-purple text-white",
  orange: "bg-orange text-white",
  success: "bg-success text-success-foreground",
};

const steps = [
  {
    step: 1,
    tone: "accent",
    title: "Connect your store",
    description:
      "Shopify orders, customers, and products — one integration, nothing re-entered by hand.",
    panel: <IntegrationPanel />,
  },
  {
    step: 2,
    tone: "purple",
    title: "Train it on your knowledge",
    description:
      "Import your docs and policies. Nivora indexes them so answers stay grounded in what you wrote.",
    panel: <KnowledgeBasePanel />,
  },
  {
    step: 3,
    tone: "orange",
    title: "Let it talk to customers",
    description:
      "The widget answers, verifies identity when it matters, and proposes actions when needed.",
    panel: <ChatWidgetPanel />,
  },
  {
    step: 4,
    tone: "success",
    title: "Keep humans in control",
    description:
      "Risky actions wait for approval by your rules. Agents work from a live, priority-ordered queue.",
    panel: <TicketQueuePanel />,
  },
];

export function HowItWorks() {
  return (
    <section id="how-it-works" className="border-b border-border">
      <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
        <p className="text-sm font-medium text-accent">How it works</p>
        <h2 className="mt-3 max-w-xl text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          From setup to first resolved ticket.
        </h2>

        <ol className="mt-12 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
          {steps.map((s) => (
            <li
              key={s.step}
              className="flex flex-col gap-4 rounded-3xl border border-border bg-card p-6 shadow-sm shadow-slate-900/5"
            >
              <span
                className={`flex h-8 w-8 items-center justify-center rounded-full text-sm font-semibold ${toneBg[s.tone]}`}
              >
                {s.step}
              </span>
              <div>
                <h3 className="text-base font-semibold text-foreground">
                  {s.title}
                </h3>
                <p className="mt-2 text-sm leading-relaxed text-muted-foreground">
                  {s.description}
                </p>
              </div>
              {s.panel}
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}