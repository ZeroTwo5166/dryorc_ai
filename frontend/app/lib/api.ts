// Talks to backend/Controllers/AuthController.cs. Cookie-based auth, so every
// call must send credentials — and, per the schemeful-same-site gotcha noted
// in the repo, the backend must be running its "http" launch profile
// (http://localhost:5293) to stay same-site with this app's http://localhost:3000
// in dev. Override with NEXT_PUBLIC_API_URL if that ever changes.
const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5293";

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

// Backend error bodies aren't always plain text: AuthController.Register
// returns a raw JSON array of IdentityResult error strings (e.g.
// `["Passwords must have at least one non alphanumeric character."]`), and
// [ApiController]'s built-in model-validation 400s return an ASP.NET
// ProblemDetails object (`{ errors: { Field: ["msg", ...] } }`). Parse both
// shapes into a plain, human-readable sentence instead of showing the raw body.
function parseErrorBody(raw: string): string {
  if (!raw) return "";

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return raw; // plain text body (e.g. BadRequest("some message")) — use as-is
  }

  if (Array.isArray(parsed)) {
    return parsed.filter((v) => typeof v === "string").join(" ");
  }
  if (typeof parsed === "string") {
    return parsed;
  }
  if (parsed && typeof parsed === "object") {
    const obj = parsed as { errors?: Record<string, unknown>; title?: string };
    if (obj.errors && typeof obj.errors === "object") {
      return Object.values(obj.errors)
        .flat()
        .filter((v): v is string => typeof v === "string")
        .join(" ");
    }
    if (typeof obj.title === "string") {
      return obj.title;
    }
  }
  return raw;
}

async function apiFetch(path: string, init?: RequestInit) {
  const res = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    credentials: "include",
    // Auth-check responses (/me in particular) must never be served from the
    // browser's HTTP cache — the backend sends no Cache-Control on it either,
    // so this closes that gap defensively rather than relying on that holding.
    cache: "no-store",
    headers: {
      "Content-Type": "application/json",
      ...init?.headers,
    },
  });

  if (!res.ok) {
    // No special-casing for 401 here, deliberately — this function is the
    // shared primitive for both anonymous endpoints (login, register, ...)
    // and, via api.get/api.post below, every authenticated one too, and a
    // 401 means something different on each: "wrong password" on login,
    // but "your session died" everywhere else. Login's own 401 message is
    // set in login() below, where that distinction is actually knowable.
    const message =
      res.status === 409
        ? "An account with this email already exists."
        : parseErrorBody(await res.text().catch(() => "")) || `Request failed (${res.status}).`;
    throw new ApiError(res.status, message);
  }

  if (res.status === 204) return null;
  return res.json();
}

// Centralized 401 handling — registered once by AuthProvider (auth-context.tsx),
// which is the only place with both the auth state to clear and a router to
// redirect with. api.ts itself has neither, so it just calls whatever's
// registered; nothing here does navigation or state directly.
//
// Deliberately global-callback-based rather than e.g. a React context: this
// needs to be callable from apiFetch, a plain function with no component
// tree to consume a context from, and reachable from api.get/api.post calls
// made anywhere (inside or outside a component).
type UnauthorizedHandler = () => void;
let unauthorizedHandler: UnauthorizedHandler | null = null;

export function setUnauthorizedHandler(handler: UnauthorizedHandler | null) {
  unauthorizedHandler = handler;
}

// Wraps apiFetch for endpoints that require an existing session — every
// authenticated endpoint, present and future, should go through this (via
// the `api` object below) instead of calling apiFetch directly. A 401 here
// always means the same thing: the session that got the caller into this
// authenticated code path in the first place is no longer valid — so it's
// handled once, centrally, instead of every call site needing its own
// `if (status === 401)` branch.
//
// /api/auth/me is the one deliberate exception, staying on plain apiFetch
// (see auth-context.tsx's fetchMe) — it's the *discovery* endpoint, callable
// from public marketing pages where "not logged in" is an expected, normal
// 401, not a session-died event, so it must never trigger this redirect.
//
// Never retries and never calls another authenticated endpoint itself —
// both would risk a loop if the session is already dead (e.g.
// extend-session's own 401 must not trigger another extend attempt).
async function authedRequest(path: string, init?: RequestInit) {
  try {
    return await apiFetch(path, init);
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) {
      unauthorizedHandler?.();
    }
    throw err;
  }
}

