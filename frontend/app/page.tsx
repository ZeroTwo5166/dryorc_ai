"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { CTA } from "./components/landing/CTA";
import { Features } from "./components/landing/Features";
import { Hero } from "./components/landing/Hero";
import { HowItWorks } from "./components/landing/HowItWorks";
import { Security } from "./components/landing/Security";
import { Footer } from "./components/layout/Footer";
import { Navbar } from "./components/layout/Navbar";
import { useAuth } from "./lib/auth-context";

export default function Home() {
  const router = useRouter();
  const { user, status } = useAuth();

  useEffect(() => {
    if (status === "ready" && user) {
      router.replace("/home");
    }
  }, [status, user, router]);

  // Loading, or already signed in and about to be redirected — never let a
  // logged-in visitor see the anonymous landing page, not even briefly.
  if (status === "loading" || user) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <span className="h-6 w-6 animate-spin rounded-full border-2 border-border border-t-accent" />
      </div>
    );
  }

  return (
    <div className="flex w-full flex-1 flex-col">
      <Navbar />
      <main className="flex-1">
        <Hero />
        <Features />
        <HowItWorks />
        <Security />
        <CTA />
      </main>
      <Footer />
    </div>
  );
}
