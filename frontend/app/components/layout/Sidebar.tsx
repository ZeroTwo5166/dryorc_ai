"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ComponentType } from "react";
import brand from "@/brand.json";
import {
  IconChevronsLeft,
  IconLogOut,
  IconMail,
  IconMessageSquare,
  IconMonitor,
  IconRadar,
  IconSend,
  IconSettings,
  IconShieldCheck,
  IconTarget,
  IconUserCheck,
  IconWorkflow,
} from "../landing/icons";
import { useAuth } from "../../lib/auth-context";
import { useTheme } from "../../lib/theme-context";
import { ThemeToggle } from "./ThemeToggle";

type IconProps = { className?: string };

type NavItem = {
  label: string;
  icon: ComponentType<IconProps>;
  href?: string;
  soon?: boolean;
};

type NavSection = {
  label: string;
  items: NavItem[];
};

// Icon choices below all come from the existing hand-drawn set in
// ../landing/icons — none of the "ideal" icons for these items (a
// dashboard-grid glyph, an inbox tray, a people glyph, a book, a ticket, a
// user-cog, a plug, a combo chart, a chat-bot/insight glyph) actually exist
// there, and this app's icon system is a small closed set of custom SVGs,
// not a swappable library — so each item below uses the closest existing
// icon instead of a new one-off implementation: IconMonitor (a screen) for
// the dashboard-like Overview, IconMail (a literal inbox) for Inbox,
// IconUserCheck (a person) for Customers, IconWorkflow (connected content
// blocks) for Knowledge base, IconMessageSquare (a ticket-as-conversation)
// for Tickets, IconShieldCheck (roles/trust) for Team, IconSend (data in/
// out) for Integrations, IconRadar (live metrics) for Analytics, and
// IconTarget (precision/insight) for Analyst — unused anywhere else in the
// dashboard, closest available stand-in for "ask a question, get an
// answer."
const STORAGE_KEY = "nivora.sidebar.collapsed";

