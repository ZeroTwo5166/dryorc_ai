"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  IconLayers,
  IconMessageSquare,
  IconShieldCheck,
  IconStore,
  IconWorkflow,
} from "../components/landing/icons";
import { StatusPill } from "../components/landing/Mockups";
import { logout, me, type MeResponse } from "../lib/api";

const stats = [
  { label: "Conversations", icon: IconMessageSquare, tone: "accent" },
  { label: "Open tickets", icon: IconWorkflow, tone: "orange" },
  { label: "Knowledge docs", icon: IconLayers, tone: "purple" },
  { label: "Actions auto-resolved", icon: IconShieldCheck, tone: "success" },
] as const;

const toneBg: Record<string, string> = {
  accent: "bg-accent-soft text-accent",
  purple: "bg-purple-soft text-purple",
  success: "bg-success-soft text-success",
  orange: "bg-orange-soft text-orange",
};

const checklist = [
  {
    icon: IconStore,
    title: "Connect your store",
    description: "Link Shopify so Nivora can see orders, customers, and products.",
  },
  {
    icon: IconLayers,
    title: "Import your knowledge base",
    description: "Add docs and policies so answers stay grounded in what you wrote.",
  },
  {
    icon: IconMessageSquare,
    title: "Install the widget",
    description: "Drop one script tag into your storefront to go live.",
  },
  {
    icon: IconWorkflow,
    title: "Invite your team",
    description: "Bring in Admins and Agents to work the queue with you.",
  },
];

export default function HomePage() {
  const router = useRouter();
  const [user, setUser] = useState<MeResponse | null>(null);
  const [status, setStatus] = useState<"loading" | "ready">("loading");
  const [signingOut, setSigningOut] = useState(false);

  useEffect(() => {
    let cancelled = false;
    me()
      .then((data) => {
        if (!cancelled) {
          setUser(data);
          setStatus("ready");
        }
      })
      .catch(() => {
        if (!cancelled) router.replace("/login");
      });
    return () => {
      cancelled = true;
    };
  }, [router]);

  async function handleSignOut() {
    setSigningOut(true);
    try {
      await logout();
    } finally {
      router.push("/login");
    }
  }

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  const firstName = user.email.split("@")[0];

  return (
    <div className="min-h-screen bg-muted/30">
      {/* Top bar */}
      <header className="sticky top-0 z-40 border-b border-border bg-white/80 backdrop-blur-xl">
        <div className="mx-auto flex h-[68px] max-w-[1200px] items-center px-6 sm:px-10">
          <Link href="/" className="flex shrink-0 items-center gap-2.5">
            <span className="flex h-8 w-8 items-center justify-center rounded-[9px] bg-foreground text-[11px] font-bold tracking-tight text-background">
              N
            </span>
            <span className="text-[13px] font-semibold tracking-[0.18em] text-foreground">
              NIVORA
            </span>
          </Link>

          <div className="ml-6 hidden items-center gap-2 border-l border-border pl-6 sm:flex">
            <span className="text-sm font-medium text-foreground">
              {user.businessName}
            </span>
            <StatusPill label={user.planTier ?? "trial"} tone="accent" />
          </div>

          <div className="ml-auto flex items-center gap-3">
            <span className="hidden text-sm text-muted-foreground sm:inline">
              {user.email}
            </span>
            <button
              type="button"
              onClick={handleSignOut}
              disabled={signingOut}
              className="rounded-full border border-border px-4 py-2 text-[13px] font-medium text-foreground transition-colors hover:bg-muted disabled:opacity-60"
            >
              {signingOut ? "Signing out…" : "Sign out"}
            </button>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-[1200px] px-6 py-12 sm:px-10">
        <p className="text-sm font-medium text-accent">
          {user.roles[0] ?? "Staff"}
        </p>
        <h1 className="mt-2 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          Welcome back, {firstName}.
        </h1>
        <p className="mt-3 max-w-lg text-sm leading-relaxed text-muted-foreground">
          Here&apos;s the state of {user.businessName}&apos;s support. Finish
          setup below to start seeing real activity here.
        </p>

        {/* Stats */}
        <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {stats.map((s) => {
            const Icon = s.icon;
            return (
              <div
                key={s.label}
                className="rounded-2xl border border-border bg-card p-5 shadow-sm shadow-slate-900/5"
              >
                <span
                  className={`inline-flex h-9 w-9 items-center justify-center rounded-xl ${toneBg[s.tone]}`}
                >
                  <Icon className="h-5 w-5" />
                </span>
                <p className="mt-4 text-2xl font-semibold tracking-tight text-foreground">
                  —
                </p>
                <p className="mt-1 text-sm text-muted-foreground">{s.label}</p>
              </div>
            );
          })}
        </div>

        {/* Setup checklist */}
        <div className="mt-12 rounded-3xl border border-border bg-card p-8 shadow-sm shadow-slate-900/5">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-lg font-semibold tracking-tight text-foreground">
                Finish setting up
              </h2>
              <p className="mt-1 text-sm text-muted-foreground">
                0 of {checklist.length} complete
              </p>
            </div>
            <div className="h-1.5 w-32 overflow-hidden rounded-full bg-muted">
              <div className="h-full w-0 rounded-full bg-accent" />
            </div>
          </div>

          <div className="mt-8 grid gap-4 sm:grid-cols-2">
            {checklist.map((item) => {
              const Icon = item.icon;
              return (
                <div
                  key={item.title}
                  className="flex items-start gap-4 rounded-2xl border border-border p-5 transition-colors hover:border-accent/40 hover:bg-accent-soft/30"
                >
                  <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-muted text-foreground">
                    <Icon className="h-5 w-5" />
                  </span>
                  <div>
                    <p className="text-sm font-semibold text-foreground">
                      {item.title}
                    </p>
                    <p className="mt-1 text-[13px] leading-relaxed text-muted-foreground">
                      {item.description}
                    </p>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </main>
    </div>
  );
}
