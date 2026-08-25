import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import TicketDetailPage from "./page";
import { ThemeProvider } from "../../lib/theme-context";

function renderPage() {
  return render(
    <ThemeProvider>
      <TicketDetailPage />
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
  apiState: {
    getTicket: vi.fn(),
    getCustomer: vi.fn(),
    claimTicket: vi.fn(),
    unclaimTicket: vi.fn(),
    resolveTicket: vi.fn(),
    reopenTicket: vi.fn(),
    updateTicketPriority: vi.fn(),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  useParams: () => ({ id: "ticket-1" }),
  usePathname: () => "/tickets/ticket-1",
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../../lib/api")>("../../lib/api");
  return {
    ...actual,
    getTicket: apiState.getTicket,
    getCustomer: apiState.getCustomer,
    claimTicket: apiState.claimTicket,
    unclaimTicket: apiState.unclaimTicket,
    resolveTicket: apiState.resolveTicket,
    reopenTicket: apiState.reopenTicket,
    updateTicketPriority: apiState.updateTicketPriority,
  };
});

const baseTicket = {
  id: "ticket-1",
  customerId: "cust-1",
  conversationId: "conv-1",
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
  summary: "Needs a callback",
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
};

const baseCustomer = {
  id: "cust-1",
  businessId: "b1",
  email: "jane@example.com",
  name: "Jane Doe",
  isAnonymized: false,
  extendedLoggingEnabled: false,
  createdAt: new Date().toISOString(),
  recentConversations: [],
};

function setOwner() {
  authState.user = {
    userId: "u1",
    businessId: "b1",
    email: "staff@example.com",
    businessName: "Acme Supply Co.",
    planTier: "trial",
    roles: ["Owner"],
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  };
}

describe("TicketDetailPage", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    setOwner();
    apiState.getTicket.mockReset().mockResolvedValue(baseTicket);
    apiState.getCustomer.mockReset().mockResolvedValue(baseCustomer);
    apiState.claimTicket.mockReset().mockResolvedValue(null);
    apiState.unclaimTicket.mockReset().mockResolvedValue(null);
    apiState.resolveTicket.mockReset().mockResolvedValue(null);
    apiState.reopenTicket.mockReset().mockResolvedValue(null);
    apiState.updateTicketPriority.mockReset().mockResolvedValue(null);
  });

  it("renders ticket information once loaded", async () => {
    renderPage();
    expect(await screen.findByRole("heading", { name: "Needs a callback" })).toBeInTheDocument();
    expect(screen.getByText("Open")).toBeInTheDocument();
    expect(screen.getByText("ManualEscalation")).toBeInTheDocument();
  });

  it("renders customer information", async () => {
    renderPage();
    expect(await screen.findByText("Jane Doe")).toBeInTheDocument();
  });

  it("renders a link to the source conversation when present", async () => {
    renderPage();
    const link = await screen.findByRole("link", { name: /view source conversation/i });
    expect(link).toHaveAttribute("href", "/conversations/conv-1");
  });

  it("shows 'No SLA configured' when slaDeadline is null", async () => {
    renderPage();
    expect(await screen.findByText(/no sla configured/i)).toBeInTheDocument();
  });

  it("shows 'Not yet responded' when firstRespondedAt is null", async () => {
    renderPage();
    expect(await screen.findByText(/not yet responded/i)).toBeInTheDocument();
  });

  it("shows the Claim control for an Open ticket", async () => {
    renderPage();
    expect(await screen.findByRole("button", { name: /^claim$/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^resolve$/i })).not.toBeInTheDocument();
  });

  it("shows Unclaim and Resolve controls for an InProgress ticket", async () => {
    apiState.getTicket.mockResolvedValue({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" });
    renderPage();
    expect(await screen.findByRole("button", { name: /^unclaim$/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^resolve$/i })).toBeInTheDocument();
  });

  it("shows the Reopen control for a Resolved ticket", async () => {
    apiState.getTicket.mockResolvedValue({ ...baseTicket, status: "Resolved", resolvedByUserId: "u1" });
    renderPage();
    expect(await screen.findByRole("button", { name: /^reopen$/i })).toBeInTheDocument();
  });

  it("claims a ticket and refreshes to the new state", async () => {
    apiState.getTicket
      .mockResolvedValueOnce(baseTicket)
      .mockResolvedValueOnce({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" });

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /^claim$/i }));

    await waitFor(() => expect(apiState.claimTicket).toHaveBeenCalledWith("ticket-1"));
    await screen.findByRole("button", { name: /^resolve$/i }); // reflects the refreshed InProgress state
  });

  it("does not falsely transition to the claimed state when the claim request fails", async () => {
    apiState.claimTicket.mockRejectedValue(new Error("already claimed"));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^claim$/i }));

    await waitFor(() => expect(screen.getByText(/something went wrong/i)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /^claim$/i })).toBeInTheDocument(); // still Open, not InProgress
    expect(screen.queryByRole("button", { name: /^resolve$/i })).not.toBeInTheDocument();
  });

  it("unclaims a ticket and refreshes to the new state", async () => {
    apiState.getTicket
      .mockResolvedValueOnce({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" })
      .mockResolvedValueOnce(baseTicket);

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /^unclaim$/i }));

    await waitFor(() => expect(apiState.unclaimTicket).toHaveBeenCalledWith("ticket-1"));
    await screen.findByRole("button", { name: /^claim$/i });
  });

  it("does not falsely transition to Open when the unclaim request fails", async () => {
    apiState.getTicket.mockResolvedValue({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" });
    apiState.unclaimTicket.mockRejectedValue(new Error("not claimed"));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^unclaim$/i }));

    await waitFor(() => expect(screen.getByText(/something went wrong/i)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /^unclaim$/i })).toBeInTheDocument(); // still InProgress
  });

  it("resolves a ticket and refreshes to the new state", async () => {
    apiState.getTicket
      .mockResolvedValueOnce({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" })
      .mockResolvedValueOnce({ ...baseTicket, status: "Resolved", resolvedByUserId: "u1" });

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /^resolve$/i }));

    await waitFor(() => expect(apiState.resolveTicket).toHaveBeenCalledWith("ticket-1"));
    await screen.findByRole("button", { name: /^reopen$/i });
  });

  it("does not falsely transition to Resolved when the resolve request fails", async () => {
    apiState.getTicket.mockResolvedValue({ ...baseTicket, status: "InProgress", claimedByUserId: "u1" });
    apiState.resolveTicket.mockRejectedValue(new Error("not in progress"));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^resolve$/i }));

    await waitFor(() => expect(screen.getByText(/something went wrong/i)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /^resolve$/i })).toBeInTheDocument(); // still InProgress
    expect(screen.queryByRole("button", { name: /^reopen$/i })).not.toBeInTheDocument();
  });

  it("reopens a ticket and refreshes to the new state", async () => {
    apiState.getTicket
      .mockResolvedValueOnce({ ...baseTicket, status: "Resolved", resolvedByUserId: "u1" })
      .mockResolvedValueOnce({ ...baseTicket, status: "Reopened" });

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /^reopen$/i }));

    await waitFor(() => expect(apiState.reopenTicket).toHaveBeenCalledWith("ticket-1"));
    await screen.findByText("Reopened");
  });

  it("does not falsely transition to Reopened when the reopen request fails", async () => {
    apiState.getTicket.mockResolvedValue({ ...baseTicket, status: "Resolved", resolvedByUserId: "u1" });
    apiState.reopenTicket.mockRejectedValue(new Error("not resolved"));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^reopen$/i }));

    await waitFor(() => expect(screen.getByText(/something went wrong/i)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /^reopen$/i })).toBeInTheDocument(); // still Resolved
  });

  it("changes priority and reflects the new value", async () => {
    apiState.getTicket
      .mockResolvedValueOnce(baseTicket)
      .mockResolvedValueOnce({ ...baseTicket, priority: "Urgent" });

    renderPage();
    const select = (await screen.findByDisplayValue("Low")) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "Urgent" } });

    await waitFor(() => expect(apiState.updateTicketPriority).toHaveBeenCalledWith("ticket-1", "Urgent"));
  });

  it("shows an error and does not falsely persist the new priority when the request fails", async () => {
    apiState.updateTicketPriority.mockRejectedValue(new Error("invalid priority"));
    renderPage();

    const select = (await screen.findByDisplayValue("Low")) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "Urgent" } });

    await waitFor(() => expect(screen.getByText(/something went wrong/i)).toBeInTheDocument());
    // getTicket was never called again after the failed mutation, so the
    // underlying ticket record was never confirmed to have changed.
    expect(apiState.getTicket).toHaveBeenCalledTimes(1);
  });

  it("shows an error state when the ticket fails to load", async () => {
    apiState.getTicket.mockRejectedValue(new Error("network error"));
    renderPage();
    await waitFor(() => expect(screen.getByText(/couldn't load this ticket/i)).toBeInTheDocument());
  });

  it("still renders the ticket when the customer fetch fails", async () => {
    apiState.getCustomer.mockRejectedValue(new Error("network error"));
    renderPage();
    expect(await screen.findByRole("heading", { name: "Needs a callback" })).toBeInTheDocument();
  });
});