// The authenticated API client — `api.get("/api/customers")`,
// `api.post("/api/messages", data)`, etc. Every authenticated endpoint
// should be called through this, not apiFetch directly, so its 401 handling
// is automatic rather than something each caller re-implements.
export const api = {
  get: (path: string) => authedRequest(path),
  post: (path: string, body?: unknown) =>
    authedRequest(path, {
      method: "POST",
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    }),
  put: (path: string, body?: unknown) =>
    authedRequest(path, {
      method: "PUT",
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    }),
  delete: (path: string) => authedRequest(path, { method: "DELETE" }),
};

export type RegisterRequest = {
  businessName: string;
  timeZone: string;
  email: string;
  password: string;
};

export type RegisterResponse = {
  userId: string;
  businessId: string;
  email: string;
};

export type LoginRequest = {
  email: string;
  password: string;
  rememberMe?: boolean;
};

export type LoginResponse = {
  userId: string;
  businessId: string;
  email: string;
  roles: string[];
};

export type MeResponse = {
  userId: string;
  businessId: string;
  email: string;
  businessName: string;
  planTier: string | null;
  roles: string[];
  // Absolute expiry of the current session cookie, server-computed — the
  // frontend's session-expiry warning schedules off this, never off its own
  // guess. See AuthController.Me / ExtendSession.
  expiresAtUtc: string;
};

