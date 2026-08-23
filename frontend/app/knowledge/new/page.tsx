"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { DashboardShell } from "../../components/layout/DashboardShell";
import { FormError, FormField, SubmitButton } from "../../components/auth/FormField";
import { IconArrowRight } from "../../components/landing/icons";
import { useAuth } from "../../lib/auth-context";
import { ApiError, createKnowledgeDocument, type KnowledgeDocumentType } from "../../lib/api";

const TYPE_OPTIONS: { value: KnowledgeDocumentType; label: string }[] = [
  { value: "Faq", label: "FAQ" },
  { value: "Policy", label: "Policy" },
  { value: "Product", label: "Product" },
  { value: "Document", label: "Document" },
];

export default function NewKnowledgeDocumentPage() {
  const router = useRouter();
  const { user, status } = useAuth();
  const canWrite = user?.roles.some((r) => r === "Owner" || r === "Admin") ?? false;

  const [type, setType] = useState<KnowledgeDocumentType>("Faq");
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (status === "ready" && !user) {
      router.replace("/login");
    } else if (status === "ready" && user && !canWrite) {
      router.replace("/knowledge");
    }
  }, [status, user, canWrite, router]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const created = await createKnowledgeDocument({ type, title, content });
      router.push(`/knowledge/${created.id}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setLoading(false);
    }
  }

  if (status === "loading" || !user || !canWrite) {
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

        <h1 className="mt-4 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">
          New document
        </h1>

        <form onSubmit={handleSubmit} className="mt-8 flex flex-col gap-5">
          <div>
            <label htmlFor="type" className="mb-1.5 block text-[13px] font-medium text-foreground">
              Type
            </label>
            <select
              id="type"
              required
              value={type}
              onChange={(e) => setType(e.target.value as KnowledgeDocumentType)}
              className="w-full cursor-pointer rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
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
            placeholder="e.g. Shipping times"
            required
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
              placeholder="What should your team and AI know?"
              value={content}
              onChange={(e) => setContent(e.target.value)}
              className="w-full resize-y rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20"
            />
          </div>

          {error && <FormError message={error} />}

          <SubmitButton loading={loading}>Create document</SubmitButton>
        </form>
      </main>
    </DashboardShell>
  );
}
