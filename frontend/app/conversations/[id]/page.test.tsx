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
    returnConversationToAi: vi.fn(),
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
    returnConversationToAi: apiState.returnConversationToAi,
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
    apiState.getConversationMessages.mockReset().mockResolvedValue({
      handledBy: "Ai",
      messages: [{ id: "m1", role: "Customer", content: "Is my order shipped?", createdAt: new Date().toISOString() }],
    });
    apiState.createConversationMessage.mockReset();
    apiState.returnConversationToAi.mockReset();
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
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Ai",
      messages: [
        { id: "m1", role: "Customer", content: "Do you ship internationally?", createdAt: new Date().toISOString() },
        { id: "m2", role: "Ai", content: "Yes, we ship worldwide.", createdAt: new Date().toISOString() },
      ],
    });

    renderPage();

    await waitFor(() => expect(screen.getByText("Do you ship internationally?")).toBeInTheDocument());
    expect(screen.getByText("Yes, we ship worldwide.")).toBeInTheDocument();
  });
});

// Step 15: Human -> Ai reversal
describe("ConversationDetailPage return-to-AI action", () => {
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
    apiState.getConversationMessages.mockReset();
    apiState.createConversationMessage.mockReset();
    apiState.returnConversationToAi.mockReset();
  });

  it("shows a Human badge and a Return to AI button when the conversation is Human-handled", async () => {
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Human",
      messages: [{ id: "m1", role: "Customer", content: "Still waiting on a reply", createdAt: new Date().toISOString() }],
    });

    renderPage();

    expect(await screen.findByText("Handled by Human")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /return to ai/i })).toBeInTheDocument();
  });

  it("shows an AI badge and no Return to AI button when the conversation is already Ai-handled", async () => {
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Ai",
      messages: [{ id: "m1", role: "Customer", content: "Hello", createdAt: new Date().toISOString() }],
    });

    renderPage();

    expect(await screen.findByText("Handled by AI")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /return to ai/i })).not.toBeInTheDocument();
  });

  it("calls returnConversationToAi and updates the badge on success, removing the button", async () => {
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Human",
      messages: [{ id: "m1", role: "Customer", content: "Still waiting on a reply", createdAt: new Date().toISOString() }],
    });
    apiState.returnConversationToAi.mockResolvedValue(null);

    renderPage();
    const button = await screen.findByRole("button", { name: /return to ai/i });
    fireEvent.click(button);

    await waitFor(() => expect(screen.getByText("Handled by AI")).toBeInTheDocument());
    expect(apiState.returnConversationToAi).toHaveBeenCalledWith("conv-1");
    expect(screen.queryByRole("button", { name: /^return to ai$/i })).not.toBeInTheDocument();
  });

  it("disables the button while the request is in flight, so a second click cannot double-submit", async () => {
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Human",
      messages: [],
    });
    let resolveReturn: (value: unknown) => void = () => {};
    apiState.returnConversationToAi.mockReturnValue(
      new Promise((resolve) => {
        resolveReturn = resolve;
      }),
    );

    renderPage();
    const button = await screen.findByRole("button", { name: /return to ai/i });

    fireEvent.click(button);
    await waitFor(() => expect(button).toBeDisabled());
    expect(button).toHaveTextContent("Returning to AI…");

    fireEvent.click(button); // second click while the first request is still pending
    expect(apiState.returnConversationToAi).toHaveBeenCalledTimes(1);

    resolveReturn(null);
    await waitFor(() => expect(screen.getByText("Handled by AI")).toBeInTheDocument());
  });

  it("shows a visible error and leaves the conversation shown as Human-handled when the request fails", async () => {
    apiState.getConversationMessages.mockResolvedValue({
      handledBy: "Human",
      messages: [],
    });
    apiState.returnConversationToAi.mockRejectedValue(new ApiError(500, "Something went wrong."));

    renderPage();
    const button = await screen.findByRole("button", { name: /return to ai/i });
    fireEvent.click(button);

    await waitFor(() => expect(screen.getByText("Something went wrong.")).toBeInTheDocument());
    // Not falsely optimistic: still shows Human, button still present/enabled to retry.
    expect(screen.getByText("Handled by Human")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /return to ai/i })).not.toBeDisabled();
  });
});
