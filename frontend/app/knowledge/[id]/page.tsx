"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useParams, useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError, FormField, SubmitButton } from "../../components/auth/FormField";
import { IconArrowRight } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import {
  ApiError,
  deactivateKnowledgeDocument,
  getKnowledgeDocument,
  updateKnowledgeDocument,
  type KnowledgeDocument,
  type KnowledgeDocumentType,
} from "../../lib/api";

const TYPE_OPTIONS: { value: KnowledgeDocumentType; label: string }[] = [
  { value: "Faq", label: "FAQ" },
  { value: "Policy", label: "Policy" },
  { value: "Product", label: "Product" },
  { value: "Document", label: "Document" },
];

export default function KnowledgeDocumentDetailPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const { user, status } = useAuth();
  const canWrite = user?.roles.some((r) => r === "Owner" || r === "Admin") ?? false;

  const [document, setDocument] = useState<KnowledgeDocument | null>(null);
  const [type, setType] = useState<KnowledgeDocumentType>("Faq");
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  const [saving, setSaving] = useState(false);
  const [togglingActive, setTogglingActive] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    }
  }, [status, user, router]);

  useEffect(() => {
    if (!user) return;
    let cancelled = false;

    getKnowledgeDocument(params.id)
      .then((result) => {
        if (cancelled) return;
        setDocument(result);
        setType(result.type);
        setTitle(result.title);
        setContent(result.content);
        setLoadError(false);
      })
      .catch(() => {
        if (!cancelled) setLoadError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [user, params.id]);

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    if (!document) return;
    setSaveError(null);
    setSaving(true);
    try {
      const updated = await updateKnowledgeDocument(document.id, {
        type,
        title,
        content,
        isActive: document.isActive,
      });
      setDocument(updated);
    } catch (err) {
      setSaveError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleActive() {
    if (!document) return;
    setTogglingActive(true);
    try {
      if (document.isActive) {
        await deactivateKnowledgeDocument(document.id);
        setDocument({ ...document, isActive: false });
      } else {
        const updated = await updateKnowledgeDocument(document.id, { type, title, content, isActive: true });
        setDocument(updated);
      }
    } catch {
      // Leave state as-is — the button remains available to retry.
    } finally {
      setTogglingActive(false);
    }
  }

  if (status === "loading" || !user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <DashboardShell>
      <main className="mx-auto max-w-[640px] px-6 py-12 sm:px-10">
        <Link
          href="/knowledge"
          className="inline-flex items-center gap-1.5 text-[13px] font-medium text-muted-foreground hover:text-foreground"
        >
          <IconArrowRight className="h-3.5 w-3.5 rotate-180" />
          Back to knowledge base
        </Link>

        {loading ? (
          <div className="mt-10 flex justify-center">
            <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
          </div>
        ) : loadError || !document ? (
          <div className="mt-10 flex flex-col items-center gap-3 rounded-3xl border border-border bg-card px-6 py-16 text-center">
            <p className="text-sm font-medium text-foreground">Couldn&apos;t load this document</p>
            <p className="max-w-xs text-[13px] text-muted-foreground">
              Something went wrong reaching the server, or it doesn&apos;t belong to your business.
            </p>
          </div>
        ) : (
          <>
            <div className="mt-4 flex items-start justify-between gap-4">
              <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
                {canWrite ? "Edit document" : document.title}
              </h1>
              {!document.isActive && (
                <span className="mt-1 shrink-0 rounded-full bg-destructive-soft px-2.5 py-1 text-[10.5px] font-semibold uppercase tracking-wider text-destructive">
                  Inactive
                </span>
              )}
            </div>

            <form onSubmit={handleSave} className="mt-8 flex flex-col gap-5">
              <div>
                <label htmlFor="type" className="mb-1.5 block text-[13px] font-medium text-foreground">
                  Type
                </label>
                <select
                  id="type"
                  required
                  disabled={!canWrite}
                  value={type}
                  onChange={(e) => setType(e.target.value as KnowledgeDocumentType)}
                  className="w-full cursor-pointer rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {TYPE_OPTIONS.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              <FormField
                id="title"
                label="Title"
                type="text"
                required
                disabled={!canWrite}
                value={title}
                onChange={(e) => setTitle(e.target.value)}
              />

              <div>
                <label htmlFor="content" className="mb-1.5 block text-[13px] font-medium text-foreground">
                  Content
                </label>
                <textarea
                  id="content"
                  required
                  rows={10}
                  disabled={!canWrite}
                  value={content}
                  onChange={(e) => setContent(e.target.value)}
                  className="w-full resize-y rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20 disabled:cursor-not-allowed disabled:opacity-60"
                />
              </div>

              {saveError && <FormError message={saveError} />}

              {canWrite && (
                <div className="flex items-center gap-3">
                  <SubmitButton loading={saving}>Save changes</SubmitButton>
                  <button
                    type="button"
                    onClick={handleToggleActive}
                    disabled={togglingActive}
                    className={`shrink-0 rounded-full border px-4 py-2.5 text-[13px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                      document.isActive
                        ? "border-destructive-soft text-destructive hover:bg-destructive-soft"
                        : "border-border text-foreground hover:bg-muted"
                    }`}
                  >
                    {togglingActive ? "Working…" : document.isActive ? "Deactivate" : "Reactivate"}
                  </button>
                </div>
              )}
            </form>
          </>
        )}
      </main>
    </DashboardShell>
  );
}
