import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import TicketsPage from "./page";
import { ThemeProvider } from "../lib/theme-context";

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

const { mockRouterReplace, authState, apiState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: { listTickets: vi.fn() },
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
  });

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
});
