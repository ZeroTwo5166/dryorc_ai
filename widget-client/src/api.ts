// Thin wrapper over the widget endpoints: POST /api/widget/init,
// POST /api/widget/messages (both unmodified by Step 6C), and
// GET /api/widget/messages (new — the Step 6C polling endpoint). Still no
// general history-fetch endpoint — pollMessages exists only to deliver
// what arrives after a known point, per its own doc comment below.

export interface InitResult {
  visitorSessionId: string;
  token: string;
}

export interface MessageResult {
  id: string;
  createdAt: string;
  aiReply: string | null;
  /** The Ai message's own id/timestamp — distinct from `id`/`createdAt` above, which are the customer message's. Null together with aiReply. */
  aiReplyId: string | null;
  aiReplyCreatedAt: string | null;
}

export interface PolledMessage {
  id: string;
  role: string;
  content: string;
  createdAt: string;
}

export interface PollResult {
  messages: PolledMessage[];
  nextCursor: { createdAt: string; id: string } | null;
}

/** A non-2xx response from the API — carries the real HTTP status so callers can branch on 401/403/429/5xx. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** The request never reached the server at all (offline, DNS failure, CORS rejection, etc.). */
export class NetworkError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "NetworkError";
  }
}

async function postJson<T>(url: string, body: unknown, token?: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify(body),
    });
  } catch {
    throw new NetworkError("Could not reach the chat service.");
  }

  if (!response.ok) {
    throw new ApiError(response.status, `Request failed with status ${response.status}`);
  }

  try {
    return (await response.json()) as T;
  } catch {
    throw new ApiError(response.status, "Received an unreadable response from the chat service.");
  }
}

async function getJson<T>(url: string, token: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, {
      method: "GET",
      headers: { Authorization: `Bearer ${token}` },
    });
  } catch {
    throw new NetworkError("Could not reach the chat service.");
  }

  if (!response.ok) {
    throw new ApiError(response.status, `Request failed with status ${response.status}`);
  }

  try {
    return (await response.json()) as T;
  } catch {
    throw new ApiError(response.status, "Received an unreadable response from the chat service.");
  }
}

export async function initSession(
  apiBaseUrl: string,
  publicKey: string,
  visitorSessionId?: string,
): Promise<InitResult> {
  const body = await postJson<{ visitorSessionId: string; token: string }>(
    `${apiBaseUrl}/api/widget/init`,
    { publicKey, visitorSessionId: visitorSessionId ?? null },
  );
  return { visitorSessionId: body.visitorSessionId, token: body.token };
}

export async function sendMessage(
  apiBaseUrl: string,
  token: string,
  content: string,
): Promise<MessageResult> {
  const body = await postJson<{
    id: string;
    createdAt: string;
    aiReply: string | null;
    aiReplyId: string | null;
    aiReplyCreatedAt: string | null;
  }>(`${apiBaseUrl}/api/widget/messages`, { content }, token);
  return {
    id: body.id,
    createdAt: body.createdAt,
    aiReply: body.aiReply,
    aiReplyId: body.aiReplyId,
    aiReplyCreatedAt: body.aiReplyCreatedAt,
  };
}

/**
 * Step 6C. Omitting `cursor` deliberately does NOT fetch conversation
 * history — the server anchors at its own current time and returns
 * nothing pre-existing (see WidgetController.PollMessages' own doc
 * comment) — only messages that arrive after this call returns will show
 * up on a subsequent poll using the returned nextCursor.
 */
export async function pollMessages(
  apiBaseUrl: string,
  token: string,
  cursor?: { createdAt: string; id: string },
): Promise<PollResult> {
  const query = cursor
    ? `?afterCreatedAt=${encodeURIComponent(cursor.createdAt)}&afterId=${encodeURIComponent(cursor.id)}`
    : "";
  const body = await getJson<{
    messages: PolledMessage[];
    nextCursor: { createdAt: string; id: string } | null;
  }>(`${apiBaseUrl}/api/widget/messages${query}`, token);
  return { messages: body.messages, nextCursor: body.nextCursor };
}
