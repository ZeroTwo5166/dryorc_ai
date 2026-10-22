"use client";

import { Suspense, useEffect, useState, type FormEvent } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import brand from "@/brand.json";
import { AuthShell } from "../components/auth/AuthShell";
import { FormError, FormField, PasswordInput, SubmitButton } from "../components/auth/FormField";
import { IconLock } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { ApiError, login, resendVerificationEmail } from "../lib/api";

// Written by accept-invite/page.tsx after a successful accept.
const LOGIN_PREFILL_EMAIL_KEY = "nivora.loginPrefillEmail";

export default function LoginPage() {
  return (
    <Suspense fallback={null}>
      <LoginForm />
    </Suspense>
  );
}

function LoginForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const verified = searchParams.get("verified") === "1";
  const { refresh } = useAuth();

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [rememberMe, setRememberMe] = useState(true);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [needsVerification, setNeedsVerification] = useState(false);
  const [resendState, setResendState] = useState<"idle" | "sending" | "sent">("idle");

  useEffect(() => {
    // Staff invite (ED-32): /accept-invite hands the new account's email over
    // through sessionStorage, so it never appears in a URL or history. Read
    // once and removed. After mount, not a lazy initializer: this client
    // component still SSRs, where sessionStorage does not exist.
    let prefill: string | null = null;
    try {
      prefill = window.sessionStorage.getItem(LOGIN_PREFILL_EMAIL_KEY);
      window.sessionStorage.removeItem(LOGIN_PREFILL_EMAIL_KEY);
    } catch {
      // Storage unavailable: nothing to prefill.
    }
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (prefill) setEmail(prefill);
  }, []);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setNeedsVerification(false);
    setResendState("idle");
    setLoading(true);
    try {
      await login({ email, password, rememberMe });
      await refresh();
      router.push("/home");
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message);
        setNeedsVerification(err.status === 403);
      } else {
        setError("Something went wrong. Please try again.");
      }
    } finally {
      setLoading(false);
    }
  }

  async function handleResend() {
    setResendState("sending");
    try {
      await resendVerificationEmail(email);
    } finally {
      setResendState("sent");
    }
  }

  return (
    <AuthShell
      eyebrow="Welcome back"
      title={`Sign in to ${brand.name}`}
      subtitle="Access your dashboard, conversations, and queue."
      icon={IconLock}
      switchLabel="Create account"
      switchHref="/signup"
    >
      {verified && (
        <div className="mb-6 rounded-xl border border-success-soft bg-success-soft px-3.5 py-3 text-[13px] text-success">
          Email verified — sign in to continue.
        </div>
      )}

      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <FormField
          id="email"
          label="Work email"
          type="email"
          autoComplete="email"
          placeholder="you@business.com"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
        />

        <div>
          <div className="mb-1.5 flex items-center justify-between">
            <label htmlFor="password" className="text-[13px] font-medium text-foreground">
              Password
            </label>
            <a href="/forgot-password" className="text-[12.5px] font-medium text-accent hover:opacity-80">
              Forgot password?
            </a>
          </div>
          <PasswordInput
            id="password"
            autoComplete="current-password"
            placeholder="••••••••"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>

        <label className="flex select-none items-center gap-2.5 text-[13px] text-muted-foreground">
          <input
            type="checkbox"
            checked={rememberMe}
            onChange={(e) => setRememberMe(e.target.checked)}
            className="h-4 w-4 rounded border-border text-accent focus:ring-accent/30"
          />
          Keep me signed in for 8 hours
        </label>

        {error && <FormError message={error} />}

        {needsVerification && (
          <p className="-mt-2 text-[13px] text-muted-foreground">
            {resendState === "sent" ? (
              "Verification email sent — check your inbox."
            ) : (
              <>
                Didn&apos;t get the link?{" "}
                <button
                  type="button"
                  onClick={handleResend}
                  disabled={resendState === "sending"}
                  className="cursor-pointer font-medium text-accent hover:opacity-80 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {resendState === "sending" ? "Sending…" : "Resend verification email"}
                </button>
              </>
            )}
          </p>
        )}

        <SubmitButton loading={loading}>Sign in</SubmitButton>
      </form>
    </AuthShell>
  );
}
