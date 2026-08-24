// Local persistence for the anonymous visitor's session — deliberately
// localStorage, not sessionStorage or a cookie: it must survive a full page
// reload/new-tab on the same site, scoped to the host page's own origin
// (never shared across unrelated sites), and the widget never sends
// credentials, so a cookie brings no benefit here. No server-side history
// endpoint exists (by design — see the Step 6B design report); this module
// is the only source of conversation continuity across a reload.

export type MessageRole = "customer" | "ai" | "agent";

// Only customer messages carry a status — a message the widget itself
// created optimistically before the server confirmed it. AI messages only
// ever exist in the transcript because a send already succeeded, so they
// never need one (left undefined).
export type MessageStatus = "pending" | "sent" | "failed";

export interface WidgetMessage {
  id: string;
  role: MessageRole;
  content: string;
  createdAt: string;
  status?: MessageStatus;
}

/** The last (CreatedAt, Id) position a poll response established — see api.ts's pollMessages. */
export interface PollCursor {
  createdAt: string;
  id: string;
}

export interface WidgetState {
  visitorSessionId: string;
  token: string;
  transcript: WidgetMessage[];
  /**
   * Absent for a visitor who has never successfully polled yet (a brand
   * new session, or one restored from a cache written before this field
   * existed) — index.ts falls back to deriving a starting point from the
   * newest Ai/Agent message already in the transcript, or if there is
   * none, lets the server establish a fresh "now" anchor on the first
   * poll. Never used to reconstruct full history — see index.ts's own
   * doc comment on why a cursor-less poll deliberately returns nothing
   * pre-existing.
   */
  lastPolledCursor?: PollCursor;
}

const STORAGE_PREFIX = "nivora_widget_";

function storageKey(publicKey: string): string {
  return `${STORAGE_PREFIX}${publicKey}`;
}

function isWidgetMessage(value: unknown): value is WidgetMessage {
  if (typeof value !== "object" || value === null) return false;
  const m = value as Record<string, unknown>;
  const validStatus =
    m.status === undefined || m.status === "pending" || m.status === "sent" || m.status === "failed";
  return (
    typeof m.id === "string" &&
    (m.role === "customer" || m.role === "ai" || m.role === "agent") &&
    typeof m.content === "string" &&
    typeof m.createdAt === "string" &&
    validStatus
  );
}

function isPollCursor(value: unknown): value is PollCursor {
  if (typeof value !== "object" || value === null) return false;
  const c = value as Record<string, unknown>;
  return typeof c.createdAt === "string" && typeof c.id === "string";
}

/**
 * Reads and validates the cached session for this widget's PublicKey.
 * Returns null for "no cached session" and for any cache that doesn't
 * parse as a WidgetState — a corrupted/foreign localStorage value must
 * never crash the widget, only make it behave as a first-time visitor.
 * Malformed transcript entries are dropped individually rather than
 * invalidating the whole cached session over one bad row.
 */
export function loadState(publicKey: string): WidgetState | null {
  let raw: string | null;
  try {
    raw = window.localStorage.getItem(storageKey(publicKey));
  } catch {
    return null;
  }
  if (!raw) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null) return null;
  const candidate = parsed as Record<string, unknown>;
  if (typeof candidate.visitorSessionId !== "string" || typeof candidate.token !== "string") {
    return null;
  }

  const transcript = Array.isArray(candidate.transcript)
    ? candidate.transcript.filter(isWidgetMessage)
    : [];

  // A malformed lastPolledCursor is dropped, not treated as invalidating
  // the whole cached session — same "one bad row doesn't break the cache"
  // policy as transcript entries above. index.ts's own fallback (derive
  // from the transcript, or let the server establish a fresh anchor)
  // covers the resulting "absent" case safely.
  const lastPolledCursor = isPollCursor(candidate.lastPolledCursor) ? candidate.lastPolledCursor : undefined;

  return { visitorSessionId: candidate.visitorSessionId, token: candidate.token, transcript, lastPolledCursor };
}

/**
 * Best-effort persistence — storage can fail (private browsing, quota
 * exceeded, disabled entirely) without that ever being allowed to break
 * the live chat for this page view; it would just not survive a reload.
 */
export function saveState(publicKey: string, state: WidgetState): void {
  try {
    window.localStorage.setItem(storageKey(publicKey), JSON.stringify(state));
  } catch {
    // Deliberately swallowed — see doc comment above.
  }
}

export function clearState(publicKey: string): void {
  try {
    window.localStorage.removeItem(storageKey(publicKey));
  } catch {
    // Deliberately swallowed — same reasoning as saveState.
  }
}
