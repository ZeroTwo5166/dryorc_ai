import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import AcceptInvitePage from "./page";
import AcceptInviteLayout from "./layout";
import { ThemeProvider } from "../lib/theme-context";

// T-D19a (staff invite D19, ED-30): the token in the #token= fragment leaves
// the address bar before any request, and travels only in POST bodies.
// api.ts is real here (fetch is stubbed), so the assertions see the actual
// URL and body every request is sent with.

const TOKEN = "q3Zt9xWc-Kp_2mNfR8sLvB4hYjD6aEoU1iGkT0wXcQ0";
const PREVIEW = { businessName: "Acme Supply Co.", email: "new.agent@example.com", role: "Agent" };

const { mockRouterReplace } = vi.hoisted(() => ({ mockRouterReplace: vi.fn() }));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/accept-invite",
}));

vi.mock("../lib/auth-context", () => ({
  useAuth: () => ({ user: null, status: "ready", logout: vi.fn() }),
}));

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function renderPage() {
  return render(
    <ThemeProvider>
      <AcceptInvitePage />
    </ThemeProvider>,
  );
}

// Runs the layout's inline script the way the browser does on a full page
// load: React renders the <script> without executing it, so the test takes
// its text and runs it once, before the page mounts.
function runLayoutScript() {
  const { container, unmount } = render(<AcceptInviteLayout params={Promise.resolve({})}>{null}</AcceptInviteLayout>);
  const script = container.querySelector("script");
  expect(script).not.toBeNull();
  const source = script!.textContent ?? "";
  unmount();
  new Function(source)();
}

function requestsTo(fetchMock: ReturnType<typeof vi.fn>, suffix: string) {
  return fetchMock.mock.calls.filter(([url]) => String(url).endsWith(suffix));
}

