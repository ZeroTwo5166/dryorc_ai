import { initSession, sendMessage, ApiError, NetworkError } from "./api";
import { loadState, saveState, type WidgetMessage } from "./state";
import { WidgetUI, isValidPublicKey } from "./ui";
import stylesCss from "./styles.css";

/**
 * document.currentScript is null once execution resumes after an async
 * script finishes loading in some browsers — falling back to matching this
 * bundle's own known filename covers that case without relying on any
 * particular script-tag placement or id.
 */
function findOwnScript(): HTMLScriptElement | null {
  if (document.currentScript instanceof HTMLScriptElement) {
    return document.currentScript;
  }
  const scripts = Array.from(document.querySelectorAll<HTMLScriptElement>("script[src]"));
  return scripts.find((s) => s.src.includes("nivora-widget.js")) ?? null;
}

function deriveApiBaseUrl(scriptSrc: string): string {
  const url = new URL(scriptSrc, window.location.href);
  return `${url.protocol}//${url.host}`;
}

function describeError(err: unknown): string {
  if (err instanceof NetworkError) {
    return "Couldn't reach the chat service. Check your connection and try again.";
  }
  if (err instanceof ApiError) {
    switch (err.status) {
      case 400:
        return "That message couldn't be sent. Please try again.";
      case 401:
        // Only ever surfaced after the single silent re-init-and-retry in
        // handleSend/retrySend has already run and failed — by the time a
        // caller sees this, reconnecting has already been tried and didn't
        // work, so the copy must say that, not imply one is in progress.
        return "Your session expired and couldn't be renewed. Tap Retry to send your message again.";
      case 403:
        return "This chat isn't available on this site.";
      case 429:
        return "You're sending messages too quickly. Please wait a moment.";
      default:
        return err.status >= 500
          ? "The chat service is temporarily unavailable. Please try again shortly."
          : "Something went wrong. Please try again.";
    }
  }
  return "Something went wrong. Please try again.";
}

