"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconMessageSquare } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { formatRelativeTime } from "../lib/relative-time";
import {
  listConversations,
  type ConversationCursor,
  type ConversationSummary,
} from "../lib/api";

const STATUS_BADGE: Record<string, string> = {
  Open: "bg-accent-soft text-accent",
  Closed: "bg-muted text-muted-foreground",
};

const HANDLED_BY_LABEL: Record<string, string> = {
  Ai: "AI",
  Human: "Human",
};

export default function ConversationsPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<ConversationCursor | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    listConversations({ take: 20 })
      .then((result) => {
        if (cancelled) return;
        setConversations(result.conversations);
        setNextCursor(result.nextCursor);
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
      const result = await listConversations({ take: 20, before: nextCursor });
      setConversations((items) => [...items, ...result.conversations]);
      setNextCursor(result.nextCursor);
    } catch {
      // Leave the already-loaded page intact — the "Load more" button
      // simply stays available to retry.
    } finally {
      setLoadingMore(false);
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
      <main className="mx-auto max-w-[820px] px-6 py-12 sm:px-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Inbox</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            Conversations across {user.businessName}&apos;s support channels.
          </p>
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load conversations</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : conversations.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconMessageSquare className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No conversations yet</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Conversations from your widget and other channels will show up here.
            </p>
          </div>
        ) : (
          <>
            <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
              {conversations.map((c) => (
                <Link
                  key={c.id}
                  href={`/conversations/${c.id}`}
                  className="flex items-start gap-4 px-6 py-5 transition-colors hover:bg-muted/50"
                >
                  <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                    <IconMessageSquare className="h-4 w-4" />
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-medium text-foreground">{c.channel}</span>
                      <span
                        className={`rounded-full px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider ${STATUS_BADGE[c.status] ?? "bg-muted text-muted-foreground"}`}
                      >
                        {c.status}
                      </span>
                      <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
                        {HANDLED_BY_LABEL[c.handledBy] ?? c.handledBy}
                      </span>
                    </div>
                    {c.lastMessagePreview && (
                      <p className="mt-1.5 truncate text-[13px] text-muted-foreground">{c.lastMessagePreview}</p>
                    )}
                    <p className="mt-1 text-[12.5px] text-muted-foreground/70">
                      {formatRelativeTime(c.lastMessageAt ?? c.createdAt)}
                    </p>
                  </div>
                </Link>
              ))}
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
