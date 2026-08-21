"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { AuthShell } from "../components/auth/AuthShell";
import { IconCheck, IconMail } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { confirmEmail, resendVerificationEmail } from "../lib/api";

export default function VerifyEmailPage() {
  return (
    <Suspense fallback={null}>
      <VerifyEmailContent />
    </Suspense>
  );
}

function VerifyEmailContent() {
  const searchParams = useSearchParams();
  const userId = searchParams.get("userId");
  const token = searchParams.get("token");
  const email = searchParams.get("email");

  // A real confirmation link (userId + token) always gets processed,
  // regardless of who's asking — RequireConfirmedEmail means a signed-in
  // user is already verified, but the backend's confirm-email endpoint is
  // idempotent (204 either way), so this is harmless to run again.
  if (userId && token) {
    return <ConfirmState userId={userId} token={token} email={email} />;
  }
  return <NoTokenState email={email} />;
}

// Reached with no userId/token — either the normal post-signup redirect
// (?email=... only, see signup/page.tsx) or someone landing on the bare
// route directly. What to show depends on who's asking:
//   - signed in: RequireConfirmedEmail guarantees their email is already
//     confirmed (unverified accounts can't log in — see AuthController.Login),
//     so "check your inbox" would be actively wrong. Say so instead.
//   - not signed in, ?email= present: the legitimate just-registered case —
//     show the existing pending state.
//   - not signed in, nothing at all: no legitimate reason to be here — bounce
//     to the landing page instead of showing a fabricated "we sent a link."
function NoTokenState({ email }: { email: string | null }) {
  const router = useRouter();
  const { user, status } = useAuth();
  const bareVisit = status === "ready" && !user && !email;

  useEffect(() => {
    if (bareVisit) router.replace("/");
  }, [bareVisit, router]);

  if (status === "loading" || bareVisit) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  if (user) {
    return (
      <AuthShell
        eyebrow="You're all set"
        title="Your email is already verified"
        subtitle="No further action needed — head to your dashboard to keep going."
        icon={IconCheck}
        switchLabel="Sign in"
        switchHref="/login"
      >
        <a
          href="/home"
          className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
        >
          Go to dashboard
        </a>
      </AuthShell>
    );
  }

  return <PendingState email={email} />;
}

function ResendControl({ email }: { email: string | null }) {
  const [state, setState] = useState<"idle" | "sending" | "sent">("idle");

  if (!email) return null;

  async function handleResend() {
    setState("sending");
    try {
      await resendVerificationEmail(email!);
    } finally {
      setState("sent");
    }
  }

  if (state === "sent") {
    return (
      <p className="text-[13px] text-success">
        Sent — check your inbox for a fresh link.
      </p>
    );
  }

  return (
    <button
      type="button"
      onClick={handleResend}
      disabled={state === "sending"}
      className="cursor-pointer text-[13px] font-medium text-accent hover:opacity-80 disabled:cursor-not-allowed disabled:opacity-60"
    >
      {state === "sending" ? "Sending…" : "Resend verification email"}
    </button>
  );
}

function PendingState({ email }: { email: string | null }) {
  return (
    <AuthShell
      eyebrow="Almost there"
      title="Check your inbox"
      subtitle={
        email
          ? `We sent a verification link to ${email}. Click it to activate your account.`
          : "We sent you a verification link. Click it to activate your account."
      }
      icon={IconMail}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <div className="flex flex-col items-start gap-3">
        <ResendControl email={email} />
      </div>
    </AuthShell>
  );
}

function ConfirmState({
  userId,
  token,
  email,
}: {
  userId: string;
  token: string;
  email: string | null;
}) {
  const [status, setStatus] = useState<"confirming" | "success" | "error">("confirming");

  // Guards against React Strict Mode's dev-only double-invoke of this effect
  // sending two concurrent confirmEmail requests. Unlike a plain GET, a
  // second concurrent confirm-email POST isn't harmless: the backend
  // (AuthController.ConfirmEmail) reads the user, sees EmailConfirmed still
  // false, and calls ConfirmEmailAsync — if two requests both read before
  // either writes, the second one to reach the DB loses an optimistic-
  // concurrency check and comes back "invalid or expired" even though the
  // first one already succeeded (the account IS verified; refreshing the
  // page proves it). A `cancelled`-flag cleanup doesn't fix this — it only
  // suppresses which request's *result* gets shown, not the duplicate
  // request itself. A ref does, because it survives Strict Mode's synthetic
  // unmount/remount of effects (unlike a fresh local variable), so the
  // second invocation sees it's already fired and skips sending anything.
  const requestedRef = useRef(false);

  useEffect(() => {
    if (requestedRef.current) return;
    requestedRef.current = true;

    confirmEmail({ userId, token })
      .then(() => setStatus("success"))
      .catch(() => setStatus("error"));
  }, [userId, token]);

  if (status === "confirming") {
    return (
      <AuthShell
        eyebrow="One moment"
        title="Verifying your email"
        subtitle="This will only take a second."
        icon={IconMail}
        switchLabel="Sign in"
        switchHref="/login"
      >
        <div className="flex justify-center py-4">
          <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
        </div>
      </AuthShell>
    );
  }

  if (status === "success") {
    return (
      <AuthShell
        eyebrow="You're verified"
        title="Email confirmed"
        subtitle="Your account is active — sign in to get started."
        icon={IconCheck}
        switchLabel="Create account"
        switchHref="/signup"
      >
        <a
          href="/login?verified=1"
          className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-all hover:opacity-90"
        >
          Continue to sign in
        </a>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      eyebrow="Link expired"
      title="This link is invalid or expired"
      subtitle="Verification links only stay valid for a limited time — request a new one below."
      icon={IconMail}
      switchLabel="Sign in"
      switchHref="/login"
    >
      <div className="flex flex-col items-start gap-3">
        <ResendControl email={email} />
      </div>
    </AuthShell>
  );
}
