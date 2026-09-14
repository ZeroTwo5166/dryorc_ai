import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import AnalyticsPage from "./page";
import { ThemeProvider } from "../lib/theme-context";
import type { AnalyticsResponse } from "../lib/api";

function renderPage() {
  return render(
    <ThemeProvider>
      <AnalyticsPage />
    </ThemeProvider>,
  );
}

type MockUser = {
  userId: string;
  businessId: string;
  email: string;
  businessName: string;
  planTier: string | null;
  roles: string[];
  expiresAtUtc: string;
};

const { mockRouterReplace, authState, apiState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: { getAnalytics: vi.fn() },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/analytics",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return { ...actual, getAnalytics: apiState.getAnalytics };
});

// Never a real SignalR connection — analytics has none, but DashboardShell
// renders Sidebar/Topbar which don't require one either; nothing to fake here.

function buildResponse(overrides?: Partial<AnalyticsResponse>): AnalyticsResponse {
  return {
    range: "7d",
    rangeStart: "2026-09-08",
    rangeEnd: "2026-09-15",
    conversationsByDay: [
      { day: "2026-09-08", count: 3 },
      { day: "2026-09-09", count: 5 },
      { day: "2026-09-10", count: 0 },
      { day: "2026-09-11", count: 8 },
      { day: "2026-09-12", count: 2 },
      { day: "2026-09-13", count: 1 },
      { day: "2026-09-14", count: 6 },
    ],
    conversationStatusDistribution: [
      { status: "Open", count: 12 },
      { status: "Closed", count: 34 },
    ],
    // Deliberately DIFFERENT totals from the period-scoped metrics above —
    // this is what a real all-history snapshot looks like next to a 7-day
    // window, and the test below asserts these numbers render unchanged
    // when a different range is selected.
    handledBySnapshot: [
      { status: "Ai", count: 900 },
      { status: "Human", count: 100 },
    ],
    handoffsByDay: [
      { day: "2026-09-08", count: 1 },
      { day: "2026-09-09", count: 0 },
      { day: "2026-09-10", count: 0 },
      { day: "2026-09-11", count: 2 },
      { day: "2026-09-12", count: 0 },
      { day: "2026-09-13", count: 0 },
      { day: "2026-09-14", count: 1 },
    ],
    ticketStatusDistribution: [
      { status: "Open", count: 4 },
      { status: "InProgress", count: 2 },
      { status: "Resolved", count: 9 },
      { status: "Reopened", count: 1 },
    ],
    ...overrides,
  };
}

