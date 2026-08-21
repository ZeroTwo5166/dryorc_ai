"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import brand from "@/brand.json";
import {
  IconLayers,
  IconMessageSquare,
  IconShieldCheck,
  IconStore,
  IconWorkflow,
} from "../components/landing/icons";
import { StatusPill } from "../components/landing/Mockups";
import { DashboardShell } from "../components/layout/DashboardShell";
import { useAuth } from "../lib/auth-context";

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
    description: `Link Shopify so ${brand.name} can see orders, customers, and products.`,
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
  const { user, status } = useAuth();

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  const firstName = user.email.split("@")[0];

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[1200px] px-6 py-12 sm:px-10">
        <div className="flex flex-wrap items-center gap-2.5">
          <p className="text-sm font-medium text-accent">
            {user.roles[0] ?? "Staff"}
          </p>
          <span className="h-1 w-1 rounded-full bg-border" />
          <StatusPill label={user.planTier ?? "trial"} tone="accent" />
        </div>
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
    </DashboardShell>
  );
}
