import Link from "next/link";
import type { ComponentType, ReactNode } from "react";
import brand from "@/brand.json";
import { useAuth } from "../../lib/auth-context";
import { useTheme } from "../../lib/theme-context";
import { IconArrowRight } from "../landing/icons";
import { StatusPill } from "../landing/Mockups";
import { ThemeToggle } from "../layout/ThemeToggle";

type IconProps = { className?: string };

export function AuthShell({
  eyebrow,
  title,
  subtitle,
  icon: Icon,
  switchLabel,
  switchHref,
  children,
}: {
  eyebrow: string;
  title: string;
  subtitle: string;
  icon: ComponentType<IconProps>;
  switchLabel: string;
  switchHref: string;
  children: ReactNode;
}) {
  const { theme } = useTheme();
  const { user } = useAuth();
  const nivora = theme === "nivora";
  const dark = theme === "dark";

  return (
    <div className="relative flex min-h-screen flex-col overflow-hidden bg-background">
      {/* Ambient background — neutral, not brand-colored, so it stays
          correct in light/nivora instead of reading as a permanent blue/
          purple tint. The page itself stays light even under nivora; only
          the card below (.auth-card) picks up the brand navy. Skipped
          entirely under "dark": the light-tinted blur blobs and the grid
          (drawn in --color-border, a light-gray in every other theme) both
          read as a mistake — pale smudges and a faint grid — on a genuinely
          dark surface, rather than the subtle texture they are elsewhere. */}
      {!dark && (
        <div aria-hidden="true" className="pointer-events-none absolute inset-0">
          <div className="absolute -top-48 left-1/2 h-[32rem] w-[56rem] -translate-x-1/2 rounded-full bg-gradient-to-br from-foreground/10 via-foreground/5 to-transparent blur-[120px]" />
          <div className="absolute -bottom-56 -left-40 h-96 w-96 rounded-full bg-foreground/5 blur-[110px]" />
          <div className="absolute -bottom-56 -right-40 h-96 w-96 rounded-full bg-foreground/5 blur-[110px]" />
          <div
            className="absolute inset-0 opacity-[0.4]"
            style={{
              backgroundImage:
                "linear-gradient(to right, var(--color-border) 1px, transparent 1px), linear-gradient(to bottom, var(--color-border) 1px, transparent 1px)",
              backgroundSize: "56px 56px",
              maskImage:
                "radial-gradient(ellipse 60% 50% at 50% 0%, black 40%, transparent 100%)",
            }}
          />
        </div>
      )}

      {/* Header — same fixed navy-rail treatment as Sidebar.tsx/Navbar.tsx
          under the "nivora" theme, so the brand look is consistent whether
          a visitor lands on marketing, dashboard, or an auth page. */}
      <header
        className={`relative z-10 flex items-center justify-between px-6 py-4 sm:px-10 ${
          nivora ? "border-b border-white/10 bg-[#0e1226]" : ""
        }`}
      >
        {/* Routes to the dashboard, never the marketing landing page, once
            signed in — same rule Navbar.tsx follows for its logo. */}
        <Link href={user ? "/home" : "/"} className="flex items-center gap-2.5">
          <span
            className={`flex h-8 w-8 items-center justify-center rounded-[9px] text-[11px] font-bold tracking-tight ${
              nivora ? "bg-white text-[#0e1226]" : "bg-foreground text-background"
            }`}
          >
            N
          </span>
          <span className={`text-[13px] font-semibold tracking-[0.18em] ${nivora ? "text-white" : "text-foreground"}`}>
            {brand.name.toUpperCase()}
          </span>
        </Link>

        <div className="flex items-center gap-3">
          <ThemeToggle />
          {user ? (
            // Signed-in visitor landed here anyway (e.g. re-opened a
            // verify-email link after already logging in) — "Sign in" /
            // "Create account" would be nonsensical, so this mirrors
            // Navbar.tsx's own logged-in state instead of AuthShell's usual
            // switchLabel/switchHref pair.
            <Link
              href="/home"
              className={`group inline-flex items-center gap-2 rounded-full px-4 py-2 text-[13px] font-medium shadow-sm transition-colors ${
                nivora
                  ? "bg-accent text-accent-foreground hover:opacity-90"
                  : "border border-border bg-card text-foreground hover:border-accent/40 hover:text-accent"
              }`}
            >
              <span className="h-1.5 w-1.5 rounded-full bg-[#39FF88]" />
              Dashboard
            </Link>
          ) : (
            <Link
              href={switchHref}
              className={`group inline-flex items-center gap-1.5 rounded-full border px-4 py-2 text-[13px] font-medium shadow-sm transition-colors ${
                nivora
                  ? switchLabel === "Create account"
                    ? // Under nivora, this is the one switch-link that's actually
                      // a signup CTA (not just "go back") — filled accent blue,
                      // same treatment as the primary auth-form button, instead
                      // of the neutral outline every other switch link gets.
                      "border-transparent bg-accent text-accent-foreground hover:opacity-90"
                    : "border-white/15 bg-white/5 text-white hover:border-white/30 hover:bg-white/10"
                  : "border-border bg-card text-foreground hover:border-accent/40 hover:text-accent"
              }`}
            >
              {switchLabel}
              <IconArrowRight className="h-3.5 w-3.5 transition-transform group-hover:translate-x-0.5" />
            </Link>
          )}
        </div>
      </header>

      {/* Card — "auth-card" is what globals.css hooks into (see the
          :root[data-theme="nivora"] .auth-card block) to re-theme every
          token this box and its children (form fields, error/success
          banners, the checkbox) already use — bg-card, border-border,
          text-foreground, text-muted-foreground, the *-soft badge washes —
          into the brand's navy-blue palette, without touching the page
          background around it. */}
      <main className="relative z-10 flex flex-1 flex-col items-center justify-center px-6 py-10 sm:px-10">
        <div className="w-full max-w-[26rem]">
          <div className="auth-card rounded-[2rem] border border-border bg-card p-8 shadow-xl shadow-slate-900/[0.06] sm:p-10">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <Icon className="h-5 w-5" />
            </span>

            <p className="mt-6 text-sm font-medium text-accent">{eyebrow}</p>
            <h1 className="mt-1.5 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
              {title}
            </h1>
            <p className="mt-2.5 text-sm leading-relaxed text-muted-foreground">
              {subtitle}
            </p>

            <div className="mt-8">{children}</div>
          </div>

          <div className="mt-6 flex flex-wrap items-center justify-center gap-2.5">
            <StatusPill label="EU-hosted" tone="success" />
            <StatusPill label="GDPR-aligned" tone="accent" />
            <StatusPill label="AI Act disclosed" tone="purple" />
          </div>
        </div>
      </main>

      <p className="relative z-10 pb-8 text-center text-xs text-muted-foreground">
        &copy; {new Date().getFullYear()} {brand.name}. All rights reserved.
      </p>
    </div>
  );
}
