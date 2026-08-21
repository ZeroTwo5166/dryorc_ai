import brand from "@/brand.json";

export function CTA() {
  return (
    <section className="border-b border-border">
      <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
        <div className="flex flex-col items-start gap-8 rounded-3xl bg-gradient-to-br from-accent to-purple px-8 py-14 sm:px-14 sm:py-16 md:flex-row md:items-center md:justify-between">
          <div className="max-w-xl">
            <p className="text-sm font-medium text-white/80">Get started</p>
            <h2 className="mt-3 text-2xl font-semibold tracking-tight text-white sm:text-3xl">
              See {brand.name} on your own storefront.
            </h2>
            <p className="mt-4 text-base leading-relaxed text-white/80">
              We&apos;re onboarding early Shopify partners in the EU. Book a
              walkthrough and we&apos;ll show you exactly how it handles your
              catalog and policies.
            </p>
          </div>

          <a
            href={`mailto:${brand.email}`}
            className="inline-flex shrink-0 items-center gap-2 rounded-full bg-white px-7 py-3.5 text-sm font-medium text-slate-900 shadow-sm transition-opacity hover:opacity-90"
          >
            Book a demo &rarr;
          </a>
        </div>
      </div>
    </section>
  );
}