export function register(body: RegisterRequest): Promise<RegisterResponse> {
  return apiFetch("/api/auth/register", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function login(body: LoginRequest): Promise<LoginResponse> {
  try {
    return await apiFetch("/api/auth/login", {
      method: "POST",
      body: JSON.stringify(body),
    });
  } catch (err) {
    // AuthController.Login returns a bare 401 with no body for wrong
    // credentials, so parseErrorBody has nothing to work with here — give
    // it a real message instead of the generic "Request failed (401)."
    // fallback. Scoped to this one call, not apiFetch's shared 401 handling,
    // since login is the only place a 401 actually means "wrong password."
    if (err instanceof ApiError && err.status === 401) {
      throw new ApiError(401, "Invalid email or password.");
    }
    throw err;
  }
}

export function logout(): Promise<null> {
  return api.post("/api/auth/logout");
}

export function me(): Promise<MeResponse> {
  return apiFetch("/api/auth/me");
}

export type ConfirmEmailRequest = {
  userId: string;
  token: string;
};

export function confirmEmail(body: ConfirmEmailRequest): Promise<null> {
  return apiFetch("/api/auth/confirm-email", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function resendVerificationEmail(email: string): Promise<null> {
  return apiFetch("/api/auth/resend-verification", {
    method: "POST",
    body: JSON.stringify({ email }),
  });
}

export function forgotPassword(email: string): Promise<null> {
  return apiFetch("/api/auth/forgot-password", {
    method: "POST",
    body: JSON.stringify({ email }),
  });
}

export type ResetPasswordRequest = {
  userId: string;
  token: string;
  newPassword: string;
};

export function resetPassword(body: ResetPasswordRequest): Promise<null> {
  return apiFetch("/api/auth/reset-password", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export type ExtendSessionResponse = {
  expiresAtUtc: string;
};

// Server-side session extension (AuthController.ExtendSession) — the server
// re-issues the auth cookie with a new absolute (+2h) expiry and returns it
// here so the frontend timer can reschedule off the real value, not a guess.
// Goes through the authenticated client deliberately: if the session is
// already gone, this 401s like any other authenticated call, and the
// centralized handler above logs out — no separate handling needed here,
// and no risk of retrying an extend on a session that's already dead.
export function extendSession(): Promise<ExtendSessionResponse> {
  return api.post("/api/auth/extend-session");
}

// Talks to backend/Controllers/NotificationsController.cs.

export type NotificationItem = {
  id: string; // NotificationRecipient.Id — the value mark-read/read-all expect, never Notification.Id.
  category: "Team" | "Customer" | "System";
  type: string;
  title: string;
  link: string | null;
  isRead: boolean;
  createdAt: string;
};

// Compound (CreatedAt, Id) cursor — both fields are required together for
// correct keyset pagination and for read-all's tie-break; never reduce this
// to just the timestamp.
export type NotificationCursor = {
  createdAt: string;
  id: string;
};

export type NotificationListResponse = {
  notifications: NotificationItem[];
  nextCursor: NotificationCursor | null;
  readAllUpTo: NotificationCursor | null;
};

export type UnreadCountResponse = {
  count: number;
};

export function listNotifications(params?: {
  take?: number;
  before?: NotificationCursor;
}): Promise<NotificationListResponse> {
  const query = new URLSearchParams();
  if (params?.take) query.set("take", String(params.take));
  if (params?.before) {
    query.set("beforeCreatedAt", params.before.createdAt);
    query.set("beforeId", params.before.id);
  }
  const qs = query.toString();
  return api.get(`/api/notifications${qs ? `?${qs}` : ""}`);
}

export function getUnreadNotificationCount(): Promise<UnreadCountResponse> {
  return api.get("/api/notifications/unread-count");
}

export function markNotificationRead(id: string): Promise<null> {
  return api.post(`/api/notifications/${id}/read`);
}

// cursor must be the readAllUpTo value returned by listNotifications() —
// never constructed client-side, and never timestamp-only (see
// NotificationCursor above).
export function markAllNotificationsRead(cursor: NotificationCursor): Promise<null> {
  return api.post("/api/notifications/read-all", cursor);
}

// Talks to backend/Controllers/ConversationsController.cs.

export type ConversationSummary = {
  id: string;
  channel: string;
  status: string;
  handledBy: "Ai" | "Human";
  lastMessagePreview: string | null;
  lastMessageAt: string | null;
  createdAt: string;
};

// Compound keyset cursor, structurally similar to NotificationCursor but
// deliberately NOT named createdAt: activityAt is (lastMessageAt ??
// createdAt), the list's actual sort/filter key. Conversation.lastMessageAt
// is mutable (unlike NotificationRecipient.createdAt, set once and never
// updated) — see ConversationsController.List's doc comment for the one
// pagination consequence that has. Never reduce this to just the timestamp.
export type ConversationCursor = {
  activityAt: string;
  id: string;
};

export type ConversationListResponse = {
  conversations: ConversationSummary[];
  nextCursor: ConversationCursor | null;
};

export type ConversationMessage = {
  id: string;
  role: "Customer" | "Ai" | "Agent";
  content: string;
  createdAt: string;
};

export function listConversations(params?: {
  take?: number;
  before?: ConversationCursor;
}): Promise<ConversationListResponse> {
  const query = new URLSearchParams();
  if (params?.take) query.set("take", String(params.take));
  if (params?.before) {
    query.set("beforeActivityAt", params.before.activityAt);
    query.set("beforeId", params.before.id);
  }
  const qs = query.toString();
  return api.get(`/api/conversations${qs ? `?${qs}` : ""}`);
}

export function getConversationMessages(id: string): Promise<ConversationMessage[]> {
  return api.get(`/api/conversations/${id}/messages`);
}

// Talks to backend/Controllers/KnowledgeDocumentsController.cs.

export type KnowledgeDocumentType = "Faq" | "Policy" | "Product" | "Document";

export type KnowledgeDocument = {
  id: string;
  type: KnowledgeDocumentType;
  title: string;
  content: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
};

export type CreateKnowledgeDocumentRequest = {
  type: KnowledgeDocumentType;
  title: string;
  content: string;
};

export type UpdateKnowledgeDocumentRequest = {
  type: KnowledgeDocumentType;
  title: string;
  content: string;
  isActive: boolean;
};

export function listKnowledgeDocuments(): Promise<KnowledgeDocument[]> {
  return api.get("/api/knowledge");
}

export function getKnowledgeDocument(id: string): Promise<KnowledgeDocument> {
  return api.get(`/api/knowledge/${id}`);
}

export function createKnowledgeDocument(body: CreateKnowledgeDocumentRequest): Promise<KnowledgeDocument> {
  return api.post("/api/knowledge", body);
}

export function updateKnowledgeDocument(id: string, body: UpdateKnowledgeDocumentRequest): Promise<KnowledgeDocument> {
  return api.put(`/api/knowledge/${id}`, body);
}

// Soft-delete only (server sets IsActive = false, never a physical delete —
// see KnowledgeDocumentsController.Deactivate). To reactivate, call
// updateKnowledgeDocument with isActive: true.
export function deactivateKnowledgeDocument(id: string): Promise<null> {
  return api.delete(`/api/knowledge/${id}`);
}
