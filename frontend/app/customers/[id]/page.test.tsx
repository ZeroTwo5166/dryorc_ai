import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import CustomerDetailPage from "./page";
import { ThemeProvider } from "../../lib/theme-context";

function renderPage() {
  return render(
    <ThemeProvider>
      <CustomerDetailPage />
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
    getCustomer: vi.fn(),
    listCustomerIdentities: vi.fn(),
    linkCustomerIdentity: vi.fn(),
    revokeCustomerIdentity: vi.fn(),
    setCustomerExtendedLogging: vi.fn(),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  useParams: () => ({ id: "cust-1" }),
  usePathname: () => "/customers/cust-1",
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../../lib/api")>("../../lib/api");
  return {
    ...actual,
    getCustomer: apiState.getCustomer,
    listCustomerIdentities: apiState.listCustomerIdentities,
    linkCustomerIdentity: apiState.linkCustomerIdentity,
    revokeCustomerIdentity: apiState.revokeCustomerIdentity,
    setCustomerExtendedLogging: apiState.setCustomerExtendedLogging,
  };
});

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

const activeIdentity = {
  id: "id-1",
  channel: "Website",
  verificationMethod: "Otp",
  externalUserId: "verified-user-123",
  linkedAt: new Date().toISOString(),
  expiresAt: null,
  revokedAt: null,
  revokedByUserId: null,
  isCurrentlyValid: true,
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

function setAgent() {
  authState.user = { ...(authState.user as MockUser), roles: ["Agent"] };
}

describe("CustomerDetailPage", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    setOwner();
    apiState.getCustomer.mockReset().mockResolvedValue(baseCustomer);
    apiState.listCustomerIdentities.mockReset().mockResolvedValue([]);
    apiState.linkCustomerIdentity.mockReset();
    apiState.revokeCustomerIdentity.mockReset().mockResolvedValue(null);
    apiState.setCustomerExtendedLogging.mockReset().mockResolvedValue(null);
  });

  it("renders customer information once loaded", async () => {
    renderPage();
    expect(await screen.findByRole("heading", { name: "Jane Doe" })).toBeInTheDocument();
    expect(screen.getByText("jane@example.com")).toBeInTheDocument();
  });

  it("shows an anonymized badge for an anonymized customer", async () => {
    apiState.getCustomer.mockResolvedValue({ ...baseCustomer, isAnonymized: true, email: null, name: null });
    renderPage();
    expect(await screen.findAllByText(/anonymized/i)).not.toHaveLength(0);
  });

  it("renders linked identities with validity status", async () => {
    apiState.listCustomerIdentities.mockResolvedValue([activeIdentity]);
    renderPage();

    expect(await screen.findByText("verified-user-123")).toBeInTheDocument();
    expect(screen.getByText("Valid")).toBeInTheDocument();
  });

  it("shows a Revoked badge and hides the Revoke button for an already-revoked identity", async () => {
    apiState.listCustomerIdentities.mockResolvedValue([
      { ...activeIdentity, revokedAt: new Date().toISOString(), revokedByUserId: "u2", isCurrentlyValid: false },
    ]);
    renderPage();

    await screen.findByText("Revoked");
    expect(screen.queryByRole("button", { name: /revoke/i })).not.toBeInTheDocument();
  });

  it("shows the link-identity form and Revoke controls for Owner/Admin", async () => {
    apiState.listCustomerIdentities.mockResolvedValue([activeIdentity]);
    renderPage();

    expect(await screen.findByRole("button", { name: /^revoke$/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /link identity/i })).toBeInTheDocument();
  });

  it("hides the link-identity form and Revoke controls for Agent", async () => {
    setAgent();
    apiState.listCustomerIdentities.mockResolvedValue([activeIdentity]);
    renderPage();

    await screen.findByText("verified-user-123");
    expect(screen.queryByRole("button", { name: /^revoke$/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /link identity/i })).not.toBeInTheDocument();
  });

  it("disables identity linking and shows a note for an anonymized customer", async () => {
    apiState.getCustomer.mockResolvedValue({ ...baseCustomer, isAnonymized: true, email: null, name: null });
    renderPage();

    expect(await screen.findByText(/can no longer be linked to a new identity/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /link identity/i })).not.toBeInTheDocument();
  });

  it("links a new identity and adds it to the list on success", async () => {
    apiState.linkCustomerIdentity.mockResolvedValue({
      ...activeIdentity,
      id: "id-2",
      externalUserId: "brand-new-id",
    });

    renderPage();
    fireEvent.change(await screen.findByLabelText(/external user id/i), { target: { value: "brand-new-id" } });
    fireEvent.click(screen.getByRole("button", { name: /link identity/i }));

    await waitFor(() => expect(screen.getByText("brand-new-id")).toBeInTheDocument());
    expect(apiState.linkCustomerIdentity).toHaveBeenCalledWith("cust-1", {
      channel: "Website",
      verificationMethod: "Otp",
      externalUserId: "brand-new-id",
    });
  });

  it("revokes an identity and updates its status without a page reload", async () => {
    apiState.listCustomerIdentities.mockResolvedValue([activeIdentity]);
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^revoke$/i }));

    await waitFor(() => expect(screen.getByText("Revoked")).toBeInTheDocument());
    expect(apiState.revokeCustomerIdentity).toHaveBeenCalledWith("cust-1", "id-1");
  });

  it("toggles ExtendedLoggingEnabled and reflects the new state", async () => {
    renderPage();

    const toggle = await screen.findByRole("button", { name: /disabled/i });
    fireEvent.click(toggle);

    await waitFor(() => expect(apiState.setCustomerExtendedLogging).toHaveBeenCalledWith("cust-1", true));
    await screen.findByRole("button", { name: /enabled/i });
  });

  it("shows a read-only extended-logging state for Agent, with no toggle button", async () => {
    setAgent();
    renderPage();

    await screen.findByText("Jane Doe");
    expect(screen.queryByRole("button", { name: /disabled/i })).not.toBeInTheDocument();
  });

  it("renders recent conversations linking to the conversation detail page", async () => {
    apiState.getCustomer.mockResolvedValue({
      ...baseCustomer,
      recentConversations: [
        { id: "conv-1", channel: "Website", status: "Open", handledBy: "Ai", lastMessageAt: new Date().toISOString(), createdAt: new Date().toISOString() },
      ],
    });
    renderPage();

    const link = await screen.findByRole("link", { name: /website/i });
    expect(link).toHaveAttribute("href", "/conversations/conv-1");
  });

  it("shows an error state when the customer fails to load", async () => {
    apiState.getCustomer.mockRejectedValue(new Error("network error"));
    renderPage();

    await waitFor(() => expect(screen.getByText(/couldn't load this customer/i)).toBeInTheDocument());
  });
});
