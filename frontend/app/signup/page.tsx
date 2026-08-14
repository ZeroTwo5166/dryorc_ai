"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { AuthShell } from "../components/auth/AuthShell";
import { FormError, FormField, SubmitButton } from "../components/auth/FormField";
import { ApiError, register } from "../lib/api";

const timeZones = [
  "Europe/Copenhagen",
  "Europe/Berlin",
  "Europe/Amsterdam",
  "Europe/Brussels",
  "Europe/Paris",
  "Europe/Madrid",
  "Europe/Rome",
  "Europe/Vienna",
  "Europe/Zurich",
  "Europe/Stockholm",
  "Europe/Oslo",
  "Europe/Helsinki",
  "Europe/Warsaw",
  "Europe/Prague",
  "Europe/Budapest",
  "Europe/Athens",
  "Europe/Bucharest",
  "Europe/Lisbon",
  "Europe/Dublin",
  "Europe/London",
];

export default function SignupPage() {
  const router = useRouter();

  const [businessName, setBusinessName] = useState("");
  const [timeZone, setTimeZone] = useState(timeZones[0]);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
      await register({ businessName, timeZone, email, password });
      router.push("/login?registered=1");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <AuthShell
      eyebrow="Get started"
      title="Create your business account"
      subtitle="You'll be the Owner — invite Admins and Agents once you're in."
    >
      <form onSubmit={handleSubmit} className="flex flex-col gap-5">
        <FormField
          id="businessName"
          label="Business name"
          type="text"
          autoComplete="organization"
          placeholder="Acme Supply Co."
          required
          value={businessName}
          onChange={(e) => setBusinessName(e.target.value)}
        />

        <div>
          <label htmlFor="timeZone" className="mb-1.5 block text-[13px] font-medium text-foreground">
            Time zone
          </label>
          <select
            id="timeZone"
            required
            value={timeZone}
            onChange={(e) => setTimeZone(e.target.value)}
            className="w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
          >
            {timeZones.map((tz) => (
              <option key={tz} value={tz}>
                {tz}
              </option>
            ))}
          </select>
        </div>

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

        <p className="text-center text-[12.5px] leading-relaxed text-muted-foreground">
          By continuing you agree to Nivora&apos;s{" "}
          <a href="/terms" className="font-medium text-foreground hover:text-accent">
            Terms
          </a>{" "}
          and{" "}
          <a href="/privacy" className="font-medium text-foreground hover:text-accent">
            Privacy Policy
          </a>
          .
        </p>
      </form>

      <p className="mt-8 text-center text-sm text-muted-foreground">
        Already have an account?{" "}
        <a href="/login" className="font-medium text-foreground hover:text-accent">
          Sign in
        </a>
      </p>
    </AuthShell>
  );
}
