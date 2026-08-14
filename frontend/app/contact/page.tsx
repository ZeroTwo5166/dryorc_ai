"use client";

import { useState, type FormEvent } from "react";
import { FormField } from "../components/auth/FormField";
import { IconMessageSquare, IconShieldCheck, IconWorkflow } from "../components/landing/icons";
import { MarketingShell } from "../components/marketing/MarketingShell";

const channels = [
  {
    icon: IconMessageSquare,
    title: "Book a demo",
    detail: "hello@nivora.app",
  },
  {
    icon: IconWorkflow,
    title: "Response time",
    detail: "Within one business day",
  },
  {
    icon: IconShieldCheck,
    title: "For everyone else",
    detail: "EU-hosted, GDPR-aligned by default",
  },
];

export default function ContactPage() {
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [message, setMessage] = useState("");

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const subject = encodeURIComponent(`Nivora inquiry from ${name || "website visitor"}`);
    const body = encodeURIComponent(`${message}\n\n— ${name} (${email})`);
    window.location.href = `mailto:hello@nivora.app?subject=${subject}&body=${body}`;
  }

  return (
    <MarketingShell>
      <div className="border-b border-border bg-gradient-to-b from-accent-soft/60 to-background">
        <div className="mx-auto grid max-w-[1440px] gap-16 px-6 py-20 sm:px-10 lg:grid-cols-[1fr_1.2fr] lg:px-14 lg:py-28">
          <div>
            <p className="text-sm font-medium text-accent">Contact</p>
            <h1 className="mt-3 text-4xl font-semibold leading-[1.1] tracking-tight text-foreground sm:text-5xl">
              Let&apos;s see it on your store.
            </h1>
            <p className="mt-6 max-w-md text-lg leading-relaxed text-muted-foreground">
              Tell us a bit about your setup and we&apos;ll show you exactly
              how Nivora handles your catalog, policies, and volume.
            </p>

            <div className="mt-10 flex flex-col gap-5">
              {channels.map((c) => {
                const Icon = c.icon;
                return (
                  <div key={c.title} className="flex items-center gap-4">
                    <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-card text-foreground shadow-sm">
                      <Icon className="h-5 w-5" />
                    </span>
                    <div>
                      <p className="text-sm font-semibold text-foreground">{c.title}</p>
                      <p className="text-sm text-muted-foreground">{c.detail}</p>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>

          <form
            onSubmit={handleSubmit}
            className="flex flex-col gap-5 rounded-3xl border border-border bg-card p-8 shadow-xl shadow-slate-900/10 sm:p-10"
          >
            <FormField
              id="name"
              label="Name"
              type="text"
              autoComplete="name"
              placeholder="Jane Doe"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
            <FormField
              id="email"
              label="Work email"
              type="email"
              autoComplete="email"
              placeholder="you@business.com"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
            <div>
              <label htmlFor="message" className="mb-1.5 block text-[13px] font-medium text-foreground">
                What are you looking to solve?
              </label>
              <textarea
                id="message"
                required
                rows={4}
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                placeholder="A few sentences about your store and support volume."
                className="w-full resize-none rounded-xl border border-border bg-card px-3.5 py-2.5 text-sm text-foreground shadow-sm outline-none transition-colors placeholder:text-muted-foreground/70 focus:border-accent focus:ring-2 focus:ring-accent/20"
              />
            </div>

            <button
              type="submit"
              className="inline-flex w-full items-center justify-center gap-2 rounded-full bg-primary px-5 py-3 text-sm font-medium text-primary-foreground shadow-sm transition-opacity hover:opacity-90"
            >
              Send message &rarr;
            </button>
            <p className="text-center text-[12.5px] text-muted-foreground">
              Opens your email client, addressed to hello@nivora.app.
            </p>
          </form>
        </div>
      </div>
    </MarketingShell>
  );
}
