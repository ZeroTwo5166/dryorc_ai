import Link from "next/link";
import type { ReactNode } from "react";
import { IconCheck } from "../landing/icons";
import { StatusPill } from "../landing/Mockups";

const bullets = [
  "Grounded answers, cited from your own docs",
  "Identity verified before anything account-specific",
  "Every action proposed, audited, human-approved",
];

export function AuthShell({
  eyebrow,
  title,
  subtitle,
  children,
}: {
  eyebrow: string;
  title: string;
  subtitle: string;
  children: ReactNode;
}) {
  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      {/* Brand panel */}
      <div className="relative hidden overflow-hidden bg-foreground lg:flex lg:flex-col lg:justify-between lg:p-14">
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 bg-gradient-to-br from-accent/25 via-transparent to-purple/25"
        />
        <div
          aria-hidden="true"
          className="pointer-events-none absolute -left-24 -top-24 h-72 w-72 rounded-full bg-accent/30 blur-[100px]"
        />
        <div
          aria-hidden="true"
          className="pointer-events-none absolute -bottom-32 -right-16 h-80 w-80 rounded-full bg-purple/30 blur-[110px]"
        />

        <Link href="/" className="relative z-10 flex w-fit items-center gap-2.5">
          <span className="flex h-8 w-8 items-center justify-center rounded-[9px] bg-white text-[11px] font-bold tracking-tight text-foreground">
            N
          </span>
          <span className="text-[13px] font-semibold tracking-[0.18em] text-white">
            NIVORA
          </span>
        </Link>

        <div className="relative z-10 max-w-md">
          <h2 className="text-3xl font-semibold leading-[1.15] tracking-tight text-white">
            Customer support that actually acts.
          </h2>
          <p className="mt-4 text-sm leading-relaxed text-white/60">
            Nivora answers from your own knowledge base, verifies who it&apos;s
            talking to, and takes real action — with a human always in the
            loop.
          </p>

          <ul className="mt-8 flex flex-col gap-3.5">
            {bullets.map((b) => (
              <li key={b} className="flex items-start gap-3">
                <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-white/10 text-white">
                  <IconCheck className="h-3 w-3" />
                </span>
                <span className="text-sm leading-relaxed text-white/80">
                  {b}
                </span>
              </li>
            ))}
          </ul>

          <div className="mt-10 flex flex-wrap gap-2.5">
            <StatusPill label="EU-hosted" tone="success" />
            <StatusPill label="GDPR-aligned" tone="accent" />
            <StatusPill label="AI Act disclosed" tone="purple" />
          </div>
        </div>

        <p className="relative z-10 text-xs text-white/40">
          &copy; {new Date().getFullYear()} Nivora. All rights reserved.
        </p>
      </div>

      {/* Form panel */}
      <div className="flex flex-col justify-center px-6 py-14 sm:px-10 lg:px-20">
        <div className="mx-auto w-full max-w-sm">
          <Link
            href="/"
            className="mb-10 inline-flex items-center gap-2.5 lg:hidden"
          >
            <span className="flex h-8 w-8 items-center justify-center rounded-[9px] bg-foreground text-[11px] font-bold tracking-tight text-background">
              N
            </span>
            <span className="text-[13px] font-semibold tracking-[0.18em] text-foreground">
              NIVORA
            </span>
          </Link>

          <p className="text-sm font-medium text-accent">{eyebrow}</p>
          <h1 className="mt-2 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
            {title}
          </h1>
          <p className="mt-2.5 text-sm leading-relaxed text-muted-foreground">
            {subtitle}
          </p>

          <div className="mt-9">{children}</div>
        </div>
      </div>
    </div>
  );
}
