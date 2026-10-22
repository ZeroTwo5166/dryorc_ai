import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import TeamPage from "./page";
import { ApiError } from "../lib/api";
import { ThemeProvider } from "../lib/theme-context";

// T-D7 (staff invite D7, D14, D1): the member list is shown to every staff
// user; invitations are requested and shown only when the switch is on and
// the caller is Owner or Admin.

type MockUser = {
  userId: string;
  businessId: string;
  email: string;
  businessName: string;
  planTier: string | null;
  roles: string[];
  expiresAtUtc: string;
  staffInvitesEnabled: boolean;
};

const { mockRouterReplace, authState, apiState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: {
    listTeamMembers: vi.fn(),
    listStaffInvitations: vi.fn(),
    createStaffInvitation: vi.fn(),
    resendStaffInvitation: vi.fn(),
    revokeStaffInvitation: vi.fn(),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/team",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    ...apiState,
    listNotifications: vi.fn().mockResolvedValue({ notifications: [], nextCursor: null }),
    getUnreadNotificationCount: vi.fn().mockResolvedValue({ count: 0 }),
  };
});

const MEMBERS = [
  { userId: "u1", email: "owner@example.com", role: "Owner", joinedAt: "2026-09-01T10:00:00Z" },
  { userId: "u2", email: "agent@example.com", role: "Agent", joinedAt: "2026-10-01T10:00:00Z" },
];

function invitation(id: string, email: string, role: string) {
  return {
    id,
    email,
    role,
    status: "Pending",
    createdAt: "2026-10-20T10:00:00Z",
    expiresAt: "2026-10-27T10:00:00Z",
    lastSentAt: "2026-10-20T10:00:00Z",
  };
}

function user(roles: string[], staffInvitesEnabled: boolean): MockUser {
  return {
    userId: "u1",
    businessId: "b1",
    email: "owner@example.com",
    businessName: "Acme Supply Co.",
    planTier: "trial",
    roles,
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    staffInvitesEnabled,
  };
}

function renderPage() {
  return render(
    <ThemeProvider>
      <TeamPage />
    </ThemeProvider>,
  );
}

describe("TeamPage — T-D7", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    apiState.listTeamMembers.mockReset().mockResolvedValue(MEMBERS);
    apiState.listStaffInvitations.mockReset().mockResolvedValue([]);
    apiState.createStaffInvitation.mockReset();
    apiState.resendStaffInvitation.mockReset();
    apiState.revokeStaffInvitation.mockReset();
  });

  it("switch off: shows the members and never requests invitations", async () => {
    authState.user = user(["Owner"], false);
    renderPage();

    expect(await screen.findByText("agent@example.com")).toBeInTheDocument();
    expect(apiState.listTeamMembers).toHaveBeenCalledTimes(1);
    expect(apiState.listStaffInvitations).not.toHaveBeenCalled();
    expect(screen.queryByRole("heading", { name: "Invitations" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /send invitation/i })).not.toBeInTheDocument();
  });

  it("Agent with the switch on: shows the members, no invitation section and no invitation request", async () => {
    authState.user = user(["Agent"], true);
    renderPage();

    expect(await screen.findByText("owner@example.com")).toBeInTheDocument();
    expect(apiState.listStaffInvitations).not.toHaveBeenCalled();
    expect(screen.queryByRole("heading", { name: "Invitations" })).not.toBeInTheDocument();
  });

  it("Owner with the switch on: lists invitations and offers Admin and Agent", async () => {
    authState.user = user(["Owner"], true);
    apiState.listStaffInvitations.mockResolvedValue([invitation("i1", "new.admin@example.com", "Admin")]);
    renderPage();

    expect(await screen.findByText("new.admin@example.com")).toBeInTheDocument();
    const options = within(screen.getByLabelText("Role")).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Admin", "Agent"]);
    const row = screen.getByText("new.admin@example.com").closest("tr")!;
    expect(within(row).getByRole("button", { name: "Resend" })).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "Revoke" })).toBeInTheDocument();
  });

  it("Admin: offers Agent only and shows Resend/Revoke only on Agent rows", async () => {
    authState.user = user(["Admin"], true);
    apiState.listStaffInvitations.mockResolvedValue([
      invitation("i1", "new.admin@example.com", "Admin"),
      invitation("i2", "new.agent@example.com", "Agent"),
    ]);
    renderPage();

    expect(await screen.findByText("new.agent@example.com")).toBeInTheDocument();
    const options = within(screen.getByLabelText("Role")).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Agent"]);
    const adminRow = screen.getByText("new.admin@example.com").closest("tr")!;
    const agentRow = screen.getByText("new.agent@example.com").closest("tr")!;
    expect(within(adminRow).queryByRole("button")).not.toBeInTheDocument();
    expect(within(agentRow).getByRole("button", { name: "Resend" })).toBeInTheDocument();
    expect(within(agentRow).getByRole("button", { name: "Revoke" })).toBeInTheDocument();
  });

  it("after create, says the email was sent and that Resend recovers a failed delivery (D8)", async () => {
    authState.user = user(["Owner"], true);
    apiState.createStaffInvitation.mockResolvedValue(invitation("i9", "colleague@example.com", "Agent"));
    renderPage();
    await screen.findByText("No pending invitations");

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "colleague@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: /send invitation/i }));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Invitation email sent to colleague@example.com. If it doesn't arrive, use Resend to send it again.",
    );
    expect(apiState.createStaffInvitation).toHaveBeenCalledWith("colleague@example.com", "Agent");
    expect(screen.getByText("colleague@example.com").closest("tr")).not.toBeNull();
  });

  it("shows the server's own 409 text, not the fixed replacement", async () => {
    authState.user = user(["Owner"], true);
    apiState.createStaffInvitation.mockRejectedValue(
      new ApiError(409, "An account with this email already exists.", "This email already belongs to a member of your business."),
    );
    renderPage();
    await screen.findByText("No pending invitations");

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "agent@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: /send invitation/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent("This email already belongs to a member of your business.");
  });

  it("shows the 429 and 403 texts on resend", async () => {
    authState.user = user(["Owner"], true);
    apiState.listStaffInvitations.mockResolvedValue([invitation("i2", "new.agent@example.com", "Agent")]);
    renderPage();
    await screen.findByText("new.agent@example.com");

    apiState.resendStaffInvitation.mockRejectedValueOnce(new ApiError(429, "Request failed (429)."));
    fireEvent.click(screen.getByRole("button", { name: "Resend" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Please wait a minute before resending");

    apiState.resendStaffInvitation.mockRejectedValueOnce(new ApiError(403, "Request failed (403)."));
    fireEvent.click(screen.getByRole("button", { name: "Resend" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("You don't have permission"));
  });

  it("revoke removes the row", async () => {
    authState.user = user(["Owner"], true);
    apiState.listStaffInvitations.mockResolvedValue([invitation("i2", "new.agent@example.com", "Agent")]);
    apiState.revokeStaffInvitation.mockResolvedValue(null);
    renderPage();
    await screen.findByText("new.agent@example.com");

    fireEvent.click(screen.getByRole("button", { name: "Revoke" }));

    await waitFor(() => expect(screen.queryByText("new.agent@example.com")).not.toBeInTheDocument());
    expect(apiState.revokeStaffInvitation).toHaveBeenCalledWith("i2");
  });

  it("redirects to /login when there is no authenticated user", () => {
    authState.user = null;
    renderPage();
    expect(mockRouterReplace).toHaveBeenCalledWith("/login");
  });
});
