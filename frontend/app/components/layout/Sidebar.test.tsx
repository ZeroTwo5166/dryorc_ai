import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { Sidebar } from "./Sidebar";
import { ThemeProvider } from "../../lib/theme-context";

// Step 23 — the Analyst nav item's presence now depends on
// user.aiBusinessAnalystEnabled (design §4: "the client hides/removes the
// entry point entirely"), which required moving navSections from a static
// module-level const into the component body — a real structural change to
// a shared component. These tests exist specifically to catch a regression
// in either direction: the gate not hiding Analyst when it should, or the
// refactor accidentally breaking/dropping any of the other, unrelated nav
// items in the process.
type MockUser = {
  userId: string;
  businessId: string;
  email: string;
  businessName: string;
  planTier: string | null;
  roles: string[];
  expiresAtUtc: string;
  aiBusinessAnalystEnabled?: boolean;
};

const { authState } = vi.hoisted(() => ({
  authState: { user: null as MockUser | null },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  usePathname: () => "/home",
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: "ready", logout: vi.fn() }),
}));

function renderSidebar() {
  return render(
    <ThemeProvider>
      <Sidebar />
    </ThemeProvider>,
  );
}

function baseUser(overrides?: Partial<MockUser>): MockUser {
  return {
    userId: "u1",
    businessId: "b1",
    email: "staff@example.com",
    businessName: "Acme Supply Co.",
    planTier: "trial",
    roles: ["Owner"],
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    ...overrides,
  };
}

describe("Sidebar — Analyst nav item gating", () => {
  beforeEach(() => {
    authState.user = null;
  });

  it("does not render the Analyst nav item when aiBusinessAnalystEnabled is false", () => {
    authState.user = baseUser({ aiBusinessAnalystEnabled: false });
    renderSidebar();
    expect(screen.queryByText("Analyst")).not.toBeInTheDocument();
  });

  it("does not render the Analyst nav item when aiBusinessAnalystEnabled is absent", () => {
    authState.user = baseUser(); // no aiBusinessAnalystEnabled at all — must fail closed, not open
    renderSidebar();
    expect(screen.queryByText("Analyst")).not.toBeInTheDocument();
  });

  it("renders the Analyst nav item, linking to /analyst, when aiBusinessAnalystEnabled is true", () => {
    authState.user = baseUser({ aiBusinessAnalystEnabled: true });
    renderSidebar();
    // Exact-match regex, deliberately — /analyst/i alone would also match
    // the unrelated "Analytics" link's accessible name.
    const link = screen.getByRole("link", { name: /^analyst$/i });
    expect(link).toHaveAttribute("href", "/analyst");
  });

  it("still renders every other nav item correctly regardless of aiBusinessAnalystEnabled", () => {
    authState.user = baseUser({ aiBusinessAnalystEnabled: true });
    renderSidebar();
    for (const [label, href] of [
      ["Overview", "/home"],
      ["Inbox", "/conversations"],
      ["Customers", "/customers"],
      ["Knowledge base", "/knowledge"],
      ["Tickets", "/tickets"],
      ["Team", "/team"],
      ["Analytics", "/analytics"],
      ["Settings", "/settings/integrations"],
    ] as const) {
      expect(screen.getByRole("link", { name: label })).toHaveAttribute("href", href);
    }
    // Integrations is still the "Soon" disabled placeholder, not a link;
    // Team is a real link for every staff user (staff invite, ED-38).
    expect(screen.getByText("Team")).toBeInTheDocument();
    expect(screen.getByText("Integrations")).toBeInTheDocument();
    expect(screen.getAllByText("Soon")).toHaveLength(1);
  });
});
