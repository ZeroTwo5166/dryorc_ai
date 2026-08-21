import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import brand from "@/brand.json";
import "./globals.css";
import { AuthProvider } from "./lib/auth-context";
import { ThemeProvider } from "./lib/theme-context";

// Runs synchronously before hydration, directly on the real DOM, so the
// correct theme is already painted on first frame — without it, a
// previously-chosen dark theme would flash the default light/nivora look
// for a moment before React/theme-context.tsx catch up. No explicit choice
// yet (first-ever visit) needs no action here at all: globals.css's base
// :root values already are the "nivora" look, matching ThemeProvider's own
// default state — the two agree without this script doing anything.
const themeInitScript = `
  try {
    var stored = localStorage.getItem("nivora.theme");
    if (stored === "light" || stored === "dark" || stored === "nivora") {
      document.documentElement.setAttribute("data-theme", stored);
    }
  } catch (e) {}
`;

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: `${brand.name} — AI customer support that can safely act`,
  description:
    `${brand.name} is an AI support agent for e-commerce. It answers from your own knowledge base, verifies who it's talking to, and takes safe, auditable action — with a human always in the loop.`,
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      // The blocking script below sets data-theme on this element before
      // React hydrates, so its attributes can legitimately differ from what
      // was server-rendered — that's expected here, not a bug to warn about.
      suppressHydrationWarning
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeInitScript }} />
      </head>
      <body className="min-h-full flex flex-col bg-background text-foreground font-sans">
        <ThemeProvider>
          <AuthProvider>{children}</AuthProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
