import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { loadState } from "../src/state";

const API_BASE = "https://api.example.com";
const PUBLIC_KEY = "pk_test_index";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function mountScript(): void {
  const script = document.createElement("script");
  script.src = `${API_BASE}/widget/nivora-widget.js`;
  script.setAttribute("data-public-key", PUBLIC_KEY);
  document.body.appendChild(script);
}

/**
 * index.ts self-executes bootstrap() on import (it's the actual embedded
 * entrypoint, not a library with an exported init function), so each test
 * needs a fresh module instance — vi.resetModules() plus a fresh dynamic
 * import — to get an isolated closure (transcript/token/sending/etc).
 */
async function loadWidget(): Promise<void> {
  mountScript();
  vi.resetModules();
  await import("../src/index");
  await flush();
}

/** Everything here runs against already-resolved mock Response objects (no real network/timers), so a bounded number of microtask ticks reliably drains the whole init/send/retry promise chain, including the internal 401 re-init-and-resend hop. */
async function flush(): Promise<void> {
  for (let i = 0; i < 25; i++) {
    await Promise.resolve();
  }
  await new Promise((r) => setTimeout(r, 0));
}

function shadow(): ShadowRoot {
  const host = document.getElementById("nivora-widget-host");
  if (!host?.shadowRoot) throw new Error("widget host/shadow root not found");
  return host.shadowRoot;
}

function openPanel(): void {
  shadow().querySelector<HTMLButtonElement>(".launcher")!.click();
}

async function send(content: string): Promise<void> {
  const input = shadow().querySelector<HTMLInputElement>(".input")!;
  input.value = content;
  shadow().querySelector<HTMLButtonElement>(".send")!.click();
  await flush();
}

async function retry(): Promise<void> {
  shadow().querySelector<HTMLButtonElement>(".retry")!.click();
  await flush();
}

function customerNodes(): HTMLDivElement[] {
  return Array.from(shadow().querySelectorAll<HTMLDivElement>(".message-customer"));
}

function errorText(): string | null {
  const el = shadow().querySelector<HTMLElement>(".error");
  return el && !el.hidden ? el.textContent : null;
}

function retryVisible(): boolean {
  const btn = shadow().querySelector<HTMLButtonElement>(".retry");
  return !!btn && !btn.hidden && !shadow().querySelector<HTMLElement>(".error")!.hidden;
}

