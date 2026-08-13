const items = [
  "Shopify-first commerce integration",
  "EU AI Act Art. 50 disclosure built in",
  "GDPR-aligned retention & anonymization",
  "Every action append-only audit-logged",
];

export function TrustStrip() {
  return (
    <section>
      <div className="grid grid-cols-2 border-t border-l border-border sm:grid-cols-4">
        {items.map((label, i) => (
          <div key={label} className="border-r border-b border-border px-6 py-6 sm:px-8">
            <p className="font-mono text-[11px] text-accent">0{i + 1}</p>
            <p className="mt-2 text-sm leading-snug text-muted-foreground">
              {label}
            </p>
          </div>
        ))}
      </div>
    </section>
  );
}
