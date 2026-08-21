import { act } from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { SessionExpiryModal } from "./SessionExpiryModal";

const { mockRouterPush, mockLogout, mockExtendSession, authState } = vi.hoisted(() => ({
  mockRouterPush: vi.fn(),
  mockLogout: vi.fn(),
  mockExtendSession: vi.fn(),
  authState: { user: null as { expiresAtUtc: string } | null },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: mockRouterPush }),
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, logout: mockLogout }),
}));

vi.mock("../../lib/api", () => ({
  extendSession: mockExtendSession,
}));

function setUserExpiringIn(ms: number) {
  authState.user = { expiresAtUtc: new Date(Date.now() + ms).toISOString() };
}

// Timer-driven state updates from vi.advanceTimersByTimeAsync happen outside
// any RTL helper's own act() wrapping, so they must be flushed explicitly —
// otherwise the resulting re-render isn't guaranteed to be in the DOM yet by
// the time the next assertion runs.
async function tick(ms = 0) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

// The countdown renders as "M:SS" (role="timer") rather than plain text —
// parse it back into total seconds for assertions.
function displayedSeconds(): number {
  const [minutes, seconds] = screen.getByRole("timer").textContent!.split(":").map(Number);
  return minutes * 60 + seconds;
}

describe("SessionExpiryModal", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mockRouterPush.mockClear();
    mockLogout.mockClear().mockResolvedValue(undefined);
    mockExtendSession.mockReset();
    authState.user = null;
    window.sessionStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("displays the expiration warning once the session is within the warning window", async () => {
    setUserExpiringIn(5_000); // well inside the 15-minute lead — warning fires ~immediately
    render(<SessionExpiryModal />);
    await tick();

    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(screen.getByText("Your session is about to expire")).toBeInTheDocument();
    expect(
      screen.getByText("Your session will expire soon. Would you like to stay signed in?"),
    ).toBeInTheDocument();
  });

  it("does not show the warning while expiry is still far away", async () => {
    setUserExpiringIn(60 * 60 * 1000); // 1 hour — outside the 15-minute window
    render(<SessionExpiryModal />);
    await tick();

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("calls the extension endpoint when 'Stay signed in' is clicked", async () => {
    setUserExpiringIn(5_000);
    mockExtendSession.mockResolvedValue({
      expiresAtUtc: new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString(),
    });
    render(<SessionExpiryModal />);
    await tick();

    fireEvent.click(screen.getByRole("button", { name: /stay signed in/i }));
    await tick();

    expect(mockExtendSession).toHaveBeenCalledTimes(1);
    // The modal closes once the extension succeeds and a new, later expiry is scheduled.
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("does not log out or navigate when the extension request fails transiently — that's the centralized API client's job, not this component's", async () => {
    setUserExpiringIn(5_000);
    // extendSession() goes through api.ts's authenticated client, which
    // already runs a genuine 401 through the centralized handler. What
    // reaches this component's catch block is only ever a non-401 failure
    // (network blip, 500) — the case that must NOT force a logout.
    mockExtendSession.mockRejectedValue(new Error("network blip"));
    render(<SessionExpiryModal />);
    await tick();

    fireEvent.click(screen.getByRole("button", { name: /stay signed in/i }));
    await tick();

    expect(mockLogout).not.toHaveBeenCalled();
    expect(mockRouterPush).not.toHaveBeenCalled();
    // The modal stays open so the user can retry, or let the still-running
    // countdown reach zero and log out normally.
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("logs out and redirects to /login when 'Log out' is clicked", async () => {
    setUserExpiringIn(5_000);
    render(<SessionExpiryModal />);
    await tick();

    fireEvent.click(screen.getByRole("button", { name: /^log out$/i }));
    await tick();

    expect(mockLogout).toHaveBeenCalledTimes(1);
    expect(mockRouterPush).toHaveBeenCalledWith("/login");
  });

  // Defensive path: logout() (auth-context.tsx) is written to never reject,
  // but goToLogin() must not silently depend on that forever — if the
  // network logout call somehow does fail (a real "Failed to fetch" was
  // observed once during dev, e.g. the backend being briefly unreachable),
  // the user must still land on /login, not get stuck on the modal.
  it("still redirects to /login even if the logout call itself throws", async () => {
    setUserExpiringIn(5_000);
    mockLogout.mockRejectedValue(new TypeError("Failed to fetch"));
    render(<SessionExpiryModal />);
    await tick();

    fireEvent.click(screen.getByRole("button", { name: /^log out$/i }));
    await tick();

    expect(mockRouterPush).toHaveBeenCalledWith("/login");
  });

  it("automatically logs out once the 60-second countdown reaches zero with no response", async () => {
    setUserExpiringIn(5_000);
    render(<SessionExpiryModal />);
    await tick();
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    await tick(60_000);

    expect(mockLogout).toHaveBeenCalledTimes(1);
    expect(mockRouterPush).toHaveBeenCalledWith("/login");
  });

  // Regression test: a page refresh remounts this component from scratch,
  // wiping its React state — the countdown must resume from the real
  // remaining time (persisted in sessionStorage), not restart at a fresh 60s.
  it("resumes the countdown from where it left off after a remount (simulating a page refresh), instead of restarting at 60s", async () => {
    setUserExpiringIn(5_000);
    const { unmount } = render(<SessionExpiryModal />);
    await tick();
    expect(displayedSeconds()).toBe(60);

    await tick(20_000); // 20s of the countdown elapse
    expect(displayedSeconds()).toBe(40);

    // Simulate a refresh: the whole component tree unmounts and remounts —
    // authState.user is unchanged (same session, same expiresAtUtc, exactly
    // as a real /me re-check after reload would report), but all React
    // state is gone. Only sessionStorage survives, same as in a real browser.
    unmount();
    render(<SessionExpiryModal />);
    await tick();

    // Should resume near 40s remaining, not reset to a fresh 60.
    expect(displayedSeconds()).toBeLessThanOrEqual(40);
    expect(displayedSeconds()).toBeGreaterThan(30);
  });
});
