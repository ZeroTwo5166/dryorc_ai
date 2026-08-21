"use client";

import { Suspense, useState, type FormEvent } from "react";
import { useSearchParams } from "next/navigation";
import { AuthShell } from "../components/auth/AuthShell";
import { FormError, FormField, SubmitButton } from "../components/auth/FormField";
import { IconCheck, IconLock, IconMail } from "../components/landing/icons";
import { ApiError, resetPassword } from "../lib/api";

export default function ResetPasswordPage() {
  return (
    <Suspense fallback={null}>
      <ResetPasswordContent />
    </Suspense>
  );
}

function ResetPasswordContent() {
  const searchParams = useSearchParams();
  const userId = searchParams.get("userId");
  const token = searchParams.get("token");

  if (!userId || !token) {
    return <InvalidLinkState />;
  }
  return <ResetForm userId={userId} token={token} />;
}

function InvalidLinkState() {
  return (
    <AuthShell
      eyebrow="Link expired"
      title="This link is invalid or expired"
      subtitle="Password reset links only stay valid for a limited time — request a new one below."
      icon={IconMail}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <a
        href="/forgot-password"
        className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
      >
        Request a new link
      </a>
    </AuthShell>
  );
}

function ResetForm({ userId, token }: { userId: string; token: string }) {
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const [invalidLink, setInvalidLink] = useState(false);

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

    setLoading(true);
    try {
      await resetPassword({ userId, token, newPassword: password });
      setSuccess(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && /invalid or has expired/i.test(err.message)) {
        setInvalidLink(true);
      } else {
        setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
      }
    } finally {
      setLoading(false);
    }
  }

  if (invalidLink) {
    return <InvalidLinkState />;
  }

  if (success) {
    return (
      <AuthShell
        eyebrow="All set"
        title="Password updated"
        subtitle="Your password has been reset — sign in with your new password to continue."
        icon={IconCheck}
        switchLabel="Create account"
        switchHref="/signup"
      >
        <a
          href="/login"
          className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
        >
          Continue to sign in
        </a>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      eyebrow="Reset your password"
      title="Choose a new password"
      subtitle="Pick a strong password you haven't used before on this account."
      icon={IconLock}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <FormField
          id="password"
          label="New password"
          type="password"
          autoComplete="new-password"
          placeholder="At least 8 characters"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />

        <FormField
          id="confirmPassword"
          label="Confirm new password"
          type="password"
          autoComplete="new-password"
          placeholder="••••••••"
          required
          value={confirmPassword}
          onChange={(e) => setConfirmPassword(e.target.value)}
        />

        {error && <FormError message={error} />}

        <SubmitButton loading={loading}>Reset password</SubmitButton>
      </form>
    </AuthShell>
  );
}
