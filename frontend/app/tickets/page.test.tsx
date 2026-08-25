import { act } from "react";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import TicketsPage from "./page";
import { ThemeProvider } from "../lib/theme-context";
import type { TicketEvent, TicketHubHandlers } from "../lib/ticketHub";

function renderPage() {
  return render(
    <ThemeProvider>
      <TicketsPage />
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

const { mockRouterReplace, authState, apiState, hubState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: { listTickets: vi.fn() },
  hubState: {
    handlers: null as TicketHubHandlers | null,
    stop: vi.fn(),
    connectTicketHub: vi.fn(),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/tickets",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return { ...actual, listTickets: apiState.listTickets };
});

// Never a real WebSocket/negotiate call in tests — captures the handlers
// the page registered so a test can simulate a server-pushed event, and a
// fake connection object so unmount/.stop() is observable.
vi.mock("../lib/ticketHub", () => ({
  connectTicketHub: hubState.connectTicketHub,
}));

const baseTicket = {
  id: "t1",
  customerId: "c1",
  conversationId: null,
  status: "Open" as const,
  priority: "Low" as const,
  priorityReason: "ManualEscalation" as const,
  channel: "Website",
  claimedByUserId: null,
  claimedAt: null,
  resolvedByUserId: null,
  resolvedAt: null,
  firstRespondedAt: null,
  slaDeadline: null,
  summary: "Customer needs help",
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
};

describe("TicketsPage", () => {
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
    apiState.listTickets.mockReset().mockResolvedValue({ tickets: [], nextCursor: null });
    hubState.handlers = null;
    hubState.stop.mockReset();
    hubState.connectTicketHub.mockReset().mockImplementation((handlers: TicketHubHandlers) => {
      hubState.handlers = handlers;
      return { stop: hubState.stop };
    });
  });

  async function emitTicketEvent(event: TicketEvent) {
    await act(async () => {
      hubState.handlers?.onTicketEvent(event);
      await Promise.resolve();
    });
  }

  it("renders the ticket list once loaded", async () => {
    apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
    renderPage();
    expect(await screen.findByText("Customer needs help")).toBeInTheDocument();
    expect(screen.getAllByText("Open").length).toBeGreaterThan(0); // status badge + filter button both render the word
  });

  it("links each row to its ticket detail page", async () => {
    apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
    renderPage();
    const link = await screen.findByRole("link", { name: /customer needs help/i });
    expect(link).toHaveAttribute("href", "/tickets/t1");
  });

  it("shows an empty state when there are no tickets", async () => {
    renderPage();
    expect(await screen.findByText(/no tickets yet/i)).toBeInTheDocument();
  });

  it("shows an error state when the list request fails", async () => {
    apiState.listTickets.mockRejectedValue(new Error("network error"));
    renderPage();
    await waitFor(() => expect(screen.getByText(/couldn't load tickets/i)).toBeInTheDocument());
  });

  it("loads more tickets and appends them using the returned cursor", async () => {
    apiState.listTickets.mockResolvedValueOnce({
      tickets: [baseTicket],
      nextCursor: { createdAt: baseTicket.createdAt, id: "t1" },
    });
    renderPage();
    await screen.findByText("Customer needs help");

    apiState.listTickets.mockResolvedValueOnce({
      tickets: [{ ...baseTicket, id: "t2", summary: "Second ticket" }],
      nextCursor: null,
    });
    fireEvent.click(screen.getByRole("button", { name: /load more/i }));

    await waitFor(() => expect(screen.getByText("Second ticket")).toBeInTheDocument());
    expect(screen.getByText("Customer needs help")).toBeInTheDocument(); // first page still present
    expect(apiState.listTickets).toHaveBeenLastCalledWith(
      expect.objectContaining({ before: { createdAt: baseTicket.createdAt, id: "t1" } }),
    );
  });

  it("re-fetches with the selected status filter", async () => {
    renderPage();
    await screen.findByText(/no tickets yet/i);

    apiState.listTickets.mockResolvedValueOnce({ tickets: [], nextCursor: null });
    fireEvent.click(screen.getByRole("button", { name: /^open$/i }));

    await waitFor(() =>
      expect(apiState.listTickets).toHaveBeenLastCalledWith(expect.objectContaining({ status: "Open" })),
    );
  });

  it("redirects to /login when there is no authenticated user", () => {
    authState.user = null;
    renderPage();
    expect(mockRouterReplace).toHaveBeenCalledWith("/login");
  });

  describe("SignalR live updates", () => {
    it("refetches the list when a TicketEvent is received", async () => {
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
      renderPage();
      await screen.findByText("Customer needs help");
      apiState.listTickets.mockClear();

      await emitTicketEvent({ ticketId: "t1", eventType: "TicketClaimed" });

      await waitFor(() => expect(apiState.listTickets).toHaveBeenCalledTimes(1));
    });

    it("does not reason about event contents — any event, including for an unrelated ticket, triggers the same unconditional refetch", async () => {
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
      renderPage();
      await screen.findByText("Customer needs help");
      apiState.listTickets.mockClear();

      await emitTicketEvent({ ticketId: "some-other-ticket-entirely", eventType: "TicketResolved" });

      await waitFor(() => expect(apiState.listTickets).toHaveBeenCalledTimes(1));
    });

    it("multiple events remain harmless", async () => {
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
      renderPage();
      await screen.findByText("Customer needs help");
      apiState.listTickets.mockClear();

      await emitTicketEvent({ ticketId: "t1", eventType: "TicketClaimed" });
      await emitTicketEvent({ ticketId: "t1", eventType: "TicketResolved" });
      await emitTicketEvent({ ticketId: "t1", eventType: "TicketReopened" });

      await waitFor(() => expect(apiState.listTickets).toHaveBeenCalledTimes(3));
      expect(screen.getByText("Customer needs help")).toBeInTheDocument();
    });

    it("refetches on reconnect", async () => {
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
      renderPage();
      await screen.findByText("Customer needs help");
      apiState.listTickets.mockClear();

      await act(async () => {
        hubState.handlers?.onReconnected?.();
        await Promise.resolve();
      });

      await waitFor(() => expect(apiState.listTickets).toHaveBeenCalledTimes(1));
    });

    it("stops the connection on unmount", async () => {
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });
      const { unmount } = renderPage();
      await screen.findByText("Customer needs help");

      unmount();

      expect(hubState.stop).toHaveBeenCalledTimes(1);
    });

    it("a SignalR connection failure does not break the REST-loaded list", async () => {
      // connectTicketHub itself swallows connection failures internally
      // (its own .start().catch(() => {})) and never rejects/throws to its
      // caller — the realistic failure mode is simply "no handler ever
      // fires," which every other test in this block already exercises via
      // the default mock. This test pins that down explicitly: the page
      // must render normally from REST data alone even when SignalR never
      // delivers a single event.
      apiState.listTickets.mockResolvedValue({ tickets: [baseTicket], nextCursor: null });

      renderPage();

      expect(await screen.findByText("Customer needs help")).toBeInTheDocument();
      expect(hubState.connectTicketHub).toHaveBeenCalledTimes(1);
    });
  });
});
