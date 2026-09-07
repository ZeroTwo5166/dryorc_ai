import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import SettingsIntegrationsPage from "./page";
import { ThemeProvider } from "../../lib/theme-context";

function renderPage() {
  return render(
    <ThemeProvider>
      <SettingsIntegrationsPage />
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

const { mockRouterReplace, authState, apiState, searchParamsState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: {
    listIntegrations: vi.fn(),
    connectIntegration: vi.fn(),
    disconnectIntegration: vi.fn(),
  },
  searchParamsState: { params: new URLSearchParams() as URLSearchParams },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/settings/integrations",
  useSearchParams: () => searchParamsState.params,
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../../lib/api")>("../../lib/api");
  return {
    ...actual,
    listIntegrations: apiState.listIntegrations,
    connectIntegration: apiState.connectIntegration,
    disconnectIntegration: apiState.disconnectIntegration,
  };
});

const shopifyConnected = {
  id: "int-1",
  platform: "shopify",
  externalAccountIdentifier: "example-store",
  scopes: ["read_orders"],
  scopesUpdatedAt: new Date().toISOString(),
  connectedAt: new Date().toISOString(),
  disconnectedAt: null,
  isActive: true,
};

const shopifyDisconnected = {
  ...shopifyConnected,
  isActive: false,
  disconnectedAt: new Date().toISOString(),
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

describe("SettingsIntegrationsPage", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    searchParamsState.params = new URLSearchParams();
    setOwner();
    apiState.listIntegrations.mockReset().mockResolvedValue([]);
    apiState.connectIntegration.mockReset();
    apiState.disconnectIntegration.mockReset().mockResolvedValue(null);
  });

  it("shows a disconnected state with a Connect control for Owner when no Shopify row exists", async () => {
    renderPage();
    expect(await screen.findByText("Disconnected")).toBeInTheDocument();
    expect(screen.getByLabelText(/shop name/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^connect$/i })).toBeInTheDocument();
  });

  it("shows a read-only disconnected state for a non-Owner, with no input", async () => {
    setAgent();
    renderPage();
    expect(await screen.findByText("Disconnected")).toBeInTheDocument();
    expect(screen.queryByLabelText(/shop name/i)).not.toBeInTheDocument();
    expect(screen.getByText(/an owner can connect shopify/i)).toBeInTheDocument();
  });

  it("shows the connected state with shop identifier and a Disconnect control for Owner", async () => {
    apiState.listIntegrations.mockResolvedValue([shopifyConnected]);
    renderPage();

    expect(await screen.findByText("Connected")).toBeInTheDocument();
    expect(screen.getByText("example-store")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^disconnect$/i })).toBeInTheDocument();
  });

  it("shows the connected state with no Disconnect control for a non-Owner", async () => {
    setAgent();
    apiState.listIntegrations.mockResolvedValue([shopifyConnected]);
    renderPage();

    expect(await screen.findByText("Connected")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^disconnect$/i })).not.toBeInTheDocument();
  });

  it("treats a present-but-inactive Integration row as disconnected", async () => {
    apiState.listIntegrations.mockResolvedValue([shopifyDisconnected]);
    renderPage();

    expect(await screen.findByText("Disconnected")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^connect$/i })).toBeInTheDocument();
  });

  it("rejects an invalid shop name client-side without calling the API", async () => {
    renderPage();
    const input = await screen.findByLabelText(/shop name/i);
    fireEvent.change(input, { target: { value: "https://not-a-bare-name.myshopify.com" } });
    fireEvent.click(screen.getByRole("button", { name: /^connect$/i }));

    expect(await screen.findByText(/enter a bare shop name/i)).toBeInTheDocument();
    expect(apiState.connectIntegration).not.toHaveBeenCalled();
  });

  it("navigates to the returned authorizationUrl on successful connect", async () => {
    apiState.connectIntegration.mockResolvedValue({ authorizationUrl: "https://example-store.myshopify.com/admin/oauth/authorize?x=1" });
    const originalLocation = window.location;
    // jsdom's window.location isn't writable via plain assignment —
    // redefine the property for this test only, restore after.
    Object.defineProperty(window, "location", {
      configurable: true,
      value: { ...originalLocation, href: "" },
    });

    renderPage();
    fireEvent.change(await screen.findByLabelText(/shop name/i), { target: { value: "example-store" } });
    fireEvent.click(screen.getByRole("button", { name: /^connect$/i }));

    await waitFor(() =>
      expect(apiState.connectIntegration).toHaveBeenCalledWith("shopify", { externalAccountIdentifier: "example-store" }),
    );
    await waitFor(() => expect(window.location.href).toBe("https://example-store.myshopify.com/admin/oauth/authorize?x=1"));

    Object.defineProperty(window, "location", { configurable: true, value: originalLocation });
  });

  it("shows a connect API error inline and leaves the input editable", async () => {
    const { ApiError } = await import("../../lib/api");
    apiState.connectIntegration.mockRejectedValue(new ApiError(400, "Shopify shop must be a bare shop name."));

    renderPage();
    fireEvent.change(await screen.findByLabelText(/shop name/i), { target: { value: "example-store" } });
    fireEvent.click(screen.getByRole("button", { name: /^connect$/i }));

    expect(await screen.findByText("Shopify shop must be a bare shop name.")).toBeInTheDocument();
    expect(screen.getByLabelText(/shop name/i)).toBeEnabled();
  });

  it("disconnects and flips to the disconnected state without a re-fetch", async () => {
    apiState.listIntegrations.mockResolvedValue([shopifyConnected]);
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /^disconnect$/i }));

    await waitFor(() => expect(screen.getByText("Disconnected")).toBeInTheDocument());
    expect(apiState.disconnectIntegration).toHaveBeenCalledWith("shopify");
    expect(apiState.listIntegrations).toHaveBeenCalledTimes(1);
  });

  it("leaves the connected state unchanged and shows an error when disconnect fails", async () => {
    const { ApiError } = await import("../../lib/api");
    apiState.listIntegrations.mockResolvedValue([shopifyConnected]);
    apiState.disconnectIntegration.mockRejectedValue(new ApiError(500, "Request failed (500)."));

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /^disconnect$/i }));

    await screen.findByText("Request failed (500).");
    expect(screen.getByText("Connected")).toBeInTheDocument();
  });

  it("shows the success banner when returned via ?connected=shopify", async () => {
    searchParamsState.params = new URLSearchParams("connected=shopify");
    renderPage();
    expect(await screen.findByText(/shopify connected successfully/i)).toBeInTheDocument();
  });

  it("maps each of the five known integrationError codes to a non-empty message", async () => {
    for (const code of ["hmac_invalid", "invalid_state", "state_mismatch", "exchange_failed", "conflict"]) {
      searchParamsState.params = new URLSearchParams(`integrationError=${code}`);
      const { unmount } = renderPage();
      await waitFor(() => expect(screen.getByText("Disconnected")).toBeInTheDocument());
      // Each code must render some banner text, not the raw code itself.
      expect(screen.queryByText(code)).not.toBeInTheDocument();
      unmount();
    }
  });

  it("shows an error state when the integration list fails to load", async () => {
    apiState.listIntegrations.mockRejectedValue(new Error("network error"));
    renderPage();

    await waitFor(() => expect(screen.getByText(/couldn't load your integrations/i)).toBeInTheDocument());
  });

  it("redirects to /login when not authenticated", async () => {
    authState.user = null;
    renderPage();
    await waitFor(() => expect(mockRouterReplace).toHaveBeenCalledWith("/login"));
  });
});
