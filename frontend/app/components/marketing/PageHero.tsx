import type { ReactNode } from "react";

const toneText: Record<string, string> = {
  accent: "text-accent",
  purple: "text-purple",
  success: "text-success",
  orange: "text-orange",
};

const toneGradient: Record<string, string> = {
  accent: "from-accent-soft/60 to-background",
  purple: "from-purple-soft/50 to-background",
  success: "from-success-soft/50 to-background",
  orange: "from-orange-soft/50 to-background",
};

export function PageHero({
  eyebrow,
  title,
  subtitle,
  tone = "accent",
  children,
}: {
  eyebrow: string;
  title: ReactNode;
  subtitle: string;
  tone?: "accent" | "purple" | "success" | "orange";
  children?: ReactNode;
}) {
  return (
    <section
      className={`border-b border-border bg-gradient-to-b ${toneGradient[tone]}`}
    >
      <div className="mx-auto max-w-[1440px] px-6 py-20 sm:px-10 lg:px-14 lg:py-24">
        <div className="max-w-2xl">
          <p className={`text-sm font-medium ${toneText[tone]}`}>{eyebrow}</p>
          <h1 className="mt-3 text-4xl font-semibold leading-[1.1] tracking-tight text-foreground sm:text-5xl">
            {title}
          </h1>
          <p className="mt-6 max-w-xl text-lg leading-relaxed text-muted-foreground">
            {subtitle}
          </p>
          {children}
        </div>
      </div>
    </section>
  );
}
