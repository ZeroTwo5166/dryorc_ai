// Thin wrapper over the two existing, unmodified widget endpoints
// (POST /api/widget/init, POST /api/widget/messages). No history endpoint
// exists and none is added here — see state.ts for why that's not needed
// for the approved Step 6B scope.

export interface InitResult {
  visitorSessionId: string;
  token: string;
}

export interface MessageResult {
  id: string;
  createdAt: string;
  aiReply: string | null;
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
  const body = await postJson<{ id: string; createdAt: string; aiReply: string | null }>(
    `${apiBaseUrl}/api/widget/messages`,
    { content },
    token,
  );
  return { id: body.id, createdAt: body.createdAt, aiReply: body.aiReply };
}
