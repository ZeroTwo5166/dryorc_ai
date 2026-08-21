"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "../../lib/auth-context";
import { extendSession } from "../../lib/api";
import { IconClock } from "../landing/icons";

const WARNING_LEAD_MS = 15 * 60 * 1000;
const COUNTDOWN_SECONDS = 60;

// The 60s countdown's actual deadline, persisted so a page refresh while the
// warning is showing resumes the real remaining time instead of restarting
// a fresh 60s — sessionStorage, not state, specifically because it survives
// exactly a reload of this tab (not longer, not other tabs) and nothing here
// is security-relevant: the real deadline enforcement is the server's cookie
// expiry, this is only ever the local warning UI's own countdown display.
const DEADLINE_STORAGE_KEY = "nivora.sessionExpiry.logoutDeadline";
type StoredDeadline = { expiresAtUtc: string; deadlineMs: number };

function readStoredDeadline(expiresAtUtc: string): number | null {
  try {
    const raw = window.sessionStorage.getItem(DEADLINE_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as StoredDeadline;
    // Only valid for the expiry cycle it was written for — a stored
    // deadline from a previous cycle (e.g. before a successful extension)
    // must never be resumed against a different expiresAtUtc.
    if (parsed.expiresAtUtc !== expiresAtUtc) return null;
    return parsed.deadlineMs;
  } catch {
    return null; // storage unavailable (e.g. private browsing) — just start fresh each time
  }
}

function writeStoredDeadline(value: StoredDeadline | null) {
  try {
    if (value === null) {
      window.sessionStorage.removeItem(DEADLINE_STORAGE_KEY);
    } else {
      window.sessionStorage.setItem(DEADLINE_STORAGE_KEY, JSON.stringify(value));
    }
  } catch {
    // best-effort only — a failed write just means a refresh won't resume the countdown
  }
}

// Session expiry is server-decided (see AuthController — absolute 8h
// cookie, no sliding). This component only schedules a *warning* off the
// server-reported expiresAtUtc; it never grants or assumes validity itself
// — a real request still gets a real 401 from the server if the session is
// actually gone, independent of anything below.
export function SessionExpiryModal() {
  const { user, logout } = useAuth();
  const router = useRouter();

  const [visible, setVisible] = useState(false);
  const [secondsLeft, setSecondsLeft] = useState(COUNTDOWN_SECONDS);
  const [extending, setExtending] = useState(false);

  const warningTimeoutRef = useRef<number | null>(null);
  const countdownIntervalRef = useRef<number | null>(null);

  // logout/router read through refs, synced after every render, precisely
  // so the scheduling effect below never has to depend on them — depending
  // on them directly (via a useCallback chain) meant any identity change on
  // either would re-fire the effect and immediately hide a just-shown
  // warning, since re-scheduling always starts by hiding it. Synced in an
  // effect, not during render — writing a ref during render is itself a
  // React rule violation, even for a same-value assignment like this.
  const logoutRef = useRef(logout);
  const routerRef = useRef(router);
  useEffect(() => {
    logoutRef.current = logout;
    routerRef.current = router;
  });

  function clearTimers() {
    if (warningTimeoutRef.current !== null) {
      window.clearTimeout(warningTimeoutRef.current);
      warningTimeoutRef.current = null;
    }
    if (countdownIntervalRef.current !== null) {
      window.clearInterval(countdownIntervalRef.current);
      countdownIntervalRef.current = null;
    }
  }

  async function goToLogin() {
    clearTimers();
    setVisible(false);
    writeStoredDeadline(null);
    try {
      await logoutRef.current();
    } catch {
      // logout() itself is written to never reject (see auth-context.tsx) —
      // but this path exists specifically to *always* reach /login
      // regardless of what the network call does, not to depend silently on
      // that invariant holding forever. A future regression there must not
      // be able to strand the user on a modal with no way out.
    }
    routerRef.current.push("/login");
  }

  function showWarning(expiresAtUtc: string) {
    // Resume the real remaining countdown across a refresh instead of
    // restarting a full 60s — reuse the stored deadline if it's still both
    // for this exact expiry cycle and still in the future; otherwise this
    // is a genuinely new warning, so start (and persist) a fresh one.
    const stored = readStoredDeadline(expiresAtUtc);
    const deadlineMs = stored !== null && stored > Date.now() ? stored : Date.now() + COUNTDOWN_SECONDS * 1000;
    writeStoredDeadline({ expiresAtUtc, deadlineMs });

    setVisible(true);

    function secondsRemaining() {
      return Math.max(Math.ceil((deadlineMs - Date.now()) / 1000), 0);
    }

    setSecondsLeft(secondsRemaining());

    // The interval is registered *before* any possible immediate logout
    // below, deliberately — goToLogin() calls clearTimers(), and it must
    // always have a real interval to clear, never one that hasn't been
    // assigned to the ref yet.
    countdownIntervalRef.current = window.setInterval(() => {
      const remaining = secondsRemaining();
      setSecondsLeft(remaining);
      if (remaining <= 0) {
        // Reaching zero with no response — automatic logout (requirement 5).
        void goToLogin();
      }
    }, 1000);

    // A resumed deadline (after a refresh) can already be in the past —
    // don't wait for the next 1s tick to notice.
    if (secondsRemaining() <= 0) {
      void goToLogin();
    }
  }

  function scheduleWarning(expiresAtMs: number, expiresAtUtc: string) {
    clearTimers();
    setVisible(false);

    const delay = Math.max(expiresAtMs - WARNING_LEAD_MS - Date.now(), 0);
    warningTimeoutRef.current = window.setTimeout(() => showWarning(expiresAtUtc), delay);
  }

  // (Re)schedule whenever the known session expiry itself changes — initial
  // login, a page load's /me check, or (below) a successful extension.
  // Deliberately depends only on the primitive expiresAtUtc string, not on
  // scheduleWarning/clearTimers — see the logoutRef/routerRef comment above
  // for why those must never be effect dependencies here.
  useEffect(() => {
    if (!user?.expiresAtUtc) {
      clearTimers();
      writeStoredDeadline(null);
      // Syncing local UI state to an external signal (the session going
      // away) — the legitimate effect case, not the cascading-render
      // anti-pattern this lint rule otherwise guards against.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setVisible(false);
      return;
    }
    scheduleWarning(new Date(user.expiresAtUtc).getTime(), user.expiresAtUtc);
    return clearTimers;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user?.expiresAtUtc]);

  async function handleStaySignedIn() {
    setExtending(true);
    try {
      const res = await extendSession();
      scheduleWarning(new Date(res.expiresAtUtc).getTime(), res.expiresAtUtc);
    } catch {
      // extendSession() goes through the authenticated API client (api.ts),
      // which already handles a genuine 401 centrally — session cleared,
      // redirected to /login — so there's nothing left to do for that case.
      // Anything else (network blip, 500) is transient, not evidence the
      // session is invalid: leave the modal open so the user can retry, or
      // let the countdown already running reach zero and log out normally.
    } finally {
      setExtending(false);
    }
  }

  async function handleLogoutClick() {
    await goToLogin();
  }

  if (!visible) return null;

  const urgent = secondsLeft <= 15;
  const minutes = Math.floor(secondsLeft / 60);
  const seconds = secondsLeft % 60;
  const formattedTime = `${minutes}:${seconds.toString().padStart(2, "0")}`;
  const progressPercent = (secondsLeft / COUNTDOWN_SECONDS) * 100;

  return (
    <div
      className="fixed inset-0 z-[100] flex items-center justify-center bg-slate-950/60 px-6 backdrop-blur-md"
      role="dialog"
      aria-modal="true"
      aria-labelledby="session-expiry-title"
      aria-describedby="session-expiry-message"
    >
      <div className="relative w-full max-w-sm overflow-hidden rounded-[1.75rem] border border-border bg-card p-7 shadow-2xl shadow-slate-900/30 sm:p-8">
        {/* Ambient glow, echoes AuthShell/Sidebar's visual language — shifts
            from orange to red as the countdown gets urgent. */}
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden">
          <div
            className={`absolute -top-24 left-1/2 h-56 w-56 -translate-x-1/2 rounded-full blur-[80px] transition-colors duration-700 ${
              urgent ? "bg-destructive/25" : "bg-orange/20"
            }`}
          />
        </div>

        <div className="relative">
          <span
            className={`relative flex h-12 w-12 items-center justify-center rounded-2xl transition-colors duration-700 ${
              urgent ? "bg-destructive-soft text-destructive" : "bg-orange-soft text-orange"
            }`}
          >
            <span
              className={`absolute inset-0 rounded-2xl opacity-40 ${
                urgent ? "animate-ping bg-destructive/40" : "animate-ping bg-orange/40"
              }`}
            />
            <IconClock className="relative h-6 w-6" />
          </span>

          <h2
            id="session-expiry-title"
            className="mt-5 text-xl font-semibold tracking-tight text-foreground"
          >
            Your session is about to expire
          </h2>
          <p id="session-expiry-message" className="mt-2 text-sm leading-relaxed text-muted-foreground">
            Your session will expire soon. Would you like to stay signed in?
          </p>

          <div className="mt-6 rounded-2xl border border-border bg-muted/60 p-4">
            <div className="flex items-baseline justify-between">
              <span className="text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">
                Signing out automatically in
              </span>
              <span
                role="timer"
                aria-live="off"
                className={`font-mono text-2xl font-bold tabular-nums transition-colors duration-500 ${
                  urgent ? "text-destructive" : "text-foreground"
                }`}
              >
                {formattedTime}
              </span>
            </div>
            <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-border">
              <div
                className={`h-full rounded-full transition-all duration-1000 ease-linear ${
                  urgent ? "bg-destructive" : "bg-orange"
                }`}
                style={{ width: `${progressPercent}%` }}
              />
            </div>
          </div>

          <div className="mt-6 flex gap-3">
            <button
              type="button"
              onClick={handleLogoutClick}
              className="flex-1 cursor-pointer rounded-full border border-border px-4 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-muted"
            >
              Log out
            </button>
            <button
              type="button"
              onClick={handleStaySignedIn}
              disabled={extending}
              className="flex-1 cursor-pointer rounded-full bg-primary px-4 py-2.5 text-sm font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {extending ? "Staying signed in…" : "Stay signed in"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