describe("AcceptInvitePage — T-D19a token handling", () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let replaceStateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    mockRouterReplace.mockClear();
    window.sessionStorage.clear();
    delete window.__nivoraInviteToken;
    window.history.replaceState(null, "", `/accept-invite#token=${TOKEN}`);
    fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, PREVIEW));
    vi.stubGlobal("fetch", fetchMock);
    replaceStateSpy = vi.spyOn(window.history, "replaceState");
  });

  afterEach(() => {
    replaceStateSpy.mockRestore();
    vi.unstubAllGlobals();
    delete window.__nivoraInviteToken;
    window.history.replaceState(null, "", "/");
  });

  function expectFragmentClearedBeforeFirstRequest() {
    expect(replaceStateSpy).toHaveBeenCalledWith(null, "", "/accept-invite");
    expect(fetchMock).toHaveBeenCalled();
    expect(replaceStateSpy.mock.invocationCallOrder[0]).toBeLessThan(fetchMock.mock.invocationCallOrder[0]);
  }

  function expectTokenOnlyInPostBody() {
    for (const [url, init] of fetchMock.mock.calls) {
      expect(String(url)).not.toContain(TOKEN);
      expect((init as RequestInit).method).toBe("POST");
      expect(JSON.stringify((init as RequestInit).headers ?? {})).not.toContain(TOKEN);
      expect(JSON.parse(String((init as RequestInit).body)).token).toBe(TOKEN);
    }
  }

  it("layout script path: clears the fragment before the preview request and sends the token only in the POST body", async () => {
    runLayoutScript();
    // The script ran before React: the fragment is already gone.
    expect(window.location.hash).toBe("");

    renderPage();

    expect(await screen.findByText(`Join ${PREVIEW.businessName}`)).toBeInTheDocument();
    expectFragmentClearedBeforeFirstRequest();
    expect(requestsTo(fetchMock, "/api/staff-invitations/preview")).toHaveLength(1);
    expectTokenOnlyInPostBody();
    expect(window.location.hash).toBe("");
    expect(window.location.href).not.toContain(TOKEN);
    expect(window.__nivoraInviteToken).toBeUndefined();
  });

  it("fallback path (layout script did not run): the page clears the fragment before the preview request", async () => {
    renderPage();

    expect(await screen.findByText(`Join ${PREVIEW.businessName}`)).toBeInTheDocument();
    expectFragmentClearedBeforeFirstRequest();
    expect(requestsTo(fetchMock, "/api/staff-invitations/preview")).toHaveLength(1);
    expectTokenOnlyInPostBody();
    expect(window.location.hash).toBe("");
    expect(window.location.href).not.toContain(TOKEN);
  });

  it("shows the preview with a read-only email and the role", async () => {
    renderPage();

    const email = await screen.findByLabelText("Email");
    expect(email).toHaveValue(PREVIEW.email);
    expect(email).toHaveAttribute("readonly");
    expect(screen.getByLabelText("Role")).toHaveValue("Agent");
  });

  it("accept sends the token only in the POST body, hands the email to /login through sessionStorage, and redirects", async () => {
    runLayoutScript();
    renderPage();
    await screen.findByText(`Join ${PREVIEW.businessName}`);

    fetchMock.mockResolvedValueOnce(jsonResponse(200, { email: PREVIEW.email }));
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    await waitFor(() => expect(mockRouterReplace).toHaveBeenCalledWith("/login"));
    const accepts = requestsTo(fetchMock, "/api/staff-invitations/accept");
    expect(accepts).toHaveLength(1);
    expect(JSON.parse(String((accepts[0][1] as RequestInit).body))).toEqual({ token: TOKEN, password: "Str0ng!Passw0rd" });
    expectTokenOnlyInPostBody();
    expect(window.sessionStorage.getItem("nivora.loginPrefillEmail")).toBe(PREVIEW.email);
    // The token itself is never stored.
    for (let i = 0; i < window.sessionStorage.length; i++) {
      expect(window.sessionStorage.getItem(window.sessionStorage.key(i)!)).not.toContain(TOKEN);
    }
  });

  it("shows the invalid-link view and makes no request when there is no token", async () => {
    window.history.replaceState(null, "", "/accept-invite");
    renderPage();

    expect(await screen.findByText(/invalid or expired/i)).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("shows the invalid-link view when preview returns 404 (also the switch-off case, D7)", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 404 }));
    renderPage();

    expect(await screen.findByText(/invalid or expired/i)).toBeInTheDocument();
  });

  it("shows the invalid-link view when accept returns 404", async () => {
    renderPage();
    await screen.findByText(`Join ${PREVIEW.businessName}`);

    fetchMock.mockResolvedValueOnce(new Response(null, { status: 404 }));
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    expect(await screen.findByText(/invalid or expired/i)).toBeInTheDocument();
    expect(mockRouterReplace).not.toHaveBeenCalled();
  });

  it("shows the sign-in-instead message when accept returns 409", async () => {
    renderPage();
    await screen.findByText(`Join ${PREVIEW.businessName}`);

    fetchMock.mockResolvedValueOnce(jsonResponse(409, "An account with this email already exists."));
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "Str0ng!Passw0rd" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    expect(await screen.findByText("This email already has a Nivora account. Sign in instead.")).toBeInTheDocument();
    expect(mockRouterReplace).not.toHaveBeenCalled();
  });

  it("shows the Identity messages when accept returns 400", async () => {
    renderPage();
    await screen.findByText(`Join ${PREVIEW.businessName}`);

    fetchMock.mockResolvedValueOnce(
      jsonResponse(400, ["Passwords must have at least one non alphanumeric character.", "Passwords must have at least one digit ('0'-'9')."]),
    );
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "longenoughpw" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "longenoughpw" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    expect(
      await screen.findByText(
        "Passwords must have at least one non alphanumeric character. Passwords must have at least one digit ('0'-'9').",
      ),
    ).toBeInTheDocument();
  });
});
