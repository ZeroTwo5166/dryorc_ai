import { act } from "react";
import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { Topbar } from "./Topbar";

const { mockListNotifications, mockGetUnreadCount, authState } = vi.hoisted(() => ({
  mockListNotifications: vi.fn(),
  mockGetUnreadCount: vi.fn(),
  authState: { user: null as { planTier: string } | null },
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user }),
}));

vi.mock("../../lib/theme-context", () => ({
  useTheme: () => ({ theme: "light" }),
}));

vi.mock("../../lib/api", () => ({
  listNotifications: mockListNotifications,
  getUnreadNotificationCount: mockGetUnreadCount,
  markNotificationRead: vi.fn(),
}));

// Timer-driven state updates from vi.advanceTimersByTimeAsync happen outside
// any RTL helper's own act() wrapping — same reasoning/helper as
// SessionExpiryModal.test.tsx.
async function tick(ms = 0) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe("Topbar unread-count polling", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    authState.user = { planTier: "trial" };
    mockListNotifications.mockReset().mockResolvedValue({ notifications: [], nextCursor: null, readAllUpTo: null });
    mockGetUnreadCount.mockReset().mockResolvedValue({ count: 0 });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("fetches the unread count once on mount", async () => {
    mockGetUnreadCount.mockResolvedValue({ count: 3 });
    render(<Topbar />);
    await tick();

    expect(mockGetUnreadCount).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: /notifications/i })).toBeInTheDocument();
  });

  it("refreshes the unread count every 30 seconds", async () => {
    render(<Topbar />);
    await tick();
    expect(mockGetUnreadCount).toHaveBeenCalledTimes(1); // initial load

    await tick(30_000);
    expect(mockGetUnreadCount).toHaveBeenCalledTimes(2);

    await tick(30_000);
    expect(mockGetUnreadCount).toHaveBeenCalledTimes(3);
  });

  it("does not poll the full notification list on the interval — only unread-count", async () => {
    render(<Topbar />);
    await tick();
    expect(mockListNotifications).toHaveBeenCalledTimes(1); // initial load only

    await tick(30_000);
    await tick(30_000);
    expect(mockListNotifications).toHaveBeenCalledTimes(1);
  });

  it("updates the visible unread badge after a poll picks up a new count", async () => {
    mockGetUnreadCount.mockResolvedValueOnce({ count: 0 });
    render(<Topbar />);
    await tick();
    expect(screen.getByRole("button", { name: /notifications/i }).querySelector("span.bg-accent")).toBeNull();

    mockGetUnreadCount.mockResolvedValueOnce({ count: 1 });
    await tick(30_000);

    // The unread dot only renders once unreadCount > 0 — its presence after
    // the poll is the observable proof the badge state actually updated.
    const bell = screen.getByRole("button", { name: /notifications/i });
    expect(bell.querySelector("span.bg-accent")).not.toBeNull();
  });

  it("a failed poll leaves the existing unread count in place rather than throwing", async () => {
    mockGetUnreadCount.mockResolvedValueOnce({ count: 2 });
    render(<Topbar />);
    await tick();

    mockGetUnreadCount.mockRejectedValueOnce(new Error("network blip"));
    await expect(tick(30_000)).resolves.toBeUndefined();

    const bell = screen.getByRole("button", { name: /notifications/i });
    expect(bell.querySelector("span.bg-accent")).not.toBeNull(); // still showing the last known unread state
  });

  it("stops polling and clears the interval on unmount", async () => {
    const { unmount } = render(<Topbar />);
    await tick();
    expect(mockGetUnreadCount).toHaveBeenCalledTimes(1);

    unmount();
    await tick(60_000);

    // No further calls after unmount — the interval was actually cleared,
    // not just abandoned (which would still fire and warn about updating an
    // unmounted component).
    expect(mockGetUnreadCount).toHaveBeenCalledTimes(1);
  });

  it("does not poll at all when there is no authenticated user", async () => {
    authState.user = null;
    render(<Topbar />);
    await tick(60_000);

    expect(mockGetUnreadCount).not.toHaveBeenCalled();
  });
});
