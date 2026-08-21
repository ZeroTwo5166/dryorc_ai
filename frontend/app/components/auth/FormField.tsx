"use client";

import { useState, type InputHTMLAttributes } from "react";
import { useTheme } from "../../lib/theme-context";
import { IconEye, IconEyeOff } from "../landing/icons";

type FormFieldProps = InputHTMLAttributes<HTMLInputElement> & {
  label: string;
};

const inputClassName =
  "w-full rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20";

export function FormField({ label, id, type, ...props }: FormFieldProps) {
  return (
    <div>
      <label
        htmlFor={id}
        className="mb-1.5 block text-[13px] font-medium text-foreground"
      >
        {label}
      </label>
      {type === "password" ? (
        <PasswordInput id={id} {...props} />
      ) : (
        <input id={id} type={type} {...props} className={inputClassName} />
      )}
    </div>
  );
}

// Standalone password input with a show/hide toggle — no label, so it also
// works for login's password field (which needs a custom label row with the
// "Forgot password?" link alongside it, not FormField's built-in label).
// FormField above uses this internally for every other password field.
export function PasswordInput({
  id,
  className,
  ...props
}: Omit<InputHTMLAttributes<HTMLInputElement>, "type">) {
  const [visible, setVisible] = useState(false);

  return (
    <div className="relative">
      <input
        id={id}
        type={visible ? "text" : "password"}
        {...props}
        className={`${inputClassName} pr-11 ${className ?? ""}`}
      />
      <button
        type="button"
        onClick={() => setVisible((v) => !v)}
        aria-label={visible ? "Hide password" : "Show password"}
        aria-pressed={visible}
        className="absolute inset-y-0 right-0 flex w-11 cursor-pointer items-center justify-center text-muted-foreground transition-colors hover:text-foreground"
      >
        {visible ? <IconEyeOff className="h-[18px] w-[18px]" /> : <IconEye className="h-[18px] w-[18px]" />}
      </button>
    </div>
  );
}

export function FormError({ message }: { message: string }) {
  return (
    <div className="rounded-xl border border-destructive-soft bg-destructive-soft px-3.5 py-3 text-[13px] text-destructive">
      {message}
    </div>
  );
}

// Under "nivora," the primary auth action (Sign in, Create account, Reset
// password, ...) picks up the brand's accent blue instead of the neutral
// bg-primary/text-primary-foreground pairing — same accent used for the
// primary CTA in Navbar.tsx under nivora, so the blue reads as "the brand's
// action color" consistently across marketing, auth, and dashboard.
export function SubmitButton({
  loading,
  children,
}: {
  loading: boolean;
  children: React.ReactNode;
}) {
  const { theme } = useTheme();
  const nivora = theme === "nivora";

  return (
    <button
      type="submit"
      disabled={loading}
      className={`inline-flex cursor-pointer w-full items-center justify-center gap-2 rounded-full px-5 py-3 text-sm font-medium shadow-sm transition-all hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60 ${
        nivora ? "bg-accent text-accent-foreground" : "bg-primary text-primary-foreground"
      }`}
    >
      {loading && (
        <span
          className={`h-3.5 w-3.5 animate-spin rounded-full border-2 ${
            nivora ? "border-accent-foreground/40 border-t-accent-foreground" : "border-primary-foreground/40 border-t-primary-foreground"
          }`}
        />
      )}
      {children}
    </button>
  );
}
