import { describe, it, expect, vi, beforeEach } from "vitest";
import { WidgetUI, isValidPublicKey } from "../src/ui";
import type { WidgetMessage } from "../src/state";

function mount(overrides: Partial<{ businessName: string; onSend: (c: string) => void; onRetry: () => void }> = {}) {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const onSend = overrides.onSend ?? vi.fn();
  const onRetry = overrides.onRetry ?? vi.fn();
  const ui = new WidgetUI(container, {
    businessName: overrides.businessName,
    onSend,
    onRetry,
  });
  return { container, ui, onSend: onSend as ReturnType<typeof vi.fn>, onRetry: onRetry as ReturnType<typeof vi.fn> };
}

describe("isValidPublicKey", () => {
  it("rejects null", () => {
    expect(isValidPublicKey(null)).toBe(false);
  });

  it("rejects undefined", () => {
    expect(isValidPublicKey(undefined)).toBe(false);
  });

  it("rejects an empty string", () => {
    expect(isValidPublicKey("")).toBe(false);
  });

  it("rejects a whitespace-only string", () => {
    expect(isValidPublicKey("   ")).toBe(false);
  });

  it("accepts a non-blank string", () => {
    expect(isValidPublicKey("abc123")).toBe(true);
  });
});

describe("WidgetUI", () => {
  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("renders a visible launcher on construction", () => {
    const { container } = mount();
    const launcher = container.querySelector(".launcher");
    expect(launcher).not.toBeNull();
    expect(launcher?.getAttribute("aria-label")).toBe("Open chat");
  });

  it("starts with the panel closed", () => {
    const { container, ui } = mount();
    const panel = container.querySelector<HTMLElement>(".panel");
    expect(panel?.hidden).toBe(true);
    expect(ui.isOpen).toBe(false);
  });

  it("opens the panel when the launcher is clicked", () => {
    const { container, ui } = mount();
    container.querySelector<HTMLElement>(".launcher")!.click();

    expect(ui.isOpen).toBe(true);
    expect(container.querySelector<HTMLElement>(".panel")!.hidden).toBe(false);
  });

  it("closes the panel when the close button is clicked", () => {
    const { container, ui } = mount();
    ui.open();
    container.querySelector<HTMLElement>(".close")!.click();

    expect(ui.isOpen).toBe(false);
  });

  it("shows the business name in the header when provided", () => {
    const { container } = mount({ businessName: "Acme Corp" });
    expect(container.querySelector(".header-title")?.textContent).toBe("Acme Corp");
  });

  it("falls back to a generic header when no business name is provided", () => {
    const { container } = mount();
    expect(container.querySelector(".header-title")?.textContent).toBe("Chat");
  });

  it("renders a full transcript via renderMessages", () => {
    const { container, ui } = mount();
    const transcript: WidgetMessage[] = [
      { id: "m1", role: "customer", content: "hi there", createdAt: "2026-01-01T00:00:00Z" },
      { id: "m2", role: "ai", content: "hello!", createdAt: "2026-01-01T00:00:01Z" },
    ];

    ui.renderMessages(transcript);

    const rendered = container.querySelectorAll(".message");
    expect(rendered).toHaveLength(2);
    expect(rendered[0].textContent).toBe("hi there");
    expect(rendered[1].textContent).toBe("hello!");
  });

  it("visually distinguishes customer and AI messages via distinct classes", () => {
    const { container, ui } = mount();
    ui.renderMessages([
      { id: "m1", role: "customer", content: "customer text", createdAt: "2026-01-01T00:00:00Z" },
      { id: "m2", role: "ai", content: "ai text", createdAt: "2026-01-01T00:00:01Z" },
    ]);

    const customerMsg = container.querySelector(".message-customer");
    const aiMsg = container.querySelector(".message-ai");
    expect(customerMsg?.textContent).toBe("customer text");
    expect(aiMsg?.textContent).toBe("ai text");
    expect(customerMsg?.classList.contains("message-ai")).toBe(false);
    expect(aiMsg?.classList.contains("message-customer")).toBe(false);
  });

  it("visually distinguishes agent messages from both customer and AI, with no staff identity rendered", () => {
    const { container, ui } = mount();
    ui.renderMessages([
      { id: "m1", role: "ai", content: "ai text", createdAt: "2026-01-01T00:00:00Z" },
      { id: "m2", role: "agent", content: "a human replied", createdAt: "2026-01-01T00:00:01Z" },
    ]);

    const aiMsg = container.querySelector(".message-ai");
    const agentMsg = container.querySelector(".message-agent");
    expect(agentMsg?.textContent).toBe("a human replied"); // content only — no author name/id ever rendered
    expect(agentMsg?.classList.contains("message-ai")).toBe(false);
    expect(aiMsg?.classList.contains("message-agent")).toBe(false);
  });

  it("appendMessage adds one more message without clearing existing ones", () => {
    const { container, ui } = mount();
    ui.renderMessages([{ id: "m1", role: "customer", content: "first", createdAt: "2026-01-01T00:00:00Z" }]);
    ui.appendMessage({ id: "m2", role: "ai", content: "second", createdAt: "2026-01-01T00:00:01Z" });

    expect(container.querySelectorAll(".message")).toHaveLength(2);
  });

  it("calls onSend with the trimmed input value when Send is clicked", () => {
    const { container, onSend } = mount();
    const input = container.querySelector<HTMLInputElement>(".input")!;
    input.value = "  hello there  ";
    container.querySelector<HTMLButtonElement>(".send")!.click();

    expect(onSend).toHaveBeenCalledWith("hello there");
  });

  it("clears the input after sending", () => {
    const { container } = mount();
    const input = container.querySelector<HTMLInputElement>(".input")!;
    input.value = "hello";
    container.querySelector<HTMLButtonElement>(".send")!.click();

    expect(input.value).toBe("");
  });

  it("does not call onSend for a blank/whitespace-only message", () => {
    const { container, onSend } = mount();
    const input = container.querySelector<HTMLInputElement>(".input")!;
    input.value = "   ";
    container.querySelector<HTMLButtonElement>(".send")!.click();

    expect(onSend).not.toHaveBeenCalled();
  });

  it("sends on Enter key press in the input", () => {
    const { container, onSend } = mount();
    const input = container.querySelector<HTMLInputElement>(".input")!;
    input.value = "enter-sent message";
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));

    expect(onSend).toHaveBeenCalledWith("enter-sent message");
  });

  it("does not send on non-Enter key presses", () => {
    const { container, onSend } = mount();
    const input = container.querySelector<HTMLInputElement>(".input")!;
    input.value = "not sent yet";
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));

    expect(onSend).not.toHaveBeenCalled();
  });

  it("disables the input and send button while sending", () => {
    const { container, ui } = mount();
    ui.setSending(true);

    expect(container.querySelector<HTMLInputElement>(".input")!.disabled).toBe(true);
    expect(container.querySelector<HTMLButtonElement>(".send")!.disabled).toBe(true);
  });

  it("re-enables the input and send button once sending finishes", () => {
    const { container, ui } = mount();
    ui.setSending(true);
    ui.setSending(false);

    expect(container.querySelector<HTMLInputElement>(".input")!.disabled).toBe(false);
    expect(container.querySelector<HTMLButtonElement>(".send")!.disabled).toBe(false);
  });

  it("shows a pending/typing indicator while sending", () => {
    const { container, ui } = mount();
    ui.setSending(true);
    expect(container.querySelector<HTMLElement>(".typing")!.hidden).toBe(false);

    ui.setSending(false);
    expect(container.querySelector<HTMLElement>(".typing")!.hidden).toBe(true);
  });

  it("displays an inline error with showError", () => {
    const { container, ui } = mount();
    ui.showError("Something went wrong");

    const errorEl = container.querySelector<HTMLElement>(".error")!;
    expect(errorEl.hidden).toBe(false);
    expect(errorEl.textContent).toContain("Something went wrong");
  });

  it("hides the error once clearError is called", () => {
    const { container, ui } = mount();
    ui.showError("boom");
    ui.clearError();

    expect(container.querySelector<HTMLElement>(".error")!.hidden).toBe(true);
  });

  it("shows a retry button only when the error is retryable", () => {
    const { container, ui } = mount();
    ui.showError("network problem", { retryable: true });
    expect(container.querySelector<HTMLButtonElement>(".retry")!.hidden).toBe(false);

    ui.showError("bad request", { retryable: false });
    expect(container.querySelector<HTMLButtonElement>(".retry")!.hidden).toBe(true);
  });

  it("calls onRetry when the retry button is clicked", () => {
    const { container, onRetry, ui } = mount();
    ui.showError("network problem", { retryable: true });
    container.querySelector<HTMLButtonElement>(".retry")!.click();

    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  describe("updateMessage (pending/sent/failed state transitions)", () => {
    it("renders a pending customer message with a distinct class from a plain sent one", () => {
      const { container, ui } = mount();
      ui.appendMessage({ id: "m1", role: "customer", content: "hi", createdAt: "t", status: "pending" });

      const node = container.querySelector(".message-customer")!;
      expect(node.className).toContain("message-pending");
    });

    it("renders a failed customer message with a distinct class", () => {
      const { container, ui } = mount();
      ui.appendMessage({ id: "m1", role: "customer", content: "hi", createdAt: "t", status: "failed" });

      const node = container.querySelector(".message-customer")!;
      expect(node.className).toContain("message-failed");
    });

    it("a sent (or statusless) customer message has neither pending nor failed classes", () => {
      const { container, ui } = mount();
      ui.appendMessage({ id: "m1", role: "customer", content: "hi", createdAt: "t", status: "sent" });

      const node = container.querySelector(".message-customer")!;
      expect(node.className).not.toContain("message-pending");
      expect(node.className).not.toContain("message-failed");
    });

    it("updateMessage transitions the same DOM node in place rather than appending a new one", () => {
      const { container, ui } = mount();
      const message = { id: "m1", role: "customer" as const, content: "hi", createdAt: "t", status: "pending" as const };
      ui.appendMessage(message);
      expect(container.querySelectorAll(".message")).toHaveLength(1);

      ui.updateMessage({ ...message, status: "sent" });

      expect(container.querySelectorAll(".message")).toHaveLength(1);
      const node = container.querySelector(".message-customer")!;
      expect(node.className).not.toContain("message-pending");
      expect(node.textContent).toBe("hi");
    });

    it("updateMessage on an unknown id falls back to appending rather than silently dropping the message", () => {
      const { container, ui } = mount();
      ui.updateMessage({ id: "never-appended", role: "customer", content: "fallback", createdAt: "t", status: "failed" });

      expect(container.querySelectorAll(".message")).toHaveLength(1);
      expect(container.querySelector(".message")!.textContent).toBe("fallback");
    });
  });
});
