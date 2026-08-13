import Link from "next/link";
import FuzzyText from "./components/FuzzyText";

export default function NotFound() {
  return (
    <main className="relative flex min-h-screen items-center justify-center overflow-hidden bg-black px-6 text-white">
      {/* Subtle background grid */}
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 opacity-[0.04]"
        style={{
          backgroundImage:
            "linear-gradient(to right, #fff 1px, transparent 1px), linear-gradient(to bottom, #fff 1px, transparent 1px)",
          backgroundSize: "48px 48px",
        }}
      />

      {/* Subtle glow */}
      <div
        aria-hidden="true"
        className="pointer-events-none absolute left-1/2 top-1/2 h-[500px] w-[500px] -translate-x-1/2 -translate-y-1/2 rounded-full bg-white/[0.025] blur-3xl"
      />

      <div className="relative z-10 flex max-w-3xl flex-col items-center text-center">
        {/* Logo */}
        <Link
          href="/"
          className="mb-16 flex items-center gap-2.5 transition-opacity hover:opacity-70"
        >
        
        </Link>

        {/* Fuzzy 404 */}
        <div className="mb-8 flex items-center justify-center">
          <FuzzyText
            fontSize="clamp(7rem, 22vw, 16rem)"
            fontWeight={900}
            color="#ffffff"
            baseIntensity={0.5}
            hoverIntensity={1}
            fuzzRange={25}
            fps={60}
            direction="horizontal"
            transitionDuration={12}
            enableHover
          >
            404
          </FuzzyText>
        </div>

        {/* Text */}
        <div className="max-w-xl">
          <p className="mb-3 text-xs font-semibold uppercase tracking-[0.2em] text-white/40">
            Page not found
          </p>

          <h1 className="text-3xl font-semibold tracking-tight text-white sm:text-4xl">
            This page doesn&apos;t exist.
          </h1>

          <p className="mx-auto mt-4 max-w-md text-sm leading-6 text-white/50 sm:text-base">
            The page you&apos;re looking for may have been moved,
            deleted, or never existed.
          </p>
        </div>

        {/* Actions */}
        <div className="mt-9 flex flex-col items-center gap-3 sm:flex-row">
          <Link
            href="/"
            className="group inline-flex h-11 items-center gap-2 rounded-full bg-white px-6 text-sm font-medium text-black transition-all duration-200 hover:-translate-y-0.5 hover:shadow-[0_8px_30px_rgba(255,255,255,0.15)]"
          >
            Back to home

            <span className="transition-transform duration-200 group-hover:translate-x-0.5">
              →
            </span>
          </Link>

          <Link
            href="/product"
            className="inline-flex h-11 items-center rounded-full border border-white/15 px-6 text-sm font-medium text-white/70 transition-colors hover:border-white/30 hover:bg-white/5 hover:text-white"
          >
            Explore Nivora
          </Link>
        </div>
      </div>
    </main>
  );
}