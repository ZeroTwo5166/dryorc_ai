import { render, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { AuthProvider, useAuth } from "./auth-context";
import { api, type MeResponse } from "./api";

// Exercises the real wiring end to end (AuthProvider + api.ts together, not
// mocked apart) — the diagram this feature is built around is "React
// component → Nivora API client → 401 → centralized handling," so the
// meaningful test is that api.get()'s 401 actually reaches AuthProvider's
// registered handler, not just that each half individually looks right.
const { mockRouterPush } = vi.hoisted(() => ({
  mockRouterPush: vi.fn(),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: mockRouterPush }),
}));

function unauthedResponse() {
  return new Response(null, { status: 401 });
}

function meResponse(user: MeResponse) {
  return new Response(JSON.stringify(user), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

const sampleUser: MeResponse = {
  userId: "u1",
  businessId: "b1",
  email: "owner@example.com",
  businessName: "Acme Supply Co.",
  planTier: "trial",
  roles: ["Owner"],
  expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  cancelOrderToolEnabled: false,
  aiBusinessAnalystEnabled: false,
};

function Probe({ onReady }: { onReady: (ctx: ReturnType<typeof useAuth>) => void }) {
  const ctx = useAuth();
  onReady(ctx);
  return null;
}

describe("AuthProvider — centralized 401 wiring", () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    mockRouterPush.mockClear();
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function renderSignedIn() {
    fetchMock.mockResolvedValueOnce(meResponse(sampleUser)); // the mount effect's /me check

    let latest: ReturnType<typeof useAuth> | undefined;
    render(
      <AuthProvider>
        <Probe onReady={(ctx) => { latest = ctx; }} />
      </AuthProvider>,
    );

    await waitFor(() => expect(latest?.status).toBe("ready"));
    await waitFor(() => expect(latest?.user).not.toBeNull());
    return () => latest!;
  }

  it("clears the user and redirects to /login when an authenticated API call gets a genuine 401", async () => {
    const getLatest = await renderSignedIn();
    expect(getLatest().user).not.toBeNull();

    fetchMock.mockResolvedValueOnce(unauthedResponse()); // a later dashboard data call
    await expect(api.get("/api/customers")).rejects.toThrow();

    await waitFor(() => expect(mockRouterPush).toHaveBeenCalledWith("/login"));
    await waitFor(() => expect(getLatest().user).toBeNull());
  });

  it("does not push /login more than once for a burst of 401s arriving together", async () => {
    await renderSignedIn();

    fetchMock.mockResolvedValue(unauthedResponse());
    await Promise.allSettled([
      api.get("/api/customers"),
      api.get("/api/conversations"),
      api.get("/api/knowledge"),
    ]);

    await waitFor(() => expect(mockRouterPush).toHaveBeenCalled());
    expect(mockRouterPush).toHaveBeenCalledTimes(1);
  });

  // The debounce guard (handledUnauthorizedRef in auth-context.tsx) must
  // not stay permanently tripped after its first use — otherwise a user who
  // hits a session expiry, logs back in, and later hits a second, genuinely
  // new expiry would silently never be redirected the second time.
  it("redirects again for a later, genuinely new 401 after a fresh login following a previous expiry", async () => {
    const getLatest = await renderSignedIn();

    // First expiry event.
    fetchMock.mockResolvedValueOnce(unauthedResponse());
    await expect(api.get("/api/customers")).rejects.toThrow();
    await waitFor(() => expect(mockRouterPush).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(getLatest().user).toBeNull());

    // Fresh sign-in — resets the debounce guard.
    fetchMock.mockResolvedValueOnce(meResponse(sampleUser));
    await getLatest().refresh();
    await waitFor(() => expect(getLatest().user).not.toBeNull());

    // A later, unrelated 401 must still redirect — not be swallowed by the
    // guard left over from the first event.
    fetchMock.mockResolvedValueOnce(unauthedResponse());
    await expect(api.get("/api/conversations")).rejects.toThrow();
    await waitFor(() => expect(mockRouterPush).toHaveBeenCalledTimes(2));
  });

  // Regression test: logout() itself calls the authenticated POST
  // /api/auth/logout, which — like any authenticated call — 401s if the
  // session was already dead (e.g. a session-expiry countdown reaching zero
  // after the real 8h expiry already passed). logout() must swallow that,
  // not reject, since its callers (SessionExpiryModal, Sidebar) await it and
  // then unconditionally redirect — an unhandled rejection there previously
  // surfaced as a Next.js dev "Runtime ApiError" overlay instead of a clean
  // /login redirect.
  it("logout() resolves (does not throw) even when the network logout call itself 401s on an already-dead session", async () => {
    const getLatest = await renderSignedIn();

    fetchMock.mockResolvedValueOnce(unauthedResponse()); // the /api/auth/logout call itself
    await expect(getLatest().logout()).resolves.toBeUndefined();

    await waitFor(() => expect(getLatest().user).toBeNull());
  });

  it("does not redirect for a public /me check with no session (e.g. an anonymous visitor)", async () => {
    fetchMock.mockResolvedValue(unauthedResponse());

    let latest: ReturnType<typeof useAuth> | undefined;
    render(
      <AuthProvider>
        <Probe onReady={(ctx) => { latest = ctx; }} />
      </AuthProvider>,
    );

    await waitFor(() => expect(latest?.status).toBe("ready"));
    expect(latest?.user).toBeNull();
    expect(mockRouterPush).not.toHaveBeenCalled();
  });
});