// Follows the theme like everything else (white/light-grey in light mode,
// black/dark-grey in dark mode) — except under the "nivora" theme
// specifically, where it's the brand's own signature fixed blue-navy rail,
// regardless of what the rest of the app looks like (which, under "nivora,"
// is identical to light — see globals.css). Every className below branches
// on that one `nivora` boolean rather than on theme tokens, since this is
// the one component in the app that doesn't just follow CSS variables.
export function Sidebar() {
  const pathname = usePathname();
  const router = useRouter();
  const { user, logout } = useAuth();
  const { theme } = useTheme();
  const [collapsed, setCollapsed] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const accountMenuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // Reads the persisted rail state after mount rather than a lazy
    // useState initializer, deliberately — this is a client component that
    // still SSRs, and localStorage isn't available there. A lazy
    // initializer would desync the server/client render (hydration
    // mismatch) instead of the one-frame update this causes.
    const stored = window.localStorage.getItem(STORAGE_KEY);
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (stored === "1") setCollapsed(true);
  }, []);

  useEffect(() => {
    if (!accountMenuOpen) return;

    function handlePointerDown(e: MouseEvent) {
      if (accountMenuRef.current && !accountMenuRef.current.contains(e.target as Node)) {
        setAccountMenuOpen(false);
      }
    }
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") setAccountMenuOpen(false);
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [accountMenuOpen]);

  function toggleCollapsed() {
    setCollapsed((prev) => {
      const next = !prev;
      window.localStorage.setItem(STORAGE_KEY, next ? "1" : "0");
      return next;
    });
  }

  async function handleSignOut() {
    setSigningOut(true);
    try {
      await logout();
    } finally {
      router.push("/login");
    }
  }

  if (!user) return null;

  const nivora = theme === "nivora";
  const initial = user.businessName.trim().charAt(0).toUpperCase() || "N";

  // Computed per-render (not a static module-level const, unlike before) —
  // the Analyst item's presence depends on user.aiBusinessAnalystEnabled,
  // per design §4's "the client hides/removes the entry point entirely."
  // A disabled business must see no trace of this item, not a disabled/
  // "Soon" row like Team/Integrations get.
  const navSections: NavSection[] = [
    {
      label: "Main",
      items: [
        { label: "Overview", icon: IconMonitor, href: "/home" },
        { label: "Inbox", icon: IconMail, href: "/conversations" },
        { label: "Customers", icon: IconUserCheck, href: "/customers" },
        { label: "Knowledge base", icon: IconWorkflow, href: "/knowledge" },
        { label: "Tickets", icon: IconMessageSquare, href: "/tickets" },
      ],
    },
    {
      label: "Manage",
      items: [
        { label: "Team", icon: IconShieldCheck, href: "/team" },
        { label: "Integrations", icon: IconSend, soon: true },
        { label: "Analytics", icon: IconRadar, href: "/analytics" },
        ...(user.aiBusinessAnalystEnabled
          ? [{ label: "Analyst", icon: IconTarget, href: "/analyst" }]
          : []),
      ],
    },
    {
      label: "System",
      items: [{ label: "Settings", icon: IconSettings, href: "/settings/integrations" }],
    },
  ];

  // Unchanged from the original single-list render — just factored out so
  // it can run once per section instead of once for the whole flat list.
  function renderNavItem(item: NavItem) {
    const active = item.href != null && pathname === item.href;
    const Icon = item.icon;

    const content = (
      <>
        <span
          className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-xl transition-colors ${
            active
              ? nivora
                ? "bg-accent text-white"
                : "bg-foreground text-background"
              : item.soon
                ? nivora
                  ? "text-white/35"
                  : "text-muted-foreground/50"
                : nivora
                  ? "text-white/70 group-hover:bg-white/10 group-hover:text-white"
                  : "text-muted-foreground group-hover:bg-muted group-hover:text-foreground"
          }`}
        >
          <Icon className="h-[18px] w-[18px]" />
        </span>
        <span
          className={`flex flex-1 items-center justify-between overflow-hidden whitespace-nowrap transition-all duration-200 ${
            collapsed ? "w-0 opacity-0" : "w-auto opacity-100"
          }`}
        >
          <span
            className={`text-[13px] font-medium ${
              active
                ? nivora
                  ? "text-white"
                  : "text-foreground"
                : item.soon
                  ? nivora
                    ? "text-white/35"
                    : "text-muted-foreground/50"
                  : nivora
                    ? "text-white/80"
                    : "text-foreground/80"
            }`}
          >
            {item.label}
          </span>
          {item.soon && (
            <span
              className={`ml-2 shrink-0 rounded-full px-1.5 py-0.5 text-[9.5px] font-semibold uppercase tracking-wider ${
                nivora ? "bg-white/8 text-white/40" : "bg-muted text-muted-foreground"
              }`}
            >
              Soon
            </span>
          )}
        </span>

        {/* Tooltip, collapsed state only — inverted relative to
            the rail for contrast, same convention as a native
            tooltip. */}
        {collapsed && (
          <span
            className={`pointer-events-none absolute left-full top-1/2 z-20 ml-3 -translate-y-1/2 scale-95 whitespace-nowrap rounded-lg px-2.5 py-1.5 text-xs font-medium opacity-0 shadow-lg shadow-slate-900/20 transition-all duration-150 group-hover:scale-100 group-hover:opacity-100 ${
              nivora
                ? "bg-[#0e1226] text-white ring-1 ring-white/10"
                : "bg-foreground text-background"
            }`}
          >
            {item.label}
            {item.soon && (
              <span className={nivora ? "ml-1.5 text-white/40" : "ml-1.5 text-background/60"}>
                · Soon
              </span>
            )}
          </span>
        )}
      </>
    );

    const rowClasses = `group relative flex items-center gap-3 rounded-xl px-2 py-2 transition-colors ${
      active
        ? nivora
          ? "bg-white/10"
          : "bg-muted"
        : item.soon
          ? "cursor-not-allowed"
          : nivora
            ? "hover:bg-white/5"
            : "hover:bg-muted/60"
    }`;

    if (item.href && !item.soon) {
      return (
        <li key={item.label}>
          <Link href={item.href} className={rowClasses}>
            {content}
          </Link>
        </li>
      );
    }

    return (
      <li key={item.label}>
        <div className={rowClasses} aria-disabled="true">
          {content}
        </div>
      </li>
    );
  }

  return (
    <aside
      className={`sticky top-0 z-30 flex h-screen shrink-0 flex-col transition-[width] duration-300 ease-[cubic-bezier(0.4,0,0.2,1)] ${
        nivora ? "bg-[#0e1226]" : "border-r border-border bg-card"
      } ${collapsed ? "w-[76px]" : "w-[260px]"}`}
    >
      {nivora && (
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden">
          <div className="absolute -left-24 -top-32 h-72 w-72 rounded-full bg-accent/20 blur-[100px]" />
          <div className="absolute -bottom-32 -left-16 h-72 w-72 rounded-full bg-purple/10 blur-[100px]" />
        </div>
      )}

      {/* Collapse toggle — floats on the sidebar/content boundary, always
          neutral (it sits on the page's own background, not the rail's). */}
      <button
        type="button"
        onClick={toggleCollapsed}
        aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        className="absolute -right-3 top-7 z-10 flex h-6 w-6 cursor-pointer items-center justify-center rounded-full border border-border bg-card text-muted-foreground shadow-md shadow-slate-900/10 transition-all hover:text-foreground"
      >
        <IconChevronsLeft
          className={`h-3.5 w-3.5 transition-transform duration-300 ${collapsed ? "rotate-180" : ""}`}
        />
      </button>

      <div className="relative flex h-full flex-col">
        {/* Logo */}
        <div className="flex h-[68px] shrink-0 items-center gap-2.5 px-5">
          <Link href="/home" className="flex shrink-0 items-center gap-2.5">
            <span
              className={`flex h-8 w-8 items-center justify-center rounded-[9px] text-[11px] font-bold tracking-tight ${
                nivora ? "bg-white text-[#0e1226]" : "bg-foreground text-background"
              }`}
            >
              N
            </span>
            <span
              className={`overflow-hidden whitespace-nowrap text-[13px] font-semibold tracking-[0.18em] transition-all duration-200 ${
                nivora ? "text-white" : "text-foreground"
              } ${collapsed ? "w-0 opacity-0" : "w-auto opacity-100"}`}
            >
              {brand.name.toUpperCase()}
            </span>
          </Link>
        </div>

        {/* Nav */}
        <nav className="flex-1 overflow-y-auto overflow-x-hidden px-3 py-2">
          {navSections.map((section, index) => (
            <div key={section.label} className={index === 0 ? "" : "mt-4"}>
              {/* Section label — hidden while collapsed, same as every
                  other piece of item text; icon-only nav and tooltips
                  underneath are untouched. */}
              {!collapsed && (
                <p
                  className={`px-2.5 pb-1.5 text-[10.5px] font-semibold uppercase tracking-wider ${
                    nivora ? "text-white/30" : "text-muted-foreground/60"
                  }`}
                >
                  {section.label}
                </p>
              )}
              <ul className="flex flex-col gap-1">
                {section.items.map((item) => renderNavItem(item))}
              </ul>
            </div>
          ))}
        </nav>

        {/* Footer — account block */}
        <div className={`shrink-0 border-t px-3 py-3 ${nivora ? "border-white/10" : "border-border"}`}>
          {!collapsed && (
            <div className="mb-1 flex items-center justify-between px-2 py-1.5">
              <span
                className={`text-[11px] font-medium uppercase tracking-wider ${
                  nivora ? "text-white/40" : "text-muted-foreground"
                }`}
              >
                Theme
              </span>
              <ThemeToggle />
            </div>
          )}

          {/* Account trigger — the whole row is now the entry point for
              settings/sign-out (see the popover below) instead of sign-out
              having its own standalone row underneath. The collapsed-state
              hover tooltip is unchanged; it's just a preview, independent of
              the click-to-open menu. */}
          <div ref={accountMenuRef} className="relative">
            <button
              type="button"
              onClick={() => setAccountMenuOpen((v) => !v)}
              aria-haspopup="true"
              aria-expanded={accountMenuOpen}
              className={`group relative flex w-full cursor-pointer items-center gap-3 rounded-xl px-2 py-2 transition-colors ${
                nivora ? "hover:bg-white/5" : "hover:bg-muted/60"
              }`}
            >
              <span
                className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-[12px] font-semibold ${
                  nivora ? "bg-accent-soft text-accent" : "bg-muted text-foreground"
                }`}
              >
                {initial}
              </span>
              <span
                className={`min-w-0 flex-1 overflow-hidden whitespace-nowrap text-left transition-all duration-200 ${
                  collapsed ? "w-0 opacity-0" : "w-auto opacity-100"
                }`}
              >
                <span className={`block truncate text-[13px] font-medium ${nivora ? "text-white" : "text-foreground"}`}>
                  {user.businessName}
                </span>
                <span className={`block truncate text-[11.5px] ${nivora ? "text-white/45" : "text-muted-foreground"}`}>
                  {user.email}
                </span>
              </span>

              {collapsed && (
                <span
                  className={`pointer-events-none absolute left-full top-1/2 z-20 ml-3 -translate-y-1/2 scale-95 whitespace-nowrap rounded-lg px-2.5 py-1.5 text-xs font-medium opacity-0 shadow-lg shadow-slate-900/20 transition-all duration-150 group-hover:scale-100 group-hover:opacity-100 ${
                    nivora ? "bg-[#0e1226] text-white ring-1 ring-white/10" : "bg-foreground text-background"
                  }`}
                >
                  {user.businessName}
                  <span className={nivora ? "ml-1.5 text-white/40" : "ml-1.5 text-background/60"}>{user.email}</span>
                </span>
              )}
            </button>

            {accountMenuOpen && (
              <div
                className={`absolute bottom-full left-0 z-30 mb-2 w-60 overflow-hidden rounded-2xl border shadow-xl ${
                  nivora ? "border-white/10 bg-[#121736]" : "border-border bg-card shadow-slate-900/10"
                }`}
              >
                <div className={`flex items-center gap-2.5 border-b px-3 py-3 ${nivora ? "border-white/10" : "border-border"}`}>
                  <span
                    className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-[12px] font-semibold ${
                      nivora ? "bg-accent-soft text-accent" : "bg-muted text-foreground"
                    }`}
                  >
                    {initial}
                  </span>
                  <div className="min-w-0">
                    <p className={`truncate text-[13px] font-medium ${nivora ? "text-white" : "text-foreground"}`}>
                      {user.businessName}
                    </p>
                    <p className={`truncate text-[11.5px] ${nivora ? "text-white/45" : "text-muted-foreground"}`}>
                      {user.email}
                    </p>
                  </div>
                </div>

                <div className="p-1.5">
                  {/* No user-settings page exists yet — same "Soon" state as
                      the Settings nav item above, kept consistent rather
                      than linking somewhere that 404s. */}
                  <div
                    aria-disabled="true"
                    className={`flex cursor-not-allowed items-center justify-between gap-2 rounded-xl px-2.5 py-2 text-[13px] font-medium ${
                      nivora ? "text-white/35" : "text-muted-foreground/50"
                    }`}
                  >
                    <span className="flex items-center gap-2.5">
                      <IconSettings className="h-4 w-4" />
                      User settings
                    </span>
                    <span
                      className={`rounded-full px-1.5 py-0.5 text-[9.5px] font-semibold uppercase tracking-wider ${
                        nivora ? "bg-white/8 text-white/40" : "bg-muted text-muted-foreground"
                      }`}
                    >
                      Soon
                    </span>
                  </div>

                  <button
                    type="button"
                    onClick={handleSignOut}
                    disabled={signingOut}
                    className={`flex w-full cursor-pointer items-center gap-2.5 rounded-xl px-2.5 py-2 text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${
                      nivora
                        ? "text-white/70 hover:bg-white/5 hover:text-white"
                        : "text-muted-foreground hover:bg-muted/60 hover:text-foreground"
                    }`}
                  >
                    <IconLogOut className="h-4 w-4" />
                    {signingOut ? "Signing out…" : "Sign out"}
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </aside>
  );
}
