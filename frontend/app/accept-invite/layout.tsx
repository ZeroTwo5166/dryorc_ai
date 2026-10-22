import type { Metadata } from "next";

// Staff invite (D19, ED-30). The invitation token arrives in the URL
// fragment (#token=...). This blocking script runs while the browser parses
// the HTML, before React hydrates, so the fragment leaves the address bar
// and history at the earliest point the app controls. The token moves to a
// window global that page.tsx reads and deletes on mount. Same inline-script
// idiom as the theme script in app/layout.tsx. On a client-side navigation
// this script does not run; page.tsx has its own fallback for that case.
const inviteTokenScript = `
  try {
    if (location.hash.indexOf("#token=") === 0) {
      window.__nivoraInviteToken = location.hash.slice(7);
      history.replaceState(null, "", location.pathname);
    }
  } catch (e) {}
`;

// The meta tag; next.config.ts sends the Referrer-Policy header too (ED-31).
export const metadata: Metadata = {
  referrer: "no-referrer",
};

export default function AcceptInviteLayout({ children }: LayoutProps<"/accept-invite">) {
  return (
    <>
      <script dangerouslySetInnerHTML={{ __html: inviteTokenScript }} />
      {children}
    </>
  );
}
