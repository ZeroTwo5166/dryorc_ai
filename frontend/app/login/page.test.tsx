import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import LoginPage from "./page";
import { ThemeProvider } from "../lib/theme-context";

// T-LP (staff invite ED-32): /accept-invite hands the new account's email to
// /login through sessionStorage. Login reads it once and removes it.

const KEY = "nivora.loginPrefillEmail";

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  useSearchParams: () => new URLSearchParams(),
  usePathname: () => "/login",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: null, status: "ready", logout: vi.fn(), refresh: vi.fn() }),
}));

function renderPage() {
  return render(
    <ThemeProvider>
      <LoginPage />
    </ThemeProvider>,
  );
}

describe("LoginPage — T-LP email prefill", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });

  it("prefills the email from sessionStorage and removes the key", async () => {
    window.sessionStorage.setItem(KEY, "new.agent@example.com");

    renderPage();

    await waitFor(() => expect(screen.getByLabelText("Work email")).toHaveValue("new.agent@example.com"));
    expect(window.sessionStorage.getItem(KEY)).toBeNull();
  });

  it("is read only once: a second mount after the first starts with an empty email", async () => {
    window.sessionStorage.setItem(KEY, "new.agent@example.com");
    const first = renderPage();
    await waitFor(() => expect(screen.getByLabelText("Work email")).toHaveValue("new.agent@example.com"));
    first.unmount();

    renderPage();
    expect(screen.getByLabelText("Work email")).toHaveValue("");
  });

  it("leaves the email empty when there is nothing to prefill", () => {
    renderPage();
    expect(screen.getByLabelText("Work email")).toHaveValue("");
  });
});
