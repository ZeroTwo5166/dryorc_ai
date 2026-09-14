import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import AnalystPage from "./page";
import { ThemeProvider } from "../lib/theme-context";
import { ApiError, type AnalystAnswerResponse } from "../lib/api";

function renderPage() {
  return render(
    <ThemeProvider>
      <AnalystPage />
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
  aiBusinessAnalystEnabled: boolean;
};

const { mockRouterReplace, authState, apiState } = vi.hoisted(() => ({
  mockRouterReplace: vi.fn(),
  authState: { user: null as MockUser | null, status: "ready" as "loading" | "ready" },
  apiState: { askAnalyst: vi.fn() },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/analyst",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return { ...actual, askAnalyst: apiState.askAnalyst };
});

function baseUser(overrides?: Partial<MockUser>): MockUser {
  return {
    userId: "u1",
    businessId: "b1",
    email: "staff@example.com",
    businessName: "Acme Supply Co.",
    planTier: "trial",
    roles: ["Owner"],
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    aiBusinessAnalystEnabled: true,
    ...overrides,
  };
}

function answer(outcome: AnalystAnswerResponse["outcome"], message: string): AnalystAnswerResponse {
  return { outcome, message };
}

async function ask(question: string) {
  const input = screen.getByLabelText(/question for the analyst/i);
  fireEvent.change(input, { target: { value: question } });
  fireEvent.click(screen.getByRole("button", { name: /^ask$/i }));
}

describe("AnalystPage", () => {
  beforeEach(() => {
    mockRouterReplace.mockClear();
    authState.status = "ready";
    authState.user = baseUser();
    apiState.askAnalyst.mockReset();
  });

  // 1. All 5 outcome kinds render through askAnalyst()'s single unified path
  it.each([
    ["Answered", "Conversations were up 12% this week."],
    ["Refused", "That question isn't something I can answer from the available data."],
    ["CapabilityFailed", "Something went wrong looking that up. Try again shortly."],
    ["ClaudeCallFailed", "The AI service didn't respond. Try again shortly."],
    ["Disabled", "The AI Business Analyst is not enabled for this business."],
  ] as const)("renders the %s outcome's message as the Analyst's reply", async (outcome, message) => {
    apiState.askAnalyst.mockResolvedValue(answer(outcome, message));
    renderPage();
    await ask("How are we doing?");

    await waitFor(() => expect(apiState.askAnalyst).toHaveBeenCalledWith("How are we doing?"));
    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.getByText("How are we doing?")).toBeInTheDocument(); // the question itself stays visible too
  });

  // Failure-shaped outcomes get a visually distinct (destructive) bubble;
  // Answered/Refused don't — both "valid, non-error" per the design doc.
  it("visually distinguishes failure outcomes (Disabled/CapabilityFailed/ClaudeCallFailed) from Answered/Refused", async () => {
    apiState.askAnalyst.mockResolvedValue(answer("CapabilityFailed", "Something broke."));
    renderPage();
    await ask("Q1");
    const failureBubble = (await screen.findByText("Something broke.")).closest("div");
    expect(failureBubble?.className).toContain("destructive");

    apiState.askAnalyst.mockResolvedValue(answer("Answered", "Here's your answer."));
    await ask("Q2");
    const okBubble = (await screen.findByText("Here's your answer.")).closest("div");
    expect(okBubble?.className).not.toContain("destructive");
  });

  // 2. Blank-question submit guard
  it("does not call askAnalyst for a blank or whitespace-only question", async () => {
    renderPage();
    const button = screen.getByRole("button", { name: /^ask$/i });
    expect(button).toBeDisabled(); // empty draft

    const input = screen.getByLabelText(/question for the analyst/i);
    fireEvent.change(input, { target: { value: "   " } });
    expect(screen.getByRole("button", { name: /^ask$/i })).toBeDisabled();

    fireEvent.click(button);
    expect(apiState.askAnalyst).not.toHaveBeenCalled();
  });

  // Genuine request failures (network error, thrown ApiError) go through the
  // FormError banner, distinct from the per-exchange bubble rendering above.
  it("shows a FormError banner (not a bubble) when the request itself fails", async () => {
    apiState.askAnalyst.mockRejectedValue(new ApiError(0, "Network error."));
    renderPage();
    await ask("Q1");
    await waitFor(() => expect(screen.getByText("Network error.")).toBeInTheDocument());
    // Never added as a fake exchange bubble — the question itself is not echoed.
    expect(screen.queryByText("Q1")).not.toBeInTheDocument();
  });

  it("clears the draft and disables the button again after a successful ask", async () => {
    apiState.askAnalyst.mockResolvedValue(answer("Answered", "Done."));
    renderPage();
    await ask("Q1");
    await screen.findByText("Done.");
    expect(screen.getByLabelText(/question for the analyst/i)).toHaveValue("");
    expect(screen.getByRole("button", { name: /^ask$/i })).toBeDisabled();
  });

  // 3. Disabled-redirect fires before any endpoint call — never even a
  // flash of the composer, and askAnalyst is never reached.
  it("redirects to /home without calling askAnalyst when aiBusinessAnalystEnabled is false", async () => {
    authState.user = baseUser({ aiBusinessAnalystEnabled: false });
    renderPage();
    await waitFor(() => expect(mockRouterReplace).toHaveBeenCalledWith("/home"));
    expect(apiState.askAnalyst).not.toHaveBeenCalled();
    expect(screen.queryByLabelText(/question for the analyst/i)).not.toBeInTheDocument();
  });

  it("redirects to /login when there is no authenticated user, without calling askAnalyst", async () => {
    authState.user = null;
    renderPage();
    await waitFor(() => expect(mockRouterReplace).toHaveBeenCalledWith("/login"));
    expect(apiState.askAnalyst).not.toHaveBeenCalled();
  });

  it("shows the loading spinner, not the composer, while the session check is in flight", () => {
    authState.status = "loading";
    authState.user = null;
    renderPage();
    expect(document.querySelector(".animate-spin")).not.toBeNull();
    expect(screen.queryByLabelText(/question for the analyst/i)).not.toBeInTheDocument();
  });

  it("shows the empty-state prompt before any question is asked", () => {
    renderPage();
    expect(screen.getByText(/ask your first question/i)).toBeInTheDocument();
  });
});