describe("AnalyticsPage", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    authState.user = {
      userId: "u1",
      businessId: "b1",
      email: "staff@example.com",
      businessName: "Acme Supply Co.",
      planTier: "trial",
      roles: ["Owner"],
      expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    };
    apiState.getAnalytics.mockReset().mockResolvedValue(buildResponse());
  });

  // 1. API integration
  it("requests the default 7-day range on load and renders the returned contract", async () => {
    renderPage();
    await waitFor(() => expect(apiState.getAnalytics).toHaveBeenCalledWith("7d"));
    expect(await screen.findByText("Conversation status")).toBeInTheDocument();
    expect(await screen.findByText("Ticket status")).toBeInTheDocument();
  });

  // 2. Range selector
  it("re-fetches with the newly selected range when a period pill is clicked", async () => {
    renderPage();
    await waitFor(() => expect(apiState.getAnalytics).toHaveBeenCalledWith("7d"));

    apiState.getAnalytics.mockClear().mockResolvedValue(buildResponse({ range: "30d" }));
    fireEvent.click(screen.getByRole("button", { name: "30 days" }));

    await waitFor(() => expect(apiState.getAnalytics).toHaveBeenCalledWith("30d"));
  });

  // 3. Metric semantics — trend vs. distribution rendering
  it("renders DayCountEntry metrics as a per-day trend, not a status distribution", async () => {
    renderPage();
    const trendChart = await screen.findByRole("img", { name: /conversations by day, one bar per day/i });
    // A trend is one mark per calendar day — 7 bars for a 7-day window —
    // each carrying its own date, not a handful of category rows.
    expect(trendChart.querySelectorAll("rect")).toHaveLength(7);
    expect(trendChart.querySelector("title")?.textContent).toBe("2026-09-08: 3");
  });

  it("renders StatusCountEntry metrics as a period-scoped composition breakdown, not a trend", async () => {
    renderPage();
    const card = (await screen.findByText("Conversation status")).closest("div");
    // A distribution shows one row per enum value with its own count,
    // never a per-day bar shape.
    const scoped = within(card as HTMLElement);
    expect(scoped.getByText("Open")).toBeInTheDocument();
    expect(scoped.getByText("12")).toBeInTheDocument();
    expect(scoped.getByText("Closed")).toBeInTheDocument();
    expect(scoped.getByText("34")).toBeInTheDocument();
    // Every distribution card is explicitly captioned "This period" —
    // never "All time", which is reserved for the snapshot panel.
    expect(screen.getAllByText("This period")).toHaveLength(2);
  });

  // 4. All-time snapshot is structurally distinct, not just differently labeled
  it("renders HandledBySnapshot in its own structurally distinct panel, separate from the period distributions", async () => {
    renderPage();
    await screen.findByText("Current handling");
    expect(screen.getByText("All time")).toBeInTheDocument();
    expect(screen.getByText(/not affected by the date range/i)).toBeInTheDocument();

    // Distinct DOM shape from DistributionChart's cards: dashed border,
    // no "This period" caption anywhere inside it.
    const panel = screen.getByText("Current handling").closest("div.border-dashed");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).queryByText("This period")).toBeNull();
  });

  // 5. Snapshot independence from the range selector
  it("does not change the all-time snapshot's rendered values when the range selector changes", async () => {
    renderPage();
    await screen.findByText("Current handling");
    expect(screen.getByText("900")).toBeInTheDocument();
    expect(screen.getByText("100")).toBeInTheDocument();

    // The backend ignores range for this metric — simulate a real response
    // to a different range that (correctly) still carries the same
    // all-history snapshot values, and confirm the UI still presents them
    // as "All time", not as freshly scoped to "30 days".
    apiState.getAnalytics.mockClear().mockResolvedValue(buildResponse({ range: "30d" }));
    fireEvent.click(screen.getByRole("button", { name: "30 days" }));

    await waitFor(() => expect(apiState.getAnalytics).toHaveBeenCalledWith("30d"));
    expect(await screen.findByText("900")).toBeInTheDocument();
    expect(screen.getByText("100")).toBeInTheDocument();
    expect(screen.getByText("All time")).toBeInTheDocument();
  });

  // 6. Color independence — identity/value survive without relying on color
  it("exposes each trend bar's day and count as accessible text, not only as a colored bar", async () => {
    renderPage();
    const trendChart = await screen.findByRole("img", { name: /handoffs by day, one bar per day/i });
    const titles = Array.from(trendChart.querySelectorAll("title")).map((t) => t.textContent);
    expect(titles).toEqual([
      "2026-09-08: 1",
      "2026-09-09: 0",
      "2026-09-10: 0",
      "2026-09-11: 2",
      "2026-09-12: 0",
      "2026-09-13: 0",
      "2026-09-14: 1",
    ]);
  });

  it("exposes each distribution slice's category and count as real text nodes, not only via fill color", async () => {
    renderPage();
    const card = (await screen.findByText("Ticket status")).closest("div");
    const scoped = within(card as HTMLElement);
    // Every category label used for TicketStatusDistribution renders as its
    // own text node, independent of whatever `bg-*` class colors its bar —
    // deleting every color class from DistributionChart would not remove
    // any of this information.
    for (const [label, count] of [["Open", "4"], ["In progress", "2"], ["Resolved", "9"], ["Reopened", "1"]]) {
      expect(scoped.getByText(label)).toBeInTheDocument();
      expect(scoped.getByText(count)).toBeInTheDocument();
    }
  });

  // 7. Loading / error / zero-data states
  it("shows a loading spinner before the first response resolves", () => {
    apiState.getAnalytics.mockReset().mockReturnValue(new Promise(() => {})); // never resolves
    renderPage();
    expect(document.querySelector(".animate-spin")).not.toBeNull();
  });

  it("shows an error state when the request fails", async () => {
    apiState.getAnalytics.mockReset().mockRejectedValue(new Error("network error"));
    renderPage();
    await waitFor(() => expect(screen.getByText(/couldn't load analytics/i)).toBeInTheDocument());
  });

  it("renders zero-data responses without error (dense zero-filled arrays, not an empty list)", async () => {
    apiState.getAnalytics.mockReset().mockResolvedValue(
      buildResponse({
        conversationsByDay: [{ day: "2026-09-14", count: 0 }],
        handoffsByDay: [{ day: "2026-09-14", count: 0 }],
        conversationStatusDistribution: [
          { status: "Open", count: 0 },
          { status: "Closed", count: 0 },
        ],
        handledBySnapshot: [
          { status: "Ai", count: 0 },
          { status: "Human", count: 0 },
        ],
        ticketStatusDistribution: [
          { status: "Open", count: 0 },
          { status: "InProgress", count: 0 },
          { status: "Resolved", count: 0 },
          { status: "Reopened", count: 0 },
        ],
      }),
    );
    renderPage();
    await screen.findByText("Current handling");
    expect(screen.getAllByText("0").length).toBeGreaterThan(0);
  });

  // 8. Auth behavior
  it("redirects to /login when there is no authenticated user", () => {
    authState.user = null;
    renderPage();
    expect(mockRouterReplace).toHaveBeenCalledWith("/login");
  });
});
