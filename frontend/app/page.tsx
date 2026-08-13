import { CTA } from "./components/landing/CTA";
import { Features } from "./components/landing/Features";
import { Footer } from "./components/landing/Footer";
import { Hero } from "./components/landing/Hero";
import { HowItWorks } from "./components/landing/HowItWorks";
import { Navbar } from "./components/landing/Navbar";
import { Security } from "./components/landing/Security";

export default function Home() {
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