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

async function apiFetch(path: string, init?: RequestInit) {
  const res = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...init?.headers,
    },
  });

  if (!res.ok) {
    const message =
      res.status === 401
        ? "Invalid email or password."
        : res.status === 409
          ? "An account with this email already exists."
          : await res.text().catch(() => "") || `Request failed (${res.status}).`;
    throw new ApiError(res.status, message);
  }

  if (res.status === 204) return null;
  return res.json();
}

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
};

export function register(body: RegisterRequest): Promise<RegisterResponse> {
  return apiFetch("/api/auth/register", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function login(body: LoginRequest): Promise<LoginResponse> {
  return apiFetch("/api/auth/login", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export function logout(): Promise<null> {
  return apiFetch("/api/auth/logout", { method: "POST" });
}

export function me(): Promise<MeResponse> {
  return apiFetch("/api/auth/me");
}
