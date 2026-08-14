import Link from "next/link";

const productLinks = [
  { href: "/product", label: "Product" },
  { href: "/use-cases", label: "Use cases" },
  { href: "/resources", label: "Resources" },
  { href: "/pricing", label: "Pricing" },
];

const companyLinks = [
  { href: "/about", label: "About" },
  { href: "/contact", label: "Contact" },
];

const legalLinks = [
  { href: "/privacy", label: "Privacy" },
  { href: "/terms", label: "Terms" },
];

export function Footer() {
  const year = new Date().getFullYear();

  return (
    <footer className="border-t border-border bg-background">
      <div className="mx-auto max-w-[1440px] px-6 py-16 sm:px-10 lg:px-14">
        <div className="grid gap-12 md:grid-cols-[1.5fr_1fr_1fr_1fr]">
          {/* Brand */}
          <div>
            <Link
              href="/"
              className="inline-flex items-center gap-2.5 text-sm font-semibold tracking-tight text-foreground transition-opacity hover:opacity-70"
            >
              <span
                aria-hidden="true"
                className="flex h-8 w-8 items-center justify-center rounded-[9px] bg-accent text-[11px] font-bold text-accent-foreground"
              >
                N
              </span>

              <span className="tracking-[0.12em]">NIVORA</span>
            </Link>

            <p className="mt-5 max-w-sm text-sm leading-6 text-muted-foreground">
              AI customer support for modern businesses — grounded in your
              knowledge, connected to your systems, and designed to keep
              humans in control.
            </p>

            <a
              href="mailto:kontakt@nivora.com"
              className="mt-5 inline-block text-sm font-medium text-foreground transition-colors hover:text-accent"
            >
              hello@nivora.app
            </a>
          </div>

          {/* Product */}
          <div>
            <p className="mb-4 text-xs font-semibold uppercase tracking-[0.16em] text-foreground">
              Product
            </p>

            <nav className="flex flex-col gap-3">
              {productLinks.map((link) => (
                <a
                  key={link.href}
                  href={link.href}
                  className="w-fit text-sm text-muted-foreground transition-colors hover:text-foreground"
                >
                  {link.label}
                </a>
              ))}
            </nav>
          </div>

          {/* Company */}
          <div>
            <p className="mb-4 text-xs font-semibold uppercase tracking-[0.16em] text-foreground">
              Company
            </p>

            <nav className="flex flex-col gap-3">
              {companyLinks.map((link) => (
                <a
                  key={link.href}
                  href={link.href}
                  className="w-fit text-sm text-muted-foreground transition-colors hover:text-foreground"
                >
                  {link.label}
                </a>
              ))}
            </nav>
          </div>

          {/* Legal */}
          <div>
            <p className="mb-4 text-xs font-semibold uppercase tracking-[0.16em] text-foreground">
              Legal
            </p>

            <nav className="flex flex-col gap-3">
              {legalLinks.map((link) => (
                <a
                  key={link.href}
                  href={link.href}
                  className="w-fit text-sm text-muted-foreground transition-colors hover:text-foreground"
                >
                  {link.label}
                </a>
              ))}
            </nav>
          </div>
        </div>
      </div>

      {/* Bottom bar */}
      <div className="border-t border-border">
        <div className="mx-auto flex max-w-[1440px] flex-col gap-3 px-6 py-6 text-xs text-muted-foreground sm:flex-row sm:items-center sm:justify-between sm:px-10 lg:px-14">
          <p>&copy; {year} Nivora. All rights reserved.</p>

          <div className="flex items-center gap-2">
            <span className="h-1.5 w-1.5 rounded-full bg-[#39FF88]" />
            <span>EU-hosted</span>
          </div>
        </div>
      </div>
    </footer>
  );
}