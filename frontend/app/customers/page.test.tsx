import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import CustomersPage from "./page";
import { ThemeProvider } from "../lib/theme-context";

function renderPage() {
  return render(
    <ThemeProvider>
      <CustomersPage />
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
  apiState: { listCustomers: vi.fn() },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/customers",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return { ...actual, listCustomers: apiState.listCustomers };
});

describe("CustomersPage", () => {
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
    apiState.listCustomers.mockReset().mockResolvedValue([]);
  });

  it("renders the customer list once loaded", async () => {
    apiState.listCustomers.mockResolvedValue([
      { id: "c1", businessId: "b1", email: "jane@example.com", name: "Jane Doe" },
      { id: "c2", businessId: "b1", email: "no-name@example.com", name: null },
    ]);

    renderPage();

    expect(await screen.findByText("Jane Doe")).toBeInTheDocument();
    expect(screen.getByText("no-name@example.com")).toBeInTheDocument();
  });

  it("links each row to its customer detail page", async () => {
    apiState.listCustomers.mockResolvedValue([
      { id: "c1", businessId: "b1", email: "jane@example.com", name: "Jane Doe" },
    ]);

    renderPage();

    const link = await screen.findByRole("link", { name: /jane doe/i });
    expect(link).toHaveAttribute("href", "/customers/c1");
  });

  it("shows an empty state when there are no customers", async () => {
    renderPage();
    expect(await screen.findByText(/no customers yet/i)).toBeInTheDocument();
  });

  it("shows an error state when the list request fails", async () => {
    apiState.listCustomers.mockRejectedValue(new Error("network error"));

    renderPage();

    await waitFor(() => expect(screen.getByText(/couldn't load customers/i)).toBeInTheDocument());
  });

  it("redirects to /login when there is no authenticated user", () => {
    authState.user = null;
    renderPage();
    expect(mockRouterReplace).toHaveBeenCalledWith("/login");
  });
});
