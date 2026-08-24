import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { loadState, saveState } from "../src/state";

const API_BASE = "https://api.example.com";
const PUBLIC_KEY = "pk_test_index";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function sendResponse(overrides: Partial<{ id: string; createdAt: string; aiReply: string | null; aiReplyId: string | null; aiReplyCreatedAt: string | null }> = {}) {
  return jsonResponse(200, {
    id: "m1",
    createdAt: "2026-01-01T00:00:00Z",
    aiReply: null,
    aiReplyId: null,
    aiReplyCreatedAt: null,
    ...overrides,
  });
}

function emptyPollResponse(): Response {
  return jsonResponse(200, { messages: [], nextCursor: null });
}

function mountScript(): void {
  const script = document.createElement("script");
  script.src = `${API_BASE}/widget/nivora-widget.js`;
  script.setAttribute("data-public-key", PUBLIC_KEY);
  document.body.appendChild(script);
}

/**
 * Dispatches by (method, path) rather than call order, with an independent
 * queue per endpoint. Deliberately not one flat `.mockResolvedValueOnce`
 * chain: Step 6C's poll loop fires its own GET request the instant the
 * panel opens (see index.ts's onOpen wiring), interleaved with whatever
 * POST calls a test cares about in a way that isn't deterministic to hand-
 * index into — a strict single sequential queue would make every existing
 * send/retry test fragile against an unrelated poll call landing at an
 * unpredictable position. GET polls default to an empty result when a test
 * hasn't queued anything for them, matching pollOnce's own "a poll with
 * nothing interesting to say is a normal, silent no-op" behavior.
 */
function scriptedFetch(script: {
  init?: Array<Response>;
  send?: Array<Response>;
  poll?: Array<Response>;
}): ReturnType<typeof vi.fn> {
  const queues = {
    init: [...(script.init ?? [])],
    send: [...(script.send ?? [])],
    poll: [...(script.poll ?? [])],
  };
  return vi.fn(async (url: string, options?: RequestInit) => {
    const method = (options?.method ?? "GET").toUpperCase();
    const isMessages = String(url).includes("/api/widget/messages");
    const isInit = String(url).includes("/api/widget/init");

    if (isInit) {
      return queues.init.shift() ?? jsonResponse(200, { visitorSessionId: "v1", token: "t1" });
    }
    if (isMessages && method === "GET") {
      return queues.poll.shift() ?? emptyPollResponse();
    }
    if (isMessages && method === "POST") {
      const next = queues.send.shift();
      if (!next) throw new Error("scriptedFetch: unexpected extra POST /messages call — no queued response left");
      return next;
    }
    throw new Error(`scriptedFetch: unhandled request ${method} ${url}`);
  });
}