describe("index (widget orchestration: send / retry / message state machine)", () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    document.body.innerHTML = "";
    window.localStorage.clear();
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it("initial send renders the customer message as pending, then transitions to sent on success", async () => {
    // First call: bootstrap's own initial init. Second: the init triggered
    // by the send itself only if no token yet — bootstrap already got one,
    // so the next call is the message POST.
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: "hi there" }));

    await loadWidget();
    openPanel();
    await send("hello");

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("hello");
    expect(msg.className).toContain("message-customer");
    expect(msg.className).not.toContain("message-pending");
    expect(msg.className).not.toContain("message-failed");
  });

  it("failed send transitions the existing message to a visually distinct failed state and retains its content", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }));

    await loadWidget();
    openPanel();
    await send("will fail");

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("will fail");
    expect(msg.className).toContain("message-failed");
    expect(msg.className).not.toContain("message-pending");
    expect(customerNodes()).toHaveLength(1);
  });

  it("shows a visible error and a Retry affordance after a failed send", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }));

    await loadWidget();
    openPanel();
    await send("will fail");

    expect(errorText()).toBeTruthy();
    expect(retryVisible()).toBe(true);
  });

  it("persists the failed message's status to localStorage", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }));

    await loadWidget();
    openPanel();
    await send("persisted failure");

    const saved = loadState(PUBLIC_KEY);
    expect(saved?.transcript).toHaveLength(1);
    expect(saved?.transcript[0]).toMatchObject({ content: "persisted failure", status: "failed" });
  });

  it("Retry resends the original failed message content and transitions it to sent without creating a duplicate", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" })) // bootstrap init
      .mockResolvedValueOnce(new Response("", { status: 500 })) // first send fails
      .mockResolvedValueOnce(
        jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: "recovered!" }),
      ); // retry succeeds

    await loadWidget();
    openPanel();
    await send("resend me");
    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].className).toContain("message-failed");

    await retry();

    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].textContent).toBe("resend me");
    expect(customerNodes()[0].className).not.toContain("message-failed");
    expect(customerNodes()[0].className).not.toContain("message-pending");

    // The retried request must have carried the same original content.
    const retryCall = fetchMock.mock.calls[2];
    const body = JSON.parse(retryCall[1].body as string);
    expect(body.content).toBe("resend me");
  });

  it("a failed Retry returns the message to failed, preserves content, and keeps Retry available", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }))
      .mockResolvedValueOnce(new Response("", { status: 500 }));

    await loadWidget();
    openPanel();
    await send("still failing");
    await retry();

    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].textContent).toBe("still failing");
    expect(customerNodes()[0].className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });

  it("Retry is disabled (hidden with its error banner) while a retry request is in flight, and cannot double-submit", async () => {
    let resolveRetry!: (r: Response) => void;
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }))
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            resolveRetry = resolve;
          }),
      );

    await loadWidget();
    openPanel();
    await send("slow retry");

    const retryButton = shadow().querySelector<HTMLButtonElement>(".retry")!;
    retryButton.click();
    await Promise.resolve();
    await Promise.resolve();

    // In-flight: error/retry banner hidden (can't be clicked again), input disabled.
    expect(shadow().querySelector<HTMLElement>(".error")!.hidden).toBe(true);
    expect(shadow().querySelector<HTMLInputElement>(".input")!.disabled).toBe(true);

    // A second click while in flight must not fire a second backend submission.
    retryButton.click();
    resolveRetry(jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: null }));
    await flush();

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(customerNodes()).toHaveLength(1);
  });

  it("double-clicking Send on the very first submission results in only one backend message POST", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: null }));

    await loadWidget();
    openPanel();

    const input = shadow().querySelector<HTMLInputElement>(".input")!;
    const sendButton = shadow().querySelector<HTMLButtonElement>(".send")!;
    input.value = "double click me";
    sendButton.click();
    input.value = "double click me";
    sendButton.click();
    await flush();

    // init + exactly one message POST, never two.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(customerNodes()).toHaveLength(1);
  });

  it("a failed message survives reload as failed with its original content, and remains retryable", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }))
      .mockResolvedValueOnce(new Response("", { status: 500 }));

    await loadWidget();
    openPanel();
    await send("reload me");

    // Simulate a reload: fresh module + fresh DOM, same localStorage.
    document.body.innerHTML = "";
    fetchMock.mockReset();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t2" }));
    await loadWidget();

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("reload me");
    expect(msg.className).toContain("message-failed");
    expect(retryVisible()).toBe(true);

    const saved = loadState(PUBLIC_KEY);
    expect(saved?.transcript[0]).toMatchObject({ content: "reload me", status: "failed" });
  });

  it("a pending message interrupted before reload (never resolved) is normalized to failed, not left stuck pending", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }));
    await loadWidget();

    // Simulate a tab close mid-send: write a still-`pending` cached message
    // directly, bypassing the running widget instance entirely.
    const { saveState } = await import("../src/state");
    saveState(PUBLIC_KEY, {
      visitorSessionId: "v1",
      token: "t1",
      transcript: [{ id: "local-1", role: "customer", content: "interrupted", createdAt: "2026-01-01T00:00:00Z", status: "pending" }],
    });

    document.body.innerHTML = "";
    fetchMock.mockReset();
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "t2" }));
    await loadWidget();

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("interrupted");
    expect(msg.className).not.toContain("message-pending");
    expect(msg.className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });

  it("existing 401 re-init-and-resend recovery still succeeds silently on a single expired token", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "stale-token" })) // bootstrap init
      .mockResolvedValueOnce(new Response("", { status: 401 })) // send rejected: expired
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "fresh-token" })) // silent re-init
      .mockResolvedValueOnce(jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: "ok" })); // resend succeeds

    await loadWidget();
    openPanel();
    await send("token will expire");

    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].className).not.toContain("message-failed");
    expect(errorText()).toBeNull();
  });

  it("uses accurate, non-'reconnecting' copy once the 401 recovery path is fully exhausted", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse(200, { visitorSessionId: "v1", token: "stale-token" }))
      .mockResolvedValueOnce(new Response("", { status: 401 })) // send rejected
      .mockResolvedValueOnce(new Response("", { status: 500 })); // re-init itself also fails

    await loadWidget();
    openPanel();
    await send("doomed");

    const text = errorText();
    expect(text).toBeTruthy();
    expect(text!.toLowerCase()).not.toContain("reconnecting");
    expect(customerNodes()[0].className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });
});
