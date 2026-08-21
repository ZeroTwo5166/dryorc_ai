"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { DashboardShell } from "../components/layout/DashboardShell";
import { mockNotifications } from "../components/layout/notifications-data";
import { IconBell } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";

export default function NotificationsPage() {
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

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[720px] px-6 py-12 sm:px-10">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
          Notifications
        </h1>
        <p className="mt-2 text-sm text-muted-foreground">
          Everything that&apos;s happened across {user.businessName}&apos;s support recently.
        </p>

        {mockNotifications.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconBell className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No notifications yet</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              You&apos;ll see activity from conversations, tickets, and your team here.
            </p>
          </div>
        ) : (
          <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
            {mockNotifications.map((n) => (
              <div key={n.id} className="flex items-start gap-4 px-6 py-5">
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                  <IconBell className="h-4 w-4" />
                </span>
                <div>
                  <p className="text-sm font-medium text-foreground">{n.title}</p>
                  <p className="mt-1 text-[12.5px] text-muted-foreground">{n.time}</p>
                </div>
              </div>
            ))}
          </div>
        )}
      </main>
    </DashboardShell>
  );
}
