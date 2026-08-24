"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError } from "../../components/auth/FormField";
import { IconArrowRight, IconMessageSquare } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import { formatRelativeTime } from "../../lib/relative-time";
import { ApiError, createConversationMessage, getConversationMessages, returnConversationToAi, type ConversationMessage } from "../../lib/api";

// Step 9: read-only thread view plus a minimal staff-reply composer.
// Customer delivery is explicitly out of scope here — a sent Agent message
// is persisted and shows up in this dashboard view, but the customer-facing
// widget has no way to receive it yet (no polling/push exists). See
// ConversationsController.CreateMessage's own doc comment.
//
// Step 15: also shows current HandledBy state and, when Human, a "Return to
// AI" action. Deliberately does not poll or otherwise watch for the handler
// state changing on its own — the same one-shot-load pattern the rest of
// this page already uses; the button's own optimistic local-state update
// (see handleReturnToAi) is what keeps this page in sync with a reversal
// this staff member themselves triggers.
export default function ConversationDetailPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const { user, status } = useAuth();

  const [messages, setMessages] = useState<ConversationMessage[]>([]);
  const [handledBy, setHandledBy] = useState<"Ai" | "Human" | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  const [returningToAi, setReturningToAi] = useState(false);
  const [returnToAiError, setReturnToAiError] = useState<string | null>(null);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    getConversationMessages(params.id)
      .then((result) => {
        if (cancelled) return;
        setMessages(result.messages);
        setHandledBy(result.handledBy);
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
  }, [user, params.id]);

  async function handleReturnToAi() {
    if (returningToAi) return; // double-click guard, same convention as handleSend's sending guard
    setReturningToAi(true);
    setReturnToAiError(null);
    try {
      await returnConversationToAi(params.id);
      // Optimistic update from the successful response — same pattern
      // KnowledgeDocumentDetailPage's handleToggleActive already
      // established, not a re-fetch/poll.
      setHandledBy("Ai");
    } catch (err) {
      // Deliberately does NOT flip handledBy — a failed request must never
      // leave the UI claiming a reversal happened when it didn't.
      setReturnToAiError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setReturningToAi(false);
    }
  }

  async function handleSend(e: FormEvent) {
    e.preventDefault();
    const content = draft.trim();
    if (!content || sending) return; // blank-content guard + double-submit guard

    setSending(true);
    setSendError(null);
    try {
      const created = await createConversationMessage(params.id, content);
      setMessages((prev) => [...prev, created]);
      setDraft("");
    } catch (err) {
      setSendError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSending(false);
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
        <Link
          href="/conversations"
          className="inline-flex items-center gap-1.5 text-[13px] font-medium text-muted-foreground hover:text-foreground"
        >
          <IconArrowRight className="h-3.5 w-3.5 rotate-180" />
          Back to inbox
        </Link>

        <h1 className="mt-4 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Conversation</h1>

        {!loading && !error && handledBy && (
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-1 text-[11px] font-semibold uppercase tracking-wider ${
                handledBy === "Human" ? "bg-accent-soft text-accent" : "bg-muted text-muted-foreground"
              }`}
            >
              Handled by {handledBy === "Ai" ? "AI" : "Human"}
            </span>
            {handledBy === "Human" && (
              <button
                type="button"
                onClick={handleReturnToAi}
                disabled={returningToAi}
                className="cursor-pointer rounded-lg border border-border bg-card px-3 py-1.5 text-[12.5px] font-medium text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60"
              >
                {returningToAi ? "Returning to AI…" : "Return to AI"}
              </button>
            )}
          </div>
        )}
        {returnToAiError && <div className="mt-2"><FormError message={returnToAiError} /></div>}

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load this conversation</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server, or it doesn&apos;t belong to your business. Try
              refreshing the page.
            </p>
          </div>
        ) : messages.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconMessageSquare className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No messages in this conversation</p>
          </div>
        ) : (
          <div className="mt-8 flex flex-col gap-3">
            {messages.map((m) => {
              const fromCustomer = m.role === "Customer";
              return (
                <div key={m.id} className={`flex ${fromCustomer ? "justify-start" : "justify-end"}`}>
                  <div className={`max-w-[75%] ${fromCustomer ? "" : "text-right"}`}>
                    <p
                      className={`mb-1 text-[11px] font-semibold uppercase tracking-wider ${
                        fromCustomer ? "text-muted-foreground" : "text-accent"
                      }`}
                    >
                      {m.role === "Ai" ? "AI" : m.role}
                    </p>
                    <div
                      className={`rounded-2xl px-4 py-2.5 text-[13.5px] leading-relaxed ${
                        fromCustomer
                          ? "rounded-tl-sm bg-muted text-foreground"
                          : "rounded-tr-sm bg-accent-soft text-foreground"
                      }`}
                    >
                      {m.content}
                    </div>
                    <p className="mt-1 text-[11.5px] text-muted-foreground/70">
                      {formatRelativeTime(m.createdAt)}
                    </p>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {!loading && !error && (
          <form onSubmit={handleSend} className="mt-6 flex items-center gap-2">
            <input
              type="text"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              disabled={sending}
              placeholder="Reply to this conversation…"
              aria-label="Reply message"
              className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20 disabled:opacity-60"
            />
            <button
              type="submit"
              disabled={sending || !draft.trim()}
              className="shrink-0 cursor-pointer rounded-xl bg-accent px-4 py-2.5 text-sm font-medium text-accent-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {sending ? "Sending…" : "Send"}
            </button>
          </form>
        )}
        {sendError && <div className="mt-2"><FormError message={sendError} /></div>}
      </main>
    </DashboardShell>
  );
}
