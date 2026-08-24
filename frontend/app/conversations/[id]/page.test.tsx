import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import ConversationDetailPage from "./page";
import { ApiError } from "../../lib/api";
import { ThemeProvider } from "../../lib/theme-context";

function renderPage() {
  return render(
    <ThemeProvider>
      <ConversationDetailPage />
    </ThemeProvider>,
  );
}

// Step 9 staff-reply composer — same mocking convention as
// app/home/page.test.tsx (mock next/navigation + auth-context), plus a
// partial mock of ../../lib/api so no real fetch is ever attempted.
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
    getConversationMessages: vi.fn(),
    createConversationMessage: vi.fn(),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  useParams: () => ({ id: "conv-1" }),
  usePathname: () => "/conversations/conv-1",
}));

vi.mock("../../lib/auth-context", () => ({
  useAuth: () => ({ user: authState.user, status: authState.status, logout: vi.fn() }),
}));

vi.mock("../../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../../lib/api")>("../../lib/api");
  return {
    ...actual,
    getConversationMessages: apiState.getConversationMessages,
    createConversationMessage: apiState.createConversationMessage,
  };
});

const PLACEHOLDER = "Reply to this conversation…";

describe("ConversationDetailPage staff-reply composer", () => {
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
    apiState.getConversationMessages.mockReset().mockResolvedValue([
      { id: "m1", role: "Customer", content: "Is my order shipped?", createdAt: new Date().toISOString() },
    ]);
    apiState.createConversationMessage.mockReset();
  });

  it("renders the composer once the conversation loads", async () => {
    renderPage();
    expect(await screen.findByPlaceholderText(PLACEHOLDER)).toBeInTheDocument();
  });

  it("blocks submission of blank/whitespace-only content", async () => {
    renderPage();
    const input = await screen.findByPlaceholderText(PLACEHOLDER);
    const sendButton = screen.getByRole("button", { name: /send/i });

    expect(sendButton).toBeDisabled(); // empty draft

    fireEvent.change(input, { target: { value: "   " } });
    expect(sendButton).toBeDisabled(); // whitespace-only draft
    expect(apiState.createConversationMessage).not.toHaveBeenCalled();
  });

  it("appends the created Agent message and clears the input on success", async () => {
    apiState.createConversationMessage.mockResolvedValue({
      id: "m2",
      role: "Agent",
      content: "Yes, it shipped yesterday!",
      createdAt: new Date().toISOString(),
    });

    renderPage();
    const input = (await screen.findByPlaceholderText(PLACEHOLDER)) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "Yes, it shipped yesterday!" } });
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    await waitFor(() => expect(screen.getByText("Yes, it shipped yesterday!")).toBeInTheDocument());
    expect(apiState.createConversationMessage).toHaveBeenCalledWith("conv-1", "Yes, it shipped yesterday!");
    expect(input.value).toBe("");
  });

  it("disables Send while a submission is in flight, so a second click cannot double-submit", async () => {
    let resolveSend: (value: unknown) => void = () => {};
    apiState.createConversationMessage.mockReturnValue(
      new Promise((resolve) => {
        resolveSend = resolve;
      }),
    );

    renderPage();
    const input = await screen.findByPlaceholderText(PLACEHOLDER);
    fireEvent.change(input, { target: { value: "Checking on this now." } });
    const sendButton = screen.getByRole("button", { name: /send/i });

    fireEvent.click(sendButton);
    await waitFor(() => expect(sendButton).toBeDisabled());

    fireEvent.click(sendButton); // second click while the first request is still pending
    expect(apiState.createConversationMessage).toHaveBeenCalledTimes(1);

    resolveSend({ id: "m3", role: "Agent", content: "Checking on this now.", createdAt: new Date().toISOString() });
    // Draft clears on success, which alone keeps Send disabled (nothing to
    // submit) — so the meaningful assertion here is that the button left
    // its "Sending…" in-flight state, not the always-disabled-when-empty state.
    await waitFor(() => expect(sendButton).toHaveTextContent("Send"));
  });

  it("shows a visible error and preserves the draft when sending fails", async () => {
    apiState.createConversationMessage.mockRejectedValue(new ApiError(500, "Something went wrong."));

    renderPage();
    const input = (await screen.findByPlaceholderText(PLACEHOLDER)) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "This will fail." } });
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    await waitFor(() => expect(screen.getByText("Something went wrong.")).toBeInTheDocument());
    expect(input.value).toBe("This will fail."); // not silently discarded
  });

  it("still renders existing customer/AI messages, unaffected by the composer", async () => {
    apiState.getConversationMessages.mockResolvedValue([
      { id: "m1", role: "Customer", content: "Do you ship internationally?", createdAt: new Date().toISOString() },
      { id: "m2", role: "Ai", content: "Yes, we ship worldwide.", createdAt: new Date().toISOString() },
    ]);

    renderPage();

    await waitFor(() => expect(screen.getByText("Do you ship internationally?")).toBeInTheDocument());
    expect(screen.getByText("Yes, we ship worldwide.")).toBeInTheDocument();
  });
});
