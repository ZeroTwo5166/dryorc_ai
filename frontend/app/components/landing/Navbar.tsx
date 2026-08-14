"use client";

import Link from "next/link";
import { useState } from "react";

const links = [
  { href: "/product", label: "Product" },
  { href: "/use-cases", label: "Use cases" },
  { href: "/resources", label: "Resources" },
  { href: "/pricing", label: "Pricing" },
];

export function Navbar() {
  const [open, setOpen] = useState(false);

  return (
    <header className="sticky top-0 z-50 w-full border-b border-border/60 bg-white backdrop-blur-xl">
      <div className="mx-auto flex h-[72px] max-w-[1440px] items-center px-6 sm:px-10 lg:px-14">
        {/* Logo */}
        <Link
          href="/"
          className="group flex shrink-0 items-center gap-2.5"
        >
          <span className="flex h-8 w-8 items-center justify-center rounded-[9px] bg-foreground text-[11px] font-bold tracking-tight text-background transition-transform duration-200 group-hover:scale-105">
            N
          </span>

          <span className="text-[13px] font-semibold tracking-[0.18em] text-foreground">
            NIVORA
          </span>
        </Link>

        {/* Desktop Navigation */}
        <nav className="ml-16 hidden items-center gap-1 md:flex">
          {links.map((link) => (
            <a
              key={link.href}
              href={link.href}
              className="group relative rounded-lg px-4 py-2.5 text-[13px] font-medium text-muted-foreground transition-colors duration-200 hover:bg-muted/70 hover:text-foreground"
            >
              {link.label}

              <span className="absolute bottom-1.5 left-4 right-4 h-px origin-left scale-x-0 bg-foreground/60 transition-transform duration-200 group-hover:scale-x-100" />
            </a>
          ))}
        </nav>

        {/* Desktop Actions */}
        <div className="ml-auto hidden items-center gap-3 md:flex">
          <a
            href="/login"
            className="rounded-lg px-4 py-2.5 text-[13px] font-medium text-black transition-colors hover:text-foreground"
          >
            Log in
          </a>

          <a
            href="/signup"
            className="group inline-flex items-center gap-2 rounded-full bg-foreground px-5 py-2.5 text-[13px] font-medium text-background shadow-sm transition-all duration-200 hover:-translate-y-0.5 hover:shadow-md"
          >
            Get started
            <span className="transition-transform duration-200 group-hover:translate-x-0.5">
              →
            </span>
          </a>
        </div>

        {/* Mobile Menu Button */}
        <button
          type="button"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          aria-controls="mobile-nav"
          aria-label={open ? "Close menu" : "Open menu"}
          className="ml-auto flex h-10 w-10 items-center justify-center rounded-xl border border-border/60 text-foreground transition-colors hover:bg-muted md:hidden"
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
            ? "max-h-[500px] border-t border-border/60 opacity-100"
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
                  className="rounded-xl px-4 py-3.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
                >
                  {link.label}
                </a>
              ))}
            </div>

            <div className="mt-3 flex flex-col gap-2 border-t border-border/60 pt-3">
              <a
                href="/login"
                onClick={() => setOpen(false)}
                className="rounded-xl px-4 py-3 text-sm font-medium text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
              >
                Sign in
              </a>

              <a
                href="/signup"
                onClick={() => setOpen(false)}
                className="rounded-xl bg-foreground px-4 py-3.5 text-center text-sm font-medium text-background transition-opacity hover:opacity-90"
              >
                Get started →
              </a>
            </div>
          </div>
        </nav>
      </div>
    </header>
  );
}