/** Everything here runs against already-resolved mock Response objects (no real network/timers), so a bounded number of microtask ticks reliably drains the whole init/send/retry promise chain, including the internal 401 re-init-and-resend hop. */
async function flush(): Promise<void> {
  for (let i = 0; i < 25; i++) {
    await Promise.resolve();
  }
  await new Promise((r) => setTimeout(r, 0));
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

function shadow(): ShadowRoot {
  const host = document.getElementById("nivora-widget-host");
  if (!host?.shadowRoot) throw new Error("widget host/shadow root not found");
  return host.shadowRoot;
}

function openPanel(): void {
  shadow().querySelector<HTMLButtonElement>(".launcher")!.click();
}

function closePanel(): void {
  shadow().querySelector<HTMLButtonElement>(".close")!.click();
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

function agentNodes(): HTMLDivElement[] {
  return Array.from(shadow().querySelectorAll<HTMLDivElement>(".message-agent"));
}

function aiNodes(): HTMLDivElement[] {
  return Array.from(shadow().querySelectorAll<HTMLDivElement>(".message-ai"));
}

function errorText(): string | null {
  const el = shadow().querySelector<HTMLElement>(".error");
  return el && !el.hidden ? el.textContent : null;
}

function retryVisible(): boolean {
  const btn = shadow().querySelector<HTMLButtonElement>(".retry");
  return !!btn && !btn.hidden && !shadow().querySelector<HTMLElement>(".error")!.hidden;
}

function callsTo(fetchMock: ReturnType<typeof vi.fn>, method: string, pathIncludes: string): unknown[][] {
  return fetchMock.mock.calls.filter(([url, options]) => {
    const m = ((options as RequestInit | undefined)?.method ?? "GET").toUpperCase();
    return m === method.toUpperCase() && String(url).includes(pathIncludes);
  });
}

describe("index (widget orchestration: send / retry / message state machine)", () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    document.body.innerHTML = "";
    window.localStorage.clear();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it("initial send renders the customer message as pending, then transitions to sent on success", async () => {
    fetchMock = scriptedFetch({ send: [sendResponse({ aiReply: "hi there" })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

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
    fetchMock = scriptedFetch({ send: [new Response("", { status: 500 })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

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
    fetchMock = scriptedFetch({ send: [new Response("", { status: 500 })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("will fail");

    expect(errorText()).toBeTruthy();
    expect(retryVisible()).toBe(true);
  });

  it("persists the failed message's status to localStorage", async () => {
    fetchMock = scriptedFetch({ send: [new Response("", { status: 500 })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("persisted failure");

    const saved = loadState(PUBLIC_KEY);
    expect(saved?.transcript).toHaveLength(1);
    expect(saved?.transcript[0]).toMatchObject({ content: "persisted failure", status: "failed" });
  });

  it("Retry resends the original failed message content and transitions it to sent without creating a duplicate", async () => {
    fetchMock = scriptedFetch({
      send: [new Response("", { status: 500 }), sendResponse({ aiReply: "recovered!" })],
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

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
    const sendCalls = callsTo(fetchMock, "POST", "/api/widget/messages");
    expect(sendCalls).toHaveLength(2);
    const retryBody = JSON.parse((sendCalls[1][1] as RequestInit).body as string);
    expect(retryBody.content).toBe("resend me");
  });

  it("a failed Retry returns the message to failed, preserves content, and keeps Retry available", async () => {
    fetchMock = scriptedFetch({
      send: [new Response("", { status: 500 }), new Response("", { status: 500 })],
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

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
    const slowRetry = new Promise<Response>((resolve) => {
      resolveRetry = resolve;
    });
    fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
      const method = (options?.method ?? "GET").toUpperCase();
      if (String(url).includes("/api/widget/init")) return jsonResponse(200, { visitorSessionId: "v1", token: "t1" });
      if (String(url).includes("/api/widget/messages") && method === "GET") return emptyPollResponse();
      // First POST /messages fails, every subsequent one is the slow in-flight retry.
      const messagePostCalls = fetchMock.mock.calls.filter(
        ([u, o]) =>
          ((o as RequestInit | undefined)?.method ?? "GET").toUpperCase() === "POST" &&
          String(u).includes("/api/widget/messages"),
      );
      return messagePostCalls.length <= 1 ? new Response("", { status: 500 }) : slowRetry;
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

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
    resolveRetry(sendResponse());
    await flush();

    expect(callsTo(fetchMock, "POST", "/api/widget/messages")).toHaveLength(2); // original fail + exactly one retry
    expect(customerNodes()).toHaveLength(1);
  });

  it("double-clicking Send on the very first submission results in only one backend message POST", async () => {
    fetchMock = scriptedFetch({ send: [sendResponse()] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();

    const input = shadow().querySelector<HTMLInputElement>(".input")!;
    const sendButton = shadow().querySelector<HTMLButtonElement>(".send")!;
    input.value = "double click me";
    sendButton.click();
    input.value = "double click me";
    sendButton.click();
    await flush();

    expect(callsTo(fetchMock, "POST", "/api/widget/init")).toHaveLength(1);
    expect(callsTo(fetchMock, "POST", "/api/widget/messages")).toHaveLength(1);
    expect(customerNodes()).toHaveLength(1);
  });

  it("a failed message survives reload as failed with its original content, and remains retryable", async () => {
    fetchMock = scriptedFetch({ send: [new Response("", { status: 500 })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("reload me");

    // Simulate a reload: fresh module + fresh DOM, same localStorage.
    document.body.innerHTML = "";
    fetchMock = scriptedFetch({ init: [jsonResponse(200, { visitorSessionId: "v1", token: "t2" })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    await loadWidget();

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("reload me");
    expect(msg.className).toContain("message-failed");
    expect(retryVisible()).toBe(true);

    const saved = loadState(PUBLIC_KEY);
    expect(saved?.transcript[0]).toMatchObject({ content: "reload me", status: "failed" });
  });

  it("a pending message interrupted before reload (never resolved) is normalized to failed, not left stuck pending", async () => {
    fetchMock = scriptedFetch({});
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    await loadWidget();

    // Simulate a tab close mid-send: write a still-`pending` cached message
    // directly, bypassing the running widget instance entirely.
    saveState(PUBLIC_KEY, {
      visitorSessionId: "v1",
      token: "t1",
      transcript: [{ id: "local-1", role: "customer", content: "interrupted", createdAt: "2026-01-01T00:00:00Z", status: "pending" }],
    });

    document.body.innerHTML = "";
    fetchMock = scriptedFetch({ init: [jsonResponse(200, { visitorSessionId: "v1", token: "t2" })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    await loadWidget();

    const [msg] = customerNodes();
    expect(msg.textContent).toBe("interrupted");
    expect(msg.className).not.toContain("message-pending");
    expect(msg.className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });

  it("existing 401 re-init-and-resend recovery still succeeds silently on a single expired token", async () => {
    fetchMock = vi.fn();
    let initCall = 0;
    fetchMock.mockImplementation(async (url: string, options?: RequestInit) => {
      const method = (options?.method ?? "GET").toUpperCase();
      if (String(url).includes("/api/widget/init")) {
        initCall++;
        return jsonResponse(200, { visitorSessionId: "v1", token: initCall === 1 ? "stale-token" : "fresh-token" });
      }
      if (String(url).includes("/api/widget/messages") && method === "GET") return emptyPollResponse();
      // First POST /messages (with the stale token) is rejected; the resend after silent re-init succeeds.
      const postCalls = fetchMock.mock.calls.filter(
        ([, o]) => ((o as RequestInit | undefined)?.method ?? "GET").toUpperCase() === "POST" && String(url).includes("/messages"),
      );
      return postCalls.length <= 1 ? new Response("", { status: 401 }) : sendResponse({ aiReply: "ok" });
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("token will expire");

    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].className).not.toContain("message-failed");
    expect(errorText()).toBeNull();
  });

  it("uses accurate, non-'reconnecting' copy once the 401 recovery path is fully exhausted", async () => {
    fetchMock = vi.fn();
    let initCall = 0;
    fetchMock.mockImplementation(async (url: string, options?: RequestInit) => {
      const method = (options?.method ?? "GET").toUpperCase();
      if (String(url).includes("/api/widget/init")) {
        initCall++;
        if (initCall === 1) return jsonResponse(200, { visitorSessionId: "v1", token: "stale-token" });
        return new Response("", { status: 500 }); // re-init itself also fails
      }
      if (String(url).includes("/api/widget/messages") && method === "GET") return emptyPollResponse();
      return new Response("", { status: 401 }); // send rejected
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("doomed");

    const text = errorText();
    expect(text).toBeTruthy();
    expect(text!.toLowerCase()).not.toContain("reconnecting");
    expect(customerNodes()[0].className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });

  // ── Step 6C: customer-side delivery via polling ───────────────────

  it("polling starts when the widget opens and issues a GET to /api/widget/messages", async () => {
    fetchMock = scriptedFetch({});
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    expect(callsTo(fetchMock, "GET", "/api/widget/messages")).toHaveLength(0); // not yet — panel isn't open
    openPanel();
    await flush();

    expect(callsTo(fetchMock, "GET", "/api/widget/messages").length).toBeGreaterThanOrEqual(1);
  });

  it("polling stops when the widget closes (no further poll calls after close)", async () => {
    fetchMock = scriptedFetch({});
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await flush();
    const pollsAtOpen = callsTo(fetchMock, "GET", "/api/widget/messages").length;

    closePanel();
    await flush();
    // setTimeout-based interval loop never fires again once pollingActive is false —
    // simulated here by advancing real microtasks only (no fake timers in this
    // suite), so the assertion is simply "no growth immediately after close."
    expect(callsTo(fetchMock, "GET", "/api/widget/messages").length).toBe(pollsAtOpen);
  });

  it("a polled Agent message is appended and rendered with the agent visual treatment, distinct from ai", async () => {
    fetchMock = scriptedFetch({
      poll: [
        jsonResponse(200, {
          messages: [{ id: "srv-1", role: "Agent", content: "a human replied", createdAt: "2026-01-01T00:00:05Z" }],
          nextCursor: { createdAt: "2026-01-01T00:00:05Z", id: "srv-1" },
        }),
      ],
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await flush();

    expect(agentNodes()).toHaveLength(1);
    expect(agentNodes()[0].textContent).toBe("a human replied");
    expect(aiNodes()).toHaveLength(0);
  });

  it("a polled message already present by id (e.g. delivered synchronously via send) is not duplicated", async () => {
    fetchMock = scriptedFetch({
      send: [sendResponse({ aiReply: "hi", aiReplyId: "ai-1", aiReplyCreatedAt: "2026-01-01T00:00:01Z" })],
      poll: [
        // Same id the send response already delivered synchronously.
        jsonResponse(200, {
          messages: [{ id: "ai-1", role: "Ai", content: "hi", createdAt: "2026-01-01T00:00:01Z" }],
          nextCursor: { createdAt: "2026-01-01T00:00:01Z", id: "ai-1" },
        }),
      ],
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("hello");
    await flush(); // let a poll tick run after the send

    expect(aiNodes()).toHaveLength(1); // not duplicated
  });

  it("a polled AI message that is appended BEFORE the synchronous send response resolves is not duplicated when that later response carries the same id (poll-first / send-second ordering)", async () => {
    const AI_ID = "ai-shared-1";
    const AI_CREATED_AT = "2026-01-01T00:00:01Z";

    // Both the poll's GET and the send's POST are held open under manual
    // control (same deferred-promise technique the "Retry is disabled...
    // in flight" test above already uses) so the ordering below is forced
    // and deterministic, not a hope about scheduler timing.
    let resolvePoll!: (r: Response) => void;
    let resolveSend!: (r: Response) => void;
    const pollPromise = new Promise<Response>((resolve) => {
      resolvePoll = resolve;
    });
    const sendPromise = new Promise<Response>((resolve) => {
      resolveSend = resolve;
    });

    fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
      const method = (options?.method ?? "GET").toUpperCase();
      if (String(url).includes("/api/widget/init")) return jsonResponse(200, { visitorSessionId: "v1", token: "t1" });
      if (String(url).includes("/api/widget/messages") && method === "GET") return pollPromise;
      if (String(url).includes("/api/widget/messages") && method === "POST") return sendPromise;
      throw new Error(`unhandled request ${method} ${url}`);
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel(); // starts polling: the GET is dispatched now and held on pollPromise, unresolved.
    await Promise.resolve();
    await Promise.resolve();

    // Kick off the customer send without awaiting it to completion (its own
    // POST is dispatched and held on sendPromise, unresolved) — mirrors how
    // the existing "Retry is disabled while in flight" test above drives
    // the click directly rather than through the send()/retry() helpers,
    // specifically so nothing here waits on a promise that hasn't resolved.
    const input = shadow().querySelector<HTMLInputElement>(".input")!;
    input.value = "hello";
    shadow().querySelector<HTMLButtonElement>(".send")!.click();
    await Promise.resolve();
    await Promise.resolve();

    expect(callsTo(fetchMock, "GET", "/api/widget/messages").length).toBeGreaterThanOrEqual(1);
    expect(callsTo(fetchMock, "POST", "/api/widget/messages")).toHaveLength(1);

    // Resolve the POLL first: pollOnce() processes it and appends the AI
    // message via the poll path's own dedup-by-id guard.
    resolvePoll(
      jsonResponse(200, {
        messages: [{ id: AI_ID, role: "Ai", content: "hi", createdAt: AI_CREATED_AT }],
        nextCursor: { createdAt: AI_CREATED_AT, id: AI_ID },
      }),
    );
    await flush();

    // Prove the poll-first ordering genuinely happened, not just assumed:
    // the AI message is already present — appended by the poll path — while
    // the send's own POST is still unresolved at this point.
    expect(aiNodes()).toHaveLength(1);
    expect(aiNodes()[0].textContent).toBe("hi");
    expect(aiNodes()[0].dataset.messageId).toBe(AI_ID);

    // Now resolve the SEND response second, carrying the identical AI
    // message id/timestamp the poll already delivered — this is exactly
    // the harder ordering: submitCustomerMessage's own dedup-by-id guard
    // (index.ts, "the reverse race also needs the same guard here") is
    // what must prevent a second append here, not the poll path.
    resolveSend(sendResponse({ aiReply: "hi", aiReplyId: AI_ID, aiReplyCreatedAt: AI_CREATED_AT }));
    await flush();

    // No duplicate: still exactly one AI message, still the real server id,
    // and the customer's own message is unaffected by any of this.
    expect(aiNodes()).toHaveLength(1);
    expect(aiNodes()[0].textContent).toBe("hi");
    expect(aiNodes()[0].dataset.messageId).toBe(AI_ID);
    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].className).not.toContain("message-failed");
  });

  it("a poll failure does not surface a user-facing error and leaves the composer usable", async () => {
    fetchMock = vi.fn(async (url: string, options?: RequestInit) => {
      const method = (options?.method ?? "GET").toUpperCase();
      if (String(url).includes("/api/widget/init")) return jsonResponse(200, { visitorSessionId: "v1", token: "t1" });
      if (String(url).includes("/api/widget/messages") && method === "GET") return new Response("", { status: 500 });
      return sendResponse();
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await flush();

    expect(errorText()).toBeNull();
    expect(shadow().querySelector<HTMLInputElement>(".input")!.disabled).toBe(false);

    // The composer must still work normally afterward.
    await send("still works");
    expect(customerNodes()).toHaveLength(1);
  });

  it("cursor seeds from the newest Ai/Agent message already in a restored localStorage transcript", async () => {
    saveState(PUBLIC_KEY, {
      visitorSessionId: "v1",
      token: "t1",
      transcript: [
        { id: "ai-old", role: "ai", content: "older", createdAt: "2026-01-01T00:00:01Z" },
        { id: "ai-new", role: "ai", content: "newer", createdAt: "2026-01-01T00:00:02Z" },
      ],
    });

    fetchMock = scriptedFetch({});
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await flush();

    const pollCall = callsTo(fetchMock, "GET", "/api/widget/messages")[0];
    const url = new URL(pollCall[0] as string);
    expect(url.searchParams.get("afterId")).toBe("ai-new");
    expect(url.searchParams.get("afterCreatedAt")).toBe("2026-01-01T00:00:02Z");
  });

  it("with no local transcript, the first poll omits the cursor (server establishes 'now')", async () => {
    fetchMock = scriptedFetch({});
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await flush();

    const pollCall = callsTo(fetchMock, "GET", "/api/widget/messages")[0];
    const url = new URL(pollCall[0] as string);
    expect(url.searchParams.has("afterId")).toBe(false);
    expect(url.searchParams.has("afterCreatedAt")).toBe(false);
  });

  it("existing pending/sent/failed/retry behavior is unaffected by polling running alongside it", async () => {
    fetchMock = scriptedFetch({ send: [new Response("", { status: 500 })] });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await loadWidget();
    openPanel();
    await send("still the same state machine");

    expect(customerNodes()).toHaveLength(1);
    expect(customerNodes()[0].className).toContain("message-failed");
    expect(retryVisible()).toBe(true);
  });
});
