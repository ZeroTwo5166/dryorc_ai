"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { useRouter } from "next/navigation";
import { ApiError, logout as apiLogout, me, setUnauthorizedHandler, type MeResponse } from "./api";

type AuthState = {
  user: MeResponse | null;
  status: "loading" | "ready";
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
};

const AuthContext = createContext<AuthState | null>(null);

// A 401/403 from /me means the cookie itself is missing/expired/invalid —
// that's a real "you are signed out." Anything else (a dropped connection
// right after the machine wakes from sleep, the dev DB needing a moment to
// reconnect, a one-off network blip) is not evidence the session is
// invalid, and must not be reported as a logout — see fetchMe below.
function isAuthError(err: unknown): boolean {
  return err instanceof ApiError && (err.status === 401 || err.status === 403);
}

// Retries once on a non-auth failure before giving up, so a real session
// isn't misreported as logged out just because the first request after
// resuming from sleep/reconnecting failed transiently.
async function fetchMe(): Promise<MeResponse | null> {
  try {
    return await me();
  } catch (err) {
    if (isAuthError(err)) return null;
    await new Promise((resolve) => window.setTimeout(resolve, 1200));
    try {
      return await me();
    } catch (err2) {
      if (isAuthError(err2)) return null;
      throw err2;
    }
  }
}

// Mounted once at the root layout so every page — marketing and dashboard
// alike — shares one session check instead of each page re-fetching /me and
// briefly disagreeing with each other about whether the visitor is signed in.
export function AuthProvider({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [user, setUser] = useState<MeResponse | null>(null);
  const [status, setStatus] = useState<"loading" | "ready">("loading");

  // Guards against a burst of parallel authenticated calls all 401-ing at
  // once (e.g. a page firing off several requests right as the session
  // dies) — without this, each one would independently clear the user and
  // push /login again. Reset below whenever `user` becomes non-null again
  // (a fresh sign-in), so a *later*, genuinely new session-expiry event
  // isn't silently swallowed by a guard left over from a previous one.
  const handledUnauthorizedRef = useRef(false);
  useEffect(() => {
    if (user) handledUnauthorizedRef.current = false;
  }, [user]);

  // The one place api.ts's centralized 401 handling actually plugs into:
  // this has both the auth state to clear and a router to redirect with,
  // neither of which api.ts (a plain module, no component tree) has itself.
  // Deliberately just clears local state and navigates — does NOT call the
  // network logout() below, since by the time this fires the session is
  // already known invalid server-side; calling an authenticated endpoint
  // from here would risk exactly the retry loop the extend-session
  // requirement warns about if that call also 401s.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      if (handledUnauthorizedRef.current) return;
      handledUnauthorizedRef.current = true;
      setUser(null);
      router.push("/login");
    });
    return () => setUnauthorizedHandler(null);
  }, [router]);

  useEffect(() => {
    let cancelled = false;
    fetchMe()
      .then((data) => {
        if (!cancelled) setUser(data);
      })
      .catch(() => {
        // Still failing after the retry above and not a clean 401/403 —
        // we genuinely don't know the session state. Leave `user` at its
        // initial null rather than asserting a logout we can't confirm.
      })
      .finally(() => {
        if (!cancelled) setStatus("ready");
      });

    // Safety net, independent of the bfcache/prerender fixes below: whatever
    // the exact cause, "stuck on the loading spinner forever" is a worse
    // failure mode than briefly treating an unresolved session as logged
    // out, so force a resolution if the check hasn't settled in a few
    // seconds no matter what.
    const timeout = window.setTimeout(() => {
      if (!cancelled) setStatus("ready");
    }, 5000);

    return () => {
      cancelled = true;
      window.clearTimeout(timeout);
    };
  }, []);

  // For callers outside the mount effect (e.g. the login form, right after
  // POST /api/auth/login sets the cookie) to tell this already-mounted
  // provider the session changed, instead of it staying stale until a full
  // page reload re-runs the mount effect above. Also sets status, not just
  // user — see the bfcache/prerender effects below, which can call this
  // while status is still frozen at "loading".
  const refresh = useCallback(async () => {
    try {
      setUser(await fetchMe());
    } catch {
      // A transient failure that survived the retry in fetchMe — not a
      // confirmed 401/403, so don't clear an already-signed-in user over
      // what's likely a one-off network blip (e.g. right after the machine
      // wakes from sleep). Leave the existing `user` state as-is.
    } finally {
      setStatus("ready");
    }
  }, []);

  // Chrome's back/forward cache can restore this whole page — including a
  // still-"loading" auth check that never got to finish — exactly as it was
  // frozen, without re-running any effects (Firefox doesn't do this the same
  // way, which is why this only shows up in Chrome). The `pageshow` event
  // with `persisted: true` is the one signal that fires on a bfcache
  // restore, so force a fresh check there instead of trusting frozen state.
  useEffect(() => {
    function handlePageShow(event: PageTransitionEvent) {
      if (event.persisted) refresh();
    }
    window.addEventListener("pageshow", handlePageShow);
    return () => window.removeEventListener("pageshow", handlePageShow);
  }, [refresh]);

  // Chrome-only speculative prerendering (chrome://settings/performance
  // "Preload pages") can render a whole navigation — e.g. typing a URL into
  // a brand-new tab — in a hidden background page before it's activated.
  // The `prerenderingchange` event on `document` fires exactly on
  // activation, so treat it the same as a bfcache restore — Firefox has no
  // such API at all, which is why this is Chrome-specific too.
  useEffect(() => {
    function handlePrerenderingChange() {
      refresh();
    }
    document.addEventListener("prerenderingchange", handlePrerenderingChange);
    return () => document.removeEventListener("prerenderingchange", handlePrerenderingChange);
  }, [refresh]);

  const logout = useCallback(async () => {
    try {
      await apiLogout();
    } catch {
      // Best-effort network call — if the session was already invalid
      // (a 401, quite likely here: e.g. the session-expiry countdown
      // reaching zero after the real 401 expiry already passed, or the
      // user clicking Log out on a session that died in the background),
      // there's nothing left for the server call to do, and api.ts's
      // centralized handler has already reacted to that 401 on its own.
      // logout()'s only real contract is "end up signed out locally" —
      // callers (SessionExpiryModal, Sidebar) rely on it never rejecting,
      // so they can redirect unconditionally right after awaiting it.
    } finally {
      setUser(null);
    }
  }, []);

  return (
    <AuthContext.Provider value={{ user, status, refresh, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used within AuthProvider");
  }
  return ctx;
}
