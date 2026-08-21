import type { ReactNode } from "react";
import { Footer } from "../layout/Footer";
import { Navbar } from "../layout/Navbar";

export function MarketingShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex w-full flex-1 flex-col">
      <Navbar />
      <main className="flex-1">{children}</main>
      <Footer />
    </div>
  );
}
