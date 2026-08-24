"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import {
  getUnreadNotificationCount,
  listNotifications,
  markNotificationRead,
  type NotificationItem,
} from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import { useTheme } from "../../lib/theme-context";
import { formatRelativeTime } from "../../lib/relative-time";
import { IconBell, IconSearch } from "../landing/icons";

// Sits above the dashboard content, to the right of Sidebar — not a
// full-width bar like the marketing Navbar, since Sidebar already owns the
// left rail. Under "nivora" it now takes the same fixed navy rail color as
// Sidebar/Navbar (#0e1226), so it reads as one continuous piece of brand
// chrome sitting directly above the navy sidebar, instead of the ordinary
// bg-card/border-border tokens it used before (which left it looking like
// plain neutral content butted up against the sidebar's navy).
export function Topbar() {
  const { user } = useAuth();
  const { theme } = useTheme();
  const nivora = theme === "nivora";

  const [query, setQuery] = useState("");
  const [notifOpen, setNotifOpen] = useState(false);
  const notifRef = useRef<HTMLDivElement>(null);

  const [latestNotifications, setLatestNotifications] = useState<NotificationItem[]>([]);
  const [unreadCount, setUnreadCount] = useState(0);
  const [notifLoading, setNotifLoading] = useState(true);
  const [notifError, setNotifError] = useState(false);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    Promise.all([listNotifications({ take: 5 }), getUnreadNotificationCount()])
      .then(([list, unread]) => {
        if (cancelled) return;
        setLatestNotifications(list.notifications);
        setUnreadCount(unread.count);
        setNotifError(false);
      })
      .catch(() => {
        if (!cancelled) setNotifError(true);
      })
      .finally(() => {
        if (!cancelled) setNotifLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user]);

  // Keeps the unread badge from going stale between page loads/reloads —
  // the dropdown/list content itself only refetches on open/mount, not on
  // this interval (deliberately not polling the full notification list).
  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    const intervalId = setInterval(() => {
      getUnreadNotificationCount()
        .then((unread) => {
          if (!cancelled) setUnreadCount(unread.count);
        })
        .catch(() => {
          // Best-effort — a transient failure just leaves the badge as-is
          // until the next tick, same tolerance the widget's own polling
          // loop already has for a failed poll.
        });
    }, 30_000);

    return () => {
      cancelled = true;
      clearInterval(intervalId);
    };
  }, [user]);

  async function handleNotificationClick(notification: NotificationItem) {
    if (notification.isRead) return;
    try {
      await markNotificationRead(notification.id);
      setLatestNotifications((items) =>
        items.map((n) => (n.id === notification.id ? { ...n, isRead: true } : n)),
      );
      setUnreadCount((count) => Math.max(0, count - 1));
    } catch {
      // Best-effort — navigation (if any) still proceeds via the Link itself.
    }
  }

  useEffect(() => {
    if (!notifOpen) return;

    function handlePointerDown(e: MouseEvent) {
      if (notifRef.current && !notifRef.current.contains(e.target as Node)) {
        setNotifOpen(false);
      }
    }
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") setNotifOpen(false);
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [notifOpen]);

  // Not sure yet what "Growth plan" should actually do (upsell CTA? plan
  // switcher?) — so instead of guessing, this just shows the business's
  // current subscription tier as a plain, non-interactive readout, same
  // source (user.planTier) the home page dashboard already reads from.
  const planLabel = (user?.planTier ?? "trial").replace(/^\w/, (c) => c.toUpperCase());

  return (
    <header
      className={`sticky top-0 z-20 flex h-16 shrink-0 items-center justify-between gap-4 border-b px-6 backdrop-blur-xl sm:px-8 ${
        nivora ? "border-white/10 bg-[#0e1226]" : "border-border bg-card/80"
      }`}
    >
      <div className="w-full max-w-md">
        <label className="relative block">
          <IconSearch
            className={`pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 ${
              nivora ? "text-white/40" : "text-muted-foreground"
            }`}
          />
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search conversations, docs, customers"
            className={`w-full rounded-lg border py-2 pl-10 pr-14 text-sm outline-none transition-colors focus:ring-2 ${
              nivora
                ? "border-white/10 bg-white/5 text-white placeholder:text-white/35 focus:border-accent focus:bg-white/10 focus:ring-accent/30"
                : "border-border bg-muted/60 text-foreground placeholder:text-muted-foreground focus:border-accent focus:bg-background focus:ring-accent/20"
            }`}
          />
          <kbd
            className={`pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 rounded-md border px-1.5 py-0.5 text-[11px] font-medium ${
              nivora ? "border-white/15 bg-white/5 text-white/40" : "border-border bg-card text-muted-foreground"
            }`}
          >
            ⌘K
          </kbd>
        </label>
      </div>

      <div className="flex shrink-0 items-center gap-3">
        <span
          className={`inline-flex items-center gap-1.5 rounded-full border px-3.5 py-2 text-[13px] font-medium ${
            nivora ? "border-white/10 bg-white/5 text-white/80" : "border-border bg-card text-foreground"
          }`}
        >
          <span className="h-1.5 w-1.5 rounded-full bg-accent" />
          {planLabel} plan
        </span>

        <div ref={notifRef} className="relative">
          <button
            type="button"
            onClick={() => setNotifOpen((v) => !v)}
            aria-label="Notifications"
            aria-haspopup="true"
            aria-expanded={notifOpen}
            className={`relative flex h-9 w-9 cursor-pointer items-center justify-center rounded-full border shadow-sm transition-colors ${
              nivora
                ? "border-white/10 bg-white/5 text-white/70 hover:bg-white/10 hover:text-white"
                : "border-border bg-card text-muted-foreground hover:bg-muted hover:text-foreground"
            }`}
          >
            <IconBell className="h-[18px] w-[18px]" />
            {unreadCount > 0 && (
              <span className="absolute right-2 top-2 h-1.5 w-1.5 rounded-full bg-accent" />
            )}
          </button>

          {notifOpen && (
            <div
              className={`absolute right-0 top-[calc(100%+10px)] z-30 w-80 overflow-hidden rounded-2xl border shadow-xl ${
                nivora ? "border-white/10 bg-[#121736]" : "border-border bg-card shadow-slate-900/10"
              }`}
            >
              <p
                className={`border-b px-4 py-3 text-sm font-semibold ${
                  nivora ? "border-white/10 text-white" : "border-border text-foreground"
                }`}
              >
                Notifications
              </p>

              <ul className="max-h-80 overflow-y-auto">
                {notifLoading ? (
                  <li className={`px-4 py-3 text-[13px] ${nivora ? "text-white/40" : "text-muted-foreground"}`}>
                    Loading…
                  </li>
                ) : notifError ? (
                  <li className={`px-4 py-3 text-[13px] ${nivora ? "text-white/40" : "text-muted-foreground"}`}>
                    Couldn&apos;t load notifications.
                  </li>
                ) : latestNotifications.length === 0 ? (
                  <li className={`px-4 py-3 text-[13px] ${nivora ? "text-white/40" : "text-muted-foreground"}`}>
                    No notifications yet.
                  </li>
                ) : (
                  latestNotifications.map((n) => {
                    const rowClassName = `block border-b px-4 py-3 text-left last:border-b-0 ${
                      nivora ? "border-white/5" : "border-border/60"
                    } ${n.isRead ? "" : nivora ? "bg-white/5" : "bg-accent-soft/40"}`;
                    const content = (
                      <>
                        <p className={`text-[13px] font-medium ${nivora ? "text-white/90" : "text-foreground"}`}>
                          {n.title}
                        </p>
                        <p className={`mt-0.5 text-[11.5px] ${nivora ? "text-white/40" : "text-muted-foreground"}`}>
                          {formatRelativeTime(n.createdAt)}
                        </p>
                      </>
                    );
                    return (
                      <li key={n.id}>
                        {n.link ? (
                          <Link
                            href={n.link}
                            onClick={() => {
                              setNotifOpen(false);
                              void handleNotificationClick(n);
                            }}
                            className={rowClassName}
                          >
                            {content}
                          </Link>
                        ) : (
                          <div className={rowClassName}>{content}</div>
                        )}
                      </li>
                    );
                  })
                )}
              </ul>

              <Link
                href="/notifications"
                onClick={() => setNotifOpen(false)}
                className={`block border-t px-4 py-3 text-center text-[13px] font-medium transition-colors ${
                  nivora
                    ? "border-white/10 text-accent hover:bg-white/5"
                    : "border-border text-accent hover:bg-muted/60"
                }`}
              >
                See all notifications
              </Link>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}