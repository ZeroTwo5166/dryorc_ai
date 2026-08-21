import brand from "@/brand.json";

export type LegalSection = {
  heading: string;
  body: string[];
};

export function LegalDocument({
  eyebrow,
  title,
  updated,
  intro,
  sections,
}: {
  eyebrow: string;
  title: string;
  updated: string;
  intro: string;
  sections: LegalSection[];
}) {
  return (
    <section>
      <div className="border-b border-border bg-gradient-to-b from-accent-soft/50 to-background">
        <div className="mx-auto max-w-[880px] px-6 py-16 sm:px-10 lg:py-20">
          <p className="text-sm font-medium text-accent">{eyebrow}</p>
          <h1 className="mt-3 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
            {title}
          </h1>
          <p className="mt-3 text-sm text-muted-foreground">
            Last updated {updated}
          </p>
          <p className="mt-6 max-w-2xl text-base leading-relaxed text-muted-foreground">
            {intro}
          </p>
        </div>
      </div>

      <div className="mx-auto max-w-[880px] px-6 py-16 sm:px-10">
        <div className="flex flex-col gap-12">
          {sections.map((s) => (
            <div key={s.heading}>
              <h2 className="text-lg font-semibold tracking-tight text-foreground">
                {s.heading}
              </h2>
              <div className="mt-3 flex flex-col gap-3">
                {s.body.map((p, i) => (
                  <p key={i} className="text-sm leading-relaxed text-muted-foreground">
                    {p}
                  </p>
                ))}
              </div>
            </div>
          ))}
        </div>

        <div className="mt-14 rounded-2xl border border-border bg-muted/50 p-6">
          <p className="text-sm leading-relaxed text-muted-foreground">
            Questions about this document? Reach us at{" "}
            <a href={`mailto:${brand.email}`} className="font-medium text-foreground hover:text-accent">
              {brand.email}
            </a>
            .
          </p>
        </div>
      </div>
    </section>
  );
}
