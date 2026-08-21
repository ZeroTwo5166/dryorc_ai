import type { ReactNode } from "react";
import { SessionExpiryModal } from "./SessionExpiryModal";
import { Sidebar } from "./Sidebar";
import { Topbar } from "./Topbar";

export function DashboardShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-screen bg-muted/30">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar />
        {children}
      </div>
      <SessionExpiryModal />
    </div>
  );
}
