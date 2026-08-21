"use client";

import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

// "nivora" is the brand's own signature look. Page content follows the
// same globals.css tokens as "light" (no override needed there), but the
// app's persistent chrome — Sidebar (dashboard) and Navbar (marketing) —
// special-cases theme === "nivora" to render its fixed blue-navy rail
// instead of following the theme normally. It's also the default for a
// first-time visitor — see ThemeProvider below and the blocking script in
// layout.tsx.
export type Theme = "light" | "dark" | "nivora";

type ThemeState = {
  theme: Theme;
  setTheme: (theme: Theme) => void;
};

const ThemeContext = createContext<ThemeState | null>(null);

const STORAGE_KEY = "nivora.theme";

function applyTheme(theme: Theme) {
  document.documentElement.setAttribute("data-theme", theme);
}

// Mounted once at the root layout, alongside AuthProvider. The page is
// already painted in the right theme before this ever runs — see the
// blocking script in layout.tsx, which sets data-theme on <html>
// synchronously before hydration to avoid a flash of the wrong theme on
// load. This provider exists only so components (ThemeToggle, Sidebar) can
// read and change the current choice, and have it persist across visits.
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>("nivora");

  useEffect(() => {
    // Reads back what the blocking script already established, rather than
    // re-deciding here — one source of truth (localStorage), read twice.
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (stored === "light" || stored === "dark" || stored === "nivora") {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setThemeState(stored);
    }
  }, []);

  function setTheme(next: Theme) {
    setThemeState(next);
    try {
      window.localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // best-effort — the theme still applies for this page view even if persistence fails
    }
    applyTheme(next);
  }

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>;
}

export function useTheme() {
  const ctx = useContext(ThemeContext);
  if (!ctx) {
    throw new Error("useTheme must be used within ThemeProvider");
  }
  return ctx;
}
