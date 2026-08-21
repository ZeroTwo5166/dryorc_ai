"use client";

import { useTheme, type Theme } from "../../lib/theme-context";
import { IconMoon, IconSun } from "../landing/icons";

export function ThemeToggle() {
  const { theme, setTheme } = useTheme();

  function optionClass(value: Theme) {
    const active = theme === value;
    return `flex h-7 w-7 cursor-pointer items-center justify-center rounded-full transition-colors ${
      active ? "bg-card text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground"
    }`;
  }

  return (
    <div className="inline-flex items-center gap-0.5 rounded-full border border-border bg-muted/60 p-0.5">
      <button
        type="button"
        onClick={() => setTheme("light")}
        aria-label="Light theme"
        aria-pressed={theme === "light"}
        className={optionClass("light")}
      >
        <IconSun className="h-[15px] w-[15px]" />
      </button>
      <button
        type="button"
        onClick={() => setTheme("dark")}
        aria-label="Dark theme"
        aria-pressed={theme === "dark"}
        className={optionClass("dark")}
      >
        <IconMoon className="h-[15px] w-[15px]" />
      </button>
      <button
        type="button"
        onClick={() => setTheme("nivora")}
        aria-label="Nivora theme"
        aria-pressed={theme === "nivora"}
        className={optionClass("nivora")}
      >
        {/* The brand mark itself, not a line icon — this option isn't
            "light" or "dark," it's the app's own signature look. */}
        <span className="flex h-[15px] w-[15px] items-center justify-center rounded-[4px] bg-[#0e1226] text-[8px] font-bold leading-none text-white">
          N
        </span>
      </button>
    </div>
  );
}
