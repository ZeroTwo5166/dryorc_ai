"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { AuthShell } from "../components/auth/AuthShell";
import { FormError, FormField, SubmitButton } from "../components/auth/FormField";
import { IconMail, IconUserCheck } from "../components/landing/icons";
import {
  ApiError,
  acceptStaffInvitation,
  previewStaffInvitation,
  type PreviewStaffInvitationResponse,
} from "../lib/api";

declare global {
  interface Window {
    // Set by accept-invite/layout.tsx's inline script, read and deleted here.
    __nivoraInviteToken?: string;
  }
}

const TOKEN_PREFIX = "#token=";
const LOGIN_PREFILL_EMAIL_KEY = "nivora.loginPrefillEmail";

type View =
  | { kind: "loading" }
  | { kind: "invalid" }
  | { kind: "error" }
  | { kind: "form"; preview: PreviewStaffInvitationResponse };

export default function AcceptInvitePage() {
  const router = useRouter();
  // The token is held in memory only: never in a URL, storage or log (D19).
  // A ref, not state, so React StrictMode's second effect run in development
  // still finds it after the first run has deleted the window global.
  const tokenRef = useRef<string | null>(null);
  const [view, setView] = useState<View>({ kind: "loading" });

  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const scriptToken = window.__nivoraInviteToken;
    delete window.__nivoraInviteToken;
    if (scriptToken) {
      tokenRef.current = scriptToken;
    } else if (!tokenRef.current && window.location.hash.startsWith(TOKEN_PREFIX)) {
      // Fallback: the layout script did not run (client-side navigation).
      // Clear the fragment at once, before any request.
      tokenRef.current = window.location.hash.slice(TOKEN_PREFIX.length);
      window.history.replaceState(null, "", window.location.pathname);
    }

    const token = tokenRef.current;
    if (!token) {
      setView({ kind: "invalid" });
      return;
    }

    let cancelled = false;
    previewStaffInvitation(token)
      .then((preview) => {
        if (!cancelled) setView({ kind: "form", preview });
      })
      .catch((err) => {
        if (cancelled) return;
        // 404 is the generic invalid link, and also the switch-off answer (D7).
        setView(err instanceof ApiError && err.status === 404 ? { kind: "invalid" } : { kind: "error" });
      });

    return () => {
      cancelled = true;
    };
  }, []);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (password !== confirmPassword) {
      setError("Passwords don't match.");
      return;
    }
    if (password.length < 8) {
      setError("Password must be at least 8 characters.");
      return;
    }

    const token = tokenRef.current;
    if (!token) {
      setView({ kind: "invalid" });
      return;
    }

    setLoading(true);
    try {
      const accepted = await acceptStaffInvitation(token, password);
      try {
        window.sessionStorage.setItem(LOGIN_PREFILL_EMAIL_KEY, accepted.email);
      } catch {
        // Storage unavailable: /login simply starts empty.
      }
      router.replace("/login");
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setView({ kind: "invalid" });
      } else if (err instanceof ApiError && err.status === 409) {
        setError("This email already has a Nivora account. Sign in instead.");
      } else if (err instanceof ApiError && err.status === 400) {
        // The Identity password messages, joined by api.ts's parseErrorBody.
        setError(err.message);
      } else {
        setError("Something went wrong. Please try again.");
      }
      setLoading(false);
    }
  }

  if (view.kind === "loading") {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  if (view.kind === "invalid") {
    return <InvalidLinkState />;
  }

  if (view.kind === "error") {
    return (
      <AuthShell
        eyebrow="Invitation"
        title="Something went wrong"
        subtitle="We couldn't check this invitation right now. Open the link in your invitation email again to retry."
        icon={IconMail}
        switchLabel="Sign in"
        switchHref="/login"
      >
        <a
          href="/login"
          className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
        >
          Go to sign in
        </a>
      </AuthShell>
    );
  }

  const { preview } = view;
  return (
    <AuthShell
      eyebrow="You're invited"
      title={`Join ${preview.businessName}`}
      subtitle={`You've been invited as ${preview.role}. Choose a password to create your account.`}
      icon={IconUserCheck}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <FormField id="email" label="Email" type="email" value={preview.email} readOnly />

        <FormField id="role" label="Role" type="text" value={preview.role} readOnly />

        <FormField
          id="password"
          label="Password"
          type="password"
          autoComplete="new-password"
          placeholder="At least 8 characters"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />

        <FormField
          id="confirmPassword"
          label="Confirm password"
          type="password"
          autoComplete="new-password"
          placeholder="••••••••"
          required
          value={confirmPassword}
          onChange={(e) => setConfirmPassword(e.target.value)}
        />

        {error && <FormError message={error} />}

        <SubmitButton loading={loading}>Create account</SubmitButton>
      </form>
    </AuthShell>
  );
}

function InvalidLinkState() {
  return (
    <AuthShell
      eyebrow="Invitation"
      title="This invitation link is invalid or expired"
      subtitle="Ask the person who invited you to send a new invitation."
      icon={IconMail}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <a
        href="/login"
        className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
      >
        Go to sign in
      </a>
    </AuthShell>
  );
}
