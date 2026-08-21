"use client";

import { useState, type FormEvent } from "react";
import { AuthShell } from "../components/auth/AuthShell";
import { FormError, FormField, SubmitButton } from "../components/auth/FormField";
import { IconMail } from "../components/landing/icons";
import { ApiError, forgotPassword } from "../lib/api";

export default function ForgotPasswordPage() {
  const [email, setEmail] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      await forgotPassword(email);
      setSent(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setLoading(false);
    }
  }

  if (sent) {
    return (
      <AuthShell
        eyebrow="Check your inbox"
        title="Reset link on its way"
        subtitle="If there's an account with that email, we've sent a link to reset your password. It'll expire shortly, so use it soon."
        icon={IconMail}
        switchLabel="Sign in"
        switchHref="/login"
      >
        <a
          href="/login"
          className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
        >
          Back to sign in
        </a>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      eyebrow="Reset your password"
      title="Forgot your password?"
      subtitle="Enter your work email and, if there's an account for it, we'll send you a link to reset your password."
      icon={IconMail}
      switchLabel="Sign in"
      switchHref="/login"
    >
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

        {error && <FormError message={error} />}

        <SubmitButton loading={loading}>Send reset link</SubmitButton>
      </form>
    </AuthShell>
  );
}
