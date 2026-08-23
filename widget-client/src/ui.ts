import type { WidgetMessage } from "./state";

/**
 * A missing/blank data-public-key is an integration mistake by whoever
 * embedded the script, not something an end visitor should see a broken
 * chat bubble over — the caller is expected to log a console error and
 * skip rendering entirely when this returns false. Exported so that
 * exact "fail cleanly" behavior is directly testable.
 */
export function isValidPublicKey(key: string | null | undefined): key is string {
  return typeof key === "string" && key.trim().length > 0;
}

export interface WidgetUIOptions {
  businessName?: string;
  onSend: (content: string) => void;
  onRetry: () => void;
}

type Container = Element | ShadowRoot;

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className?: string,
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/**
 * Owns the widget's whole DOM tree (launcher + panel) inside whatever
 * container it's given — a real ShadowRoot in production, a plain element
 * in tests. Pure DOM/event plumbing only; it never calls the API or
 * touches localStorage itself (see index.ts for that orchestration) —
 * kept that way specifically so this class is testable without a network.
 */
export class WidgetUI {
  private readonly launcher: HTMLButtonElement;
  private readonly panel: HTMLDivElement;
  private readonly messagesEl: HTMLDivElement;
  private readonly typingEl: HTMLDivElement;
  private readonly errorEl: HTMLDivElement;
  private readonly errorTextEl: HTMLSpanElement;
  private readonly retryButton: HTMLButtonElement;
  private readonly input: HTMLInputElement;
  private readonly sendButton: HTMLButtonElement;
  private readonly options: WidgetUIOptions;

  constructor(container: Container, options: WidgetUIOptions) {
    this.options = options;

    const root = el("div", "root");

    this.launcher = el("button", "launcher");
    this.launcher.type = "button";
    this.launcher.setAttribute("aria-label", "Open chat");
    this.launcher.textContent = "💬";
    this.launcher.addEventListener("click", () => this.open());

    this.panel = el("div", "panel");
    this.panel.hidden = true;

    const header = el("div", "header");
    const headerTitle = el("span", "header-title", options.businessName ?? "Chat");
    const closeButton = el("button", "close");
    closeButton.type = "button";
    closeButton.setAttribute("aria-label", "Close chat");
    closeButton.textContent = "×";
    closeButton.addEventListener("click", () => this.close());
    header.append(headerTitle, closeButton);

    this.messagesEl = el("div", "messages");
    this.messagesEl.setAttribute("role", "log");

    this.typingEl = el("div", "typing", "…");
    this.typingEl.hidden = true;

    this.errorTextEl = el("span");
    this.retryButton = el("button", "retry", "Retry");
    this.retryButton.type = "button";
    this.retryButton.addEventListener("click", () => this.options.onRetry());
    this.errorEl = el("div", "error");
    this.errorEl.hidden = true;
    this.errorEl.append(this.errorTextEl, this.retryButton);

    const composer = el("div", "composer");
    this.input = el("input", "input");
    this.input.type = "text";
    this.input.placeholder = "Type a message…";
    this.input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        this.handleSend();
      }
    });
    this.sendButton = el("button", "send", "Send");
    this.sendButton.type = "button";
    this.sendButton.addEventListener("click", () => this.handleSend());
    composer.append(this.input, this.sendButton);

    this.panel.append(header, this.messagesEl, this.typingEl, this.errorEl, composer);
    root.append(this.launcher, this.panel);
    container.appendChild(root);
  }

  private handleSend(): void {
    const value = this.input.value.trim();
    if (value.length === 0) return;
    this.input.value = "";
    this.options.onSend(value);
  }

  open(): void {
    this.panel.hidden = false;
  }

  close(): void {
    this.panel.hidden = true;
  }

  get isOpen(): boolean {
    return !this.panel.hidden;
  }

  renderMessages(messages: WidgetMessage[]): void {
    this.messagesEl.replaceChildren();
    for (const message of messages) {
      this.messagesEl.appendChild(this.renderMessageNode(message));
    }
    this.scrollToBottom();
  }

  appendMessage(message: WidgetMessage): void {
    this.messagesEl.appendChild(this.renderMessageNode(message));
    this.scrollToBottom();
  }

  /**
   * Re-renders an already-appended message in place (matched by `id`) —
   * used to move a customer message through pending → sent/failed without
   * ever creating a second DOM node for the same logical message. Falls
   * back to appending if the id isn't found (defensive; shouldn't happen).
   */
  updateMessage(message: WidgetMessage): void {
    const existing = Array.from(this.messagesEl.children).find(
      (child): child is HTMLDivElement => child instanceof HTMLDivElement && child.dataset.messageId === message.id,
    );
    if (existing) {
      this.applyMessageState(existing, message);
    } else {
      this.appendMessage(message);
    }
  }

  private renderMessageNode(message: WidgetMessage): HTMLDivElement {
    const node = el("div");
    node.dataset.messageId = message.id;
    this.applyMessageState(node, message);
    return node;
  }

  /** Status only ever applies to customer messages; `sent`/undefined render identically to today's plain bubble. */
  private applyMessageState(node: HTMLDivElement, message: WidgetMessage): void {
    const statusClass = message.status && message.status !== "sent" ? ` message-${message.status}` : "";
    node.className = `message message-${message.role}${statusClass}`;
    node.textContent = message.content;
  }

  private scrollToBottom(): void {
    this.messagesEl.scrollTop = this.messagesEl.scrollHeight;
  }

  setSending(sending: boolean): void {
    this.input.disabled = sending;
    this.sendButton.disabled = sending;
    this.typingEl.hidden = !sending;
  }

  showError(message: string, opts: { retryable?: boolean } = {}): void {
    this.errorTextEl.textContent = message;
    this.retryButton.hidden = !opts.retryable;
    this.errorEl.hidden = false;
  }

  clearError(): void {
    this.errorEl.hidden = true;
  }
}
