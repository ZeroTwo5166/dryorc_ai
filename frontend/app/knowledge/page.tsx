"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../components/layout/DashboardShell";
import { IconWorkflow } from "../components/landing/icons";
import { useAuth } from "../lib/auth-context";
import { formatRelativeTime } from "../lib/relative-time";
import { listKnowledgeDocuments, type KnowledgeDocument } from "../lib/api";

const TYPE_LABEL: Record<string, string> = {
  Faq: "FAQ",
  Policy: "Policy",
  Product: "Product",
  Document: "Document",
};

export default function KnowledgePage() {
  const router = useRouter();
  const { user, status } = useAuth();
  const canWrite = user?.roles.some((r) => r === "Owner" || r === "Admin") ?? false;

  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
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

    listKnowledgeDocuments()
      .then((result) => {
        if (cancelled) return;
        setDocuments(result);
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
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
              Knowledge base
            </h1>
            <p className="mt-2 text-sm text-muted-foreground">
              FAQs, policies, and reference documents for {user.businessName}&apos;s AI and team.
            </p>
          </div>
          {canWrite && (
            <Link
              href="/knowledge/new"
              className="shrink-0 rounded-full bg-primary px-4 py-2 text-[13px] font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90"
            >
              New document
            </Link>
          )}
        </div>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : error ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load the knowledge base</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server. Try refreshing the page.
            </p>
          </div>
        ) : documents.length === 0 ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-accent-soft text-accent">
              <IconWorkflow className="h-5 w-5" />
            </span>
            <p className="text-sm font-medium text-foreground">No documents yet</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              {canWrite
                ? "Add FAQs, policies, and reference material for your team and AI to use."
                : "An Owner or Admin hasn't added anything here yet."}
            </p>
          </div>
        ) : (
          <div className="mt-8 divide-y divide-border overflow-hidden rounded-3xl border border-border bg-card">
            {documents.map((d) => (
              <Link
                key={d.id}
                href={`/knowledge/${d.id}`}
                className="flex items-start gap-4 px-6 py-5 transition-colors hover:bg-muted/50"
              >
                <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
                  <IconWorkflow className="h-4 w-4" />
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium text-foreground">{d.title}</span>
                    <span className="rounded-full bg-muted px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-muted-foreground">
                      {TYPE_LABEL[d.type] ?? d.type}
                    </span>
                    {!d.isActive && (
                      <span className="rounded-full bg-destructive-soft px-2 py-0.5 text-[10.5px] font-semibold uppercase tracking-wider text-destructive">
                        Inactive
                      </span>
                    )}
                  </div>
                  <p className="mt-1.5 truncate text-[13px] text-muted-foreground">{d.content}</p>
                  <p className="mt-1 text-[12.5px] text-muted-foreground/70">
                    Updated {formatRelativeTime(d.updatedAt)}
                  </p>
                </div>
              </Link>
            ))}
          </div>
        )}
      </main>
    </DashboardShell>
  );
}
