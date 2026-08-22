"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconBell } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { formatRelativeTime } from "../lib/relative-time";
import {
  listNotifications,
  markAllNotificationsRead,
  markNotificationRead,
  type NotificationCursor,
  type NotificationItem,
} from "../lib/api";

export default function NotificationsPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [notifications, setNotifications] = useState<NotificationItem[]>([]);
  const [nextCursor, setNextCursor] = useState<NotificationCursor | null>(null);
  const [readAllUpTo, setReadAllUpTo] = useState<NotificationCursor | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [markingAllRead, setMarkingAllRead] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    listNotifications({ take: 20 })
      .then((result) => {
        if (cancelled) return;
        setNotifications(result.notifications);
        setNextCursor(result.nextCursor);
        setReadAllUpTo(result.readAllUpTo);
        setError(false);
      })
      .catch(() => {
        if (!cancelled) setError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user]);

  async function handleLoadMore() {
    if (!nextCursor || loadingMore) return;
    setLoadingMore(true);
    try {
      const result = await listNotifications({ take: 20, before: nextCursor });
      setNotifications((items) => [...items, ...result.notifications]);
      setNextCursor(result.nextCursor);
      setReadAllUpTo(result.readAllUpTo);
    } catch {
      // Leave the already-loaded page intact — the "Load more" button
      // simply stays available to retry.
    } finally {
      setLoadingMore(false);
    }
  }

  async function handleMarkAllRead() {
    if (!readAllUpTo || markingAllRead) return;
    const cursor = readAllUpTo;
    setMarkingAllRead(true);
    try {
      await markAllNotificationsRead(cursor);
      setNotifications((items) =>
        items.map((n) =>
          n.createdAt < cursor.createdAt || (n.createdAt === cursor.createdAt && n.id <= cursor.id)
            ? { ...n, isRead: true }
            : n,
        ),
      );
      setReadAllUpTo(null);
    } catch {
      // Leave state as-is — the button remains available to retry.
    } finally {
      setMarkingAllRead(false);
    }
  }

  async function handleNotificationClick(notification: NotificationItem) {
    if (notification.isRead) return;
    try {
      await markNotificationRead(notification.id);
      setNotifications((items) =>
        items.map((n) => (n.id === notification.id ? { ...n, isRead: true } : n)),
      );
    } catch {
      // Best-effort — navigation (if any) still proceeds via the Link itself.
    }
  }

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
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
              Notifications
            </h1>
            <p className="mt-2 text-sm text-muted-foreground">
              Everything that&apos;s happened across {user.businessName}&apos;s support recently.
            </p>
          </div>
          {readAllUpTo && (
            <button
              type="button"
              onClick={handleMarkAllRead}
              disabled={markingAllRead}
              className="shrink-0 text-[13px] font-medium text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-50"
            >
              Mark all as read
            </button>
          )}
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load notifications</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : notifications.length === 0 ? (
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
          <>
            <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
              {notifications.map((n) => {
                const rowClassName = `flex items-start gap-4 px-6 py-5 ${n.isRead ? "" : "bg-accent-soft/30"}`;
                const content = (
                  <>
                    <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                      <IconBell className="h-4 w-4" />
                    </span>
                    <div>
                      <p className="text-sm font-medium text-foreground">{n.title}</p>
                      <p className="mt-1 text-[12.5px] text-muted-foreground">
                        {formatRelativeTime(n.createdAt)}
                      </p>
                    </div>
                  </>
                );
                return n.link ? (
                  <Link
                    key={n.id}
                    href={n.link}
                    onClick={() => void handleNotificationClick(n)}
                    className={rowClassName}
                  >
                    {content}
                  </Link>
                ) : (
                  <div key={n.id} className={rowClassName}>
                    {content}
                  </div>
                );
              })}
            </div>

            {nextCursor && (
              <div className="mt-6 flex justify-center">
                <button
                  type="button"
                  onClick={handleLoadMore}
                  disabled={loadingMore}
                  className="rounded-full border border-border bg-card px-5 py-2 text-[13px] font-medium text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {loadingMore ? "Loading…" : "Load more"}
                </button>
              </div>
            )}
          </>
        )}
      </main>
    </DashboardShell>
  );
}
