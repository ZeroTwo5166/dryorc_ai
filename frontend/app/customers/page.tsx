"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconUserCheck } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { listCustomers, type CustomerSummary } from "../lib/api";

export default function CustomersPage() {
  const router = useRouter();
  const { user, status } = useAuth();

  const [customers, setCustomers] = useState<CustomerSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    listCustomers()
      .then((result) => {
        if (cancelled) return;
        setCustomers(result);
        setError(false);
      })
      .catch(() => {
        if (!cancelled) setError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user]);

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[820px] px-6 py-12 sm:px-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
            Customers
          </h1>
          <p className="mt-2 text-sm text-muted-foreground">
            Profiles, linked identities, and conversation history for {user.businessName}&apos;s customers.
          </p>
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load customers</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : customers.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconUserCheck className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No customers yet</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Customers appear here once they&apos;re created or verified through a conversation.
            </p>
          </div>
        ) : (
          <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
            {customers.map((c) => (
              <Link
                key={c.id}
                href={`/customers/${c.id}`}
                className="flex items-start gap-4 px-6 py-5 transition-colors hover:bg-muted/50"
              >
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                  <IconUserCheck className="h-4 w-4" />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium text-foreground">
                    {c.name ?? c.email ?? "Unnamed customer"}
                  </p>
                  {c.name && c.email && (
                    <p className="mt-1 text-[13px] text-muted-foreground">{c.email}</p>
                  )}
                </div>
              </Link>
            ))}
          </div>
        )}
      </main>
    </DashboardShell>
  );
}
