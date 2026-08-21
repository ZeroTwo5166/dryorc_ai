import { render, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import HomePage from "./page";
import { ThemeProvider } from "../lib/theme-context";

// Covers requirement 6 / test item "A 401 from the API correctly sends the
// user to /login": auth-context resolves a genuine 401 from /me to
// { user: null, status: "ready" } (see auth-context.tsx's isAuthError), and
// this page's own effect is what turns that into the actual redirect.
type MockUser = {
  userId: string;
  businessId: string;
  email: string;
  businessName: string;
  planTier: string | null;
  roles: string[];
  expiresAtUtc: string;
};

const { mockRouterReplace, authState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "loading" as "loading" | "ready" },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/home",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

describe("HomePage auth redirect", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.user = null;
    authState.status = "loading";
  });

  it("redirects to /login once the session check resolves with no user (e.g. after a 401)", async () => {
    authState.status = "ready";
    authState.user = null;

    render(
      <ThemeProvider>
        <HomePage />
      </ThemeProvider>,
    );

    await waitFor(() => expect(mockRouterReplace).toHaveBeenCalledWith("/login"));
  });

  it("does not redirect while the session check is still in flight", () => {
    authState.status = "loading";
    authState.user = null;

    render(
      <ThemeProvider>
        <HomePage />
      </ThemeProvider>,
    );

    expect(mockRouterReplace).not.toHaveBeenCalled();
  });

  it("does not redirect when a signed-in user is present", () => {
    authState.status = "ready";
    authState.user = {
      userId: "u1",
      businessId: "b1",
      email: "owner@example.com",
      businessName: "Acme Supply Co.",
      planTier: "trial",
      roles: ["Owner"],
      expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    };

    render(
      <ThemeProvider>
        <HomePage />
      </ThemeProvider>,
    );

    expect(mockRouterReplace).not.toHaveBeenCalled();
  });
});