async function bootstrap(): Promise<void> {
  const script = findOwnScript();
  if (!script) {
    console.error("[Nivora Widget] could not locate its own <script> element; aborting.");
    return;
  }

  const rawPublicKey = script.getAttribute("data-public-key");
  if (!isValidPublicKey(rawPublicKey)) {
    console.error("[Nivora Widget] missing required data-public-key attribute; aborting.");
    return;
  }
  // Reassigned to its own binding (rather than relying on narrowing of
  // rawPublicKey surviving into the nested functions below) so every
  // later use is unambiguously `string`, not `string | null`.
  const publicKey: string = rawPublicKey;

  const businessName = script.getAttribute("data-business-name") ?? undefined;
  const apiBaseUrl = deriveApiBaseUrl(script.src);

  const host = document.createElement("div");
  host.id = "nivora-widget-host";
  // Shadow DOM only isolates the host element's *descendants* — the host
  // element itself still participates in the page's normal layout, so
  // fixed positioning has to live here, in the light DOM, not in the
  // shadow-scoped stylesheet.
  host.style.cssText = "all: initial; position: fixed; inset: 0; pointer-events: none; z-index: 2147483647;";
  document.body.appendChild(host);
  const shadowRoot = host.attachShadow({ mode: "open" });

  const styleEl = document.createElement("style");
  styleEl.textContent = stylesCss;
  shadowRoot.appendChild(styleEl);

  // Re-enable pointer events only inside the shadow tree — the host div
  // itself spans the whole viewport (inset: 0) purely so its fixed-position
  // descendants have an unclipped stacking context, and must stay
  // click-through everywhere else so it never blocks the host page.
  const wrapper = document.createElement("div");
  wrapper.style.pointerEvents = "auto";
  shadowRoot.appendChild(wrapper);

  const cached = loadState(publicKey);
  const transcript: WidgetMessage[] = cached?.transcript ?? [];
  let visitorSessionId = cached?.visitorSessionId;
  let token: string | null = null;
  let sending = false;
  // Tracks which customer message the visible Retry affordance currently
  // applies to (null means "no message has failed — Retry just re-inits").
  let failedMessageId: string | null = null;

  function persist(): void {
    saveState(publicKey, {
      visitorSessionId: visitorSessionId ?? "",
      token: token ?? "",
      transcript,
    });
  }

  // Re-associate the Retry affordance with whatever customer message was
  // last left unresolved before this page load. A message still `pending`
  // in a cache written before a reload/tab-close was interrupted mid-flight
  // — there's no live request left to resume it, so leaving it `pending`
  // would show a permanent, un-retryable "sending" bubble, and marking it
  // `sent` would misrepresent delivery; normalizing it to `failed` keeps
  // the pending/sent/failed contract meaningful across a reload. A message
  // already `failed` from before the reload needs no status change, only
  // Retry re-armed for it. Iterating in transcript order and overwriting
  // `failedMessageId` as we go means the *last* unresolved customer message
  // wins — the same "most recent failure" semantics Retry already has
  // during a live session.
  let normalizedPendingOnLoad = false;
  for (const message of transcript) {
    if (message.role !== "customer") continue;
    if (message.status === "pending") {
      message.status = "failed";
      failedMessageId = message.id;
      normalizedPendingOnLoad = true;
    } else if (message.status === "failed") {
      failedMessageId = message.id;
    }
  }
  if (normalizedPendingOnLoad) {
    persist();
  }

  const ui = new WidgetUI(wrapper, {
    businessName,
    onSend: (content) => void handleSend(content),
    onRetry: () => {
      if (sending) return;
      if (failedMessageId) {
        void retrySend(failedMessageId);
      } else {
        void doInit();
      }
    },
  });

  if (transcript.length > 0) {
    ui.renderMessages(transcript);
  }
  if (failedMessageId) {
    ui.showError("Your previous message wasn't sent. Tap Retry to send it again.", { retryable: true });
  }

  async function doInit(): Promise<void> {
    try {
      const result = await initSession(apiBaseUrl, publicKey, visitorSessionId);
      token = result.token;

      if (visitorSessionId && result.visitorSessionId !== visitorSessionId) {
        // The server didn't resume the cached conversation (e.g. it's no
        // longer Open) — start fresh rather than showing a transcript that
        // no longer corresponds to anything server-side.
        transcript.length = 0;
        ui.renderMessages(transcript);
      }

      visitorSessionId = result.visitorSessionId;
      persist();
      // Getting a fresh token doesn't mean a still-unsent message got
      // delivered — leave that message's own failed banner/Retry visible
      // until the user actually resends it (or a different message
      // succeeds and reassigns failedMessageId).
      if (!failedMessageId) {
        ui.clearError();
      }
    } catch (err) {
      token = null;
      ui.showError(describeError(err), { retryable: true });
    }
  }

  /**
   * The single submission path for a customer message — both a first send
   * (handleSend) and a Retry (retrySend) funnel through here so a message
   * only ever exists once in `transcript`/the DOM and is transitioned
   * pending → sent/failed in place, never duplicated.
   */
  async function submitCustomerMessage(message: WidgetMessage): Promise<void> {
    sending = true;
    ui.setSending(true);
    ui.clearError();

    message.status = "pending";
    ui.updateMessage(message);
    persist();

    try {
      if (!token) {
        await doInit();
      }
      if (!token) {
        throw new Error("widget is not initialized");
      }

      let result;
      try {
        result = await sendMessage(apiBaseUrl, token, message.content);
      } catch (err) {
        // A single silent re-init-and-retry on an expired/invalid token —
        // deliberately never more than once, so a persistently failing
        // backend can't turn into an infinite retry loop.
        if (err instanceof ApiError && err.status === 401) {
          await doInit();
          if (!token) throw err;
          result = await sendMessage(apiBaseUrl, token, message.content);
        } else {
          throw err;
        }
      }

      message.status = "sent";
      ui.updateMessage(message);
      failedMessageId = null;

      if (result.aiReply) {
        const aiMessage: WidgetMessage = {
          id: result.id,
          role: "ai",
          content: result.aiReply,
          createdAt: result.createdAt,
        };
        transcript.push(aiMessage);
        ui.appendMessage(aiMessage);
      }
      persist();
    } catch (err) {
      message.status = "failed";
      ui.updateMessage(message);
      persist();
      failedMessageId = message.id;
      ui.showError(describeError(err), { retryable: true });
    } finally {
      sending = false;
      ui.setSending(false);
    }
  }

  async function handleSend(content: string): Promise<void> {
    if (sending) return;

    const customerMessage: WidgetMessage = {
      id: `local-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      role: "customer",
      content,
      createdAt: new Date().toISOString(),
      status: "pending",
    };
    transcript.push(customerMessage);
    ui.appendMessage(customerMessage);
    persist();

    await submitCustomerMessage(customerMessage);
  }

  async function retrySend(messageId: string): Promise<void> {
    if (sending) return;
    const message = transcript.find((m) => m.id === messageId && m.role === "customer");
    if (!message) return;
    await submitCustomerMessage(message);
  }

  await doInit();
}

bootstrap().catch((err) => {
  console.error("[Nivora Widget] failed to initialize", err);
});
