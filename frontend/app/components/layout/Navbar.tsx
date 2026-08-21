"use client";

import Link from "next/link";
import { useState } from "react";
import brand from "@/brand.json";
import { useAuth } from "../../lib/auth-context";
import { useTheme } from "../../lib/theme-context";
import { ThemeToggle } from "./ThemeToggle";

const links = [
  { href: "/product", label: "Product" },
  { href: "/use-cases", label: "Use cases" },
  { href: "/resources", label: "Resources" },
  { href: "/pricing", label: "Pricing" },
];

// Follows the theme like any other marketing surface (white in light mode,
// black in dark mode) — except under "nivora," where, like Sidebar.tsx, it
// becomes the brand's own fixed navy rail rather than tracking theme
// tokens. That's the one visible difference between "nivora" and "light"
// outside the dashboard, so every className below branches on the same
// `nivora` boolean Sidebar uses.
export function Navbar() {
  const [open, setOpen] = useState(false);
  const { user } = useAuth();
  const { theme } = useTheme();
  const nivora = theme === "nivora";

  return (
    <header
      className={`sticky top-0 z-50 w-full border-b backdrop-blur-xl ${
        nivora ? "border-white/10 bg-[#0e1226]" : "border-border/60 bg-background"
      }`}
    >
      <div className="mx-auto flex h-[72px] max-w-[1440px] items-center px-6 sm:px-10 lg:px-14">
        {/* Logo — routes to the dashboard, never the landing page, once signed in */}
        <Link
          href={user ? "/home" : "/"}
          className="group flex shrink-0 items-center gap-2.5"
        >
          <span
            className={`flex h-8 w-8 items-center justify-center rounded-[9px] text-[11px] font-bold tracking-tight transition-transform duration-200 group-hover:scale-105 ${
              nivora ? "bg-white text-[#0e1226]" : "bg-foreground text-background"
            }`}
          >
            N
          </span>

          <span className={`text-[13px] font-semibold tracking-[0.18em] ${nivora ? "text-white" : "text-foreground"}`}>
            {brand.name.toUpperCase()}
          </span>
        </Link>

        {/* Desktop Navigation */}
        <nav className="ml-16 hidden items-center gap-1 md:flex">
          {links.map((link) => (
            <a
              key={link.href}
              href={link.href}
              className={`group relative rounded-lg px-4 py-2.5 text-[13px] font-medium transition-colors duration-200 ${
                nivora
                  ? "text-white/70 hover:bg-white/10 hover:text-white"
                  : "text-muted-foreground hover:bg-muted/70 hover:text-foreground"
              }`}
            >
              {link.label}

              <span
                className={`absolute bottom-1.5 left-4 right-4 h-px origin-left scale-x-0 transition-transform duration-200 group-hover:scale-x-100 ${
                  nivora ? "bg-white/60" : "bg-foreground/60"
                }`}
              />
            </a>
          ))}
        </nav>

        {/* Desktop Actions */}
        <div className="ml-auto hidden items-center gap-3 md:flex">
          <ThemeToggle />
          {user ? (
            <a
              href="/home"
              className={`group inline-flex items-center gap-2 rounded-full px-5 py-2.5 text-[13px] font-medium shadow-sm transition-all duration-200 hover:-translate-y-0.5 hover:shadow-md ${
                nivora ? "bg-accent text-white" : "bg-foreground text-background"
              }`}
            >
              <span className="h-1.5 w-1.5 rounded-full bg-[#39FF88]" />
              Dashboard
            </a>
          ) : (
            <>
              <a
                href="/login"
                className={`rounded-lg px-4 py-2.5 text-[13px] font-medium transition-colors ${
                  nivora ? "text-white/70 hover:text-white" : "text-muted-foreground hover:text-foreground"
                }`}
              >
                Log in
              </a>

              <a
                href="/signup"
                className={`group inline-flex items-center gap-2 rounded-full px-5 py-2.5 text-[13px] font-medium shadow-sm transition-all duration-200 hover:-translate-y-0.5 hover:shadow-md ${
                  nivora ? "bg-accent text-white" : "bg-foreground text-background"
                }`}
              >
                Get started
                <span className="transition-transform duration-200 group-hover:translate-x-0.5">
                  →
                </span>
              </a>
            </>
          )}
        </div>

        {/* Mobile Menu Button */}
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          aria-controls="mobile-nav"
          aria-label={open ? "Close menu" : "Open menu"}
          className={`ml-auto flex h-10 w-10 cursor-pointer items-center justify-center rounded-xl border transition-colors md:hidden ${
            nivora ? "border-white/15 text-white hover:bg-white/10" : "border-border/60 text-foreground hover:bg-muted"
          }`}
        >
          <svg
            viewBox="0 0 24 24"
            className="h-[18px] w-[18px]"
            aria-hidden="true"
          >
            {open ? (
              <path
                d="M6 6l12 12M18 6 6 18"
                stroke="currentColor"
                strokeWidth="1.7"
                strokeLinecap="round"
              />
            ) : (
              <path
                d="M5 7h14M5 12h14M5 17h14"
                stroke="currentColor"
                strokeWidth="1.7"
                strokeLinecap="round"
              />
            )}
          </svg>
        </button>
      </div>

      {/* Mobile Navigation */}
      <div
        className={`overflow-hidden transition-all duration-300 md:hidden ${
          open
            ? `max-h-[500px] border-t opacity-100 ${nivora ? "border-white/10" : "border-border/60"}`
            : "pointer-events-none max-h-0 opacity-0"
        }`}
      >
        <nav
          id="mobile-nav"
          className="px-6 py-4 sm:px-10"
        >
          <div className="mx-auto max-w-[1440px]">
            <div className="flex flex-col gap-1">
              {links.map((link) => (
                <a
                  key={link.href}
                  href={link.href}
                  onClick={() => setOpen(false)}
                  className={`rounded-xl px-4 py-3.5 text-sm font-medium transition-colors ${
                    nivora
                      ? "text-white/70 hover:bg-white/10 hover:text-white"
                      : "text-muted-foreground hover:bg-muted hover:text-foreground"
                  }`}
                >
                  {link.label}
                </a>
              ))}
            </div>

            <div className={`mt-3 flex items-center justify-between border-t pt-3 ${nivora ? "border-white/10" : "border-border/60"}`}>
              <span className={`text-[13px] font-medium ${nivora ? "text-white/70" : "text-muted-foreground"}`}>Theme</span>
              <ThemeToggle />
            </div>

            <div className="mt-3 flex flex-col gap-2">
              {user ? (
                <a
                  href="/home"
                  onClick={() => setOpen(false)}
                  className={`rounded-xl px-4 py-3.5 text-center text-sm font-medium transition-opacity hover:opacity-90 ${
                    nivora ? "bg-accent text-white" : "bg-foreground text-background"
                  }`}
                >
                  Go to dashboard →
                </a>
              ) : (
                <>
                  <a
                    href="/login"
                    onClick={() => setOpen(false)}
                    className={`rounded-xl px-4 py-3 text-sm font-medium transition-colors ${
                      nivora
                        ? "text-white/70 hover:bg-white/10 hover:text-white"
                        : "text-muted-foreground hover:bg-muted hover:text-foreground"
                    }`}
                  >
                    Sign in
                  </a>

                  <a
                    href="/signup"
                    onClick={() => setOpen(false)}
                    className={`rounded-xl px-4 py-3.5 text-center text-sm font-medium transition-opacity hover:opacity-90 ${
                      nivora ? "bg-accent text-white" : "bg-foreground text-background"
                    }`}
                  >
                    Get started →
                  </a>
                </>
              )}
            </div>
          </div>
        </nav>
      </div>
    </header>
  );
}