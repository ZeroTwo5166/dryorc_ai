"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { DashboardShell } from "../components/layout/DashboardShell";
import { FormError } from "../components/auth/FormField";
import { IconTarget } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { ApiError, askAnalyst, type AnalystOutcome } from "../lib/api";

type Exchange = {
  id: string;
  question: string;
  outcome: AnalystOutcome;
  message: string;
};

// Answered/Refused are both "a valid, non-error outcome of a well-formed
// request" per the design doc — rendered identically, neutral. Disabled/
// CapabilityFailed/ClaudeCallFailed are genuinely something going wrong
// (access blocked, or the backend/upstream API broke), so they get a
// destructive tint instead, even though structurally they're still an
// analyst "answer" bubble, not a FormError banner.
function isFailureOutcome(outcome: AnalystOutcome): boolean {
  return outcome === "CapabilityFailed" || outcome === "ClaudeCallFailed" || outcome === "Disabled";
}

// Step 23 Phase 5's frontend: a single-turn Q&A form over POST /api/analyst,
// following /conversations/[id]/page.tsx's message-thread + composer shape
// (role-labeled bubbles, single-line input + submit) rather than Step 21's
// dashboard/chart conventions — this is a conversation-shaped interaction,
// not a data-visualization one. The exchange list below is local-only
// render state, not a persisted session — the backend itself is stateless
// per request (design owner decision 3), this is purely how multiple asks
// in one page visit stack up on screen.
export default function AnalystPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [exchanges, setExchanges] = useState<Exchange[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  useEffect(() => {
    if (status !== "ready") return;
    if (!user) {
      router.replace("/login");
    } else if (!user.aiBusinessAnalystEnabled) {
      // Design §4: "the client hides/removes the entry point entirely."
      // Redirect before ever calling askAnalyst — a disabled business must
      // never see this page's content, and must never trigger a (billed)
      // Claude API call just by navigating here directly. The backend's
      // own 403 stays as defense-in-depth for anyone who bypasses this.
      router.replace("/home");
    }
  }, [status, user, router]);

  async function handleAsk(e: FormEvent) {
    e.preventDefault();
    const question = draft.trim();
    if (!question || sending) return; // blank-content guard + double-submit guard, matching /conversations/[id]

    setSending(true);
    setSendError(null);
    try {
      const result = await askAnalyst(question);
      setExchanges((prev) => [
        ...prev,
        { id: crypto.randomUUID(), question, outcome: result.outcome, message: result.message },
      ]);
      setDraft("");
    } catch (err) {
      setSendError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSending(false);
    }
  }

  // Covers the auth-loading state, the not-yet-redirected instant right
  // after a disabled business's Me() resolves, and (briefly) an
  // unauthenticated visitor — none of these should ever flash the composer.
  if (status === "loading" || !user || !user.aiBusinessAnalystEnabled) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[720px] px-6 py-12 sm:px-10">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Analyst</h1>
        <p className="mt-2 text-sm text-muted-foreground">
          Ask a question about {user.businessName}&apos;s conversations, handoffs, or tickets.
        </p>

        {exchanges.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconTarget className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">Ask your first question</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              For example: &quot;How many conversations were handed off to a human this week?&quot;
            </p>
          </div>
        ) : (
          <div className="mt-8 flex flex-col gap-3">
            {exchanges.map((ex) => (
              <div key={ex.id} className="flex flex-col gap-3">
                <div className="flex justify-end">
                  <div className="max-w-[75%] text-right">
                    <p className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-accent">You</p>
                    <div className="rounded-2xl rounded-tr-sm bg-accent-soft px-4 py-2.5 text-[13.5px] leading-relaxed text-foreground">
                      {ex.question}
                    </div>
                  </div>
                </div>
                <div className="flex justify-start">
                  <div className="max-w-[75%]">
                    <p className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                      Analyst
                    </p>
                    <div
                      className={`rounded-2xl rounded-tl-sm px-4 py-2.5 text-[13.5px] leading-relaxed ${
                        isFailureOutcome(ex.outcome)
                          ? "bg-destructive-soft text-destructive"
                          : "bg-muted text-foreground"
                      }`}
                    >
                      {ex.message}
                    </div>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}

        <form onSubmit={handleAsk} className="mt-6 flex items-center gap-2">
          <input
            type="text"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            disabled={sending}
            placeholder="Ask the Analyst…"
            aria-label="Question for the Analyst"
            className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20 disabled:opacity-60"
          />
          <button
            type="submit"
            disabled={sending || !draft.trim()}
            className="shrink-0 cursor-pointer rounded-xl bg-accent px-4 py-2.5 text-sm font-medium text-accent-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {sending ? "Asking…" : "Ask"}
          </button>
        </form>
        {sendError && <div className="mt-2"><FormError message={sendError} /></div>}
      </main>
    </DashboardShell>
  );
}
