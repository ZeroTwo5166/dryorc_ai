// Smallest appropriate relative-time formatter — uses the built-in
// Intl.RelativeTimeFormat rather than adding a date library dependency
// (none exists in this project). The API always returns UTC ISO 8601
// timestamps (Notification.CreatedAt); `new Date(iso)` parses that
// correctly regardless of the viewer's own timezone, and comparing two
// real Date instants (both internally UTC epoch millis) is timezone-safe
// without any extra handling.
const rtf = new Intl.RelativeTimeFormat("en", { numeric: "auto" });

const UNITS: { unit: Intl.RelativeTimeFormatUnit; seconds: number }[] = [
  { unit: "year", seconds: 31536000 },
  { unit: "month", seconds: 2592000 },
  { unit: "week", seconds: 604800 },
  { unit: "day", seconds: 86400 },
  { unit: "hour", seconds: 3600 },
  { unit: "minute", seconds: 60 },
];

// `now` is injectable for deterministic tests — defaults to the real clock
// for every actual call site.
export function formatRelativeTime(isoTimestamp: string, now: Date = new Date()): string {
  const diffSeconds = (new Date(isoTimestamp).getTime() - now.getTime()) / 1000; // negative = past

  if (Math.abs(diffSeconds) < 60) {
    return "Just now";
  }

  for (const { unit, seconds } of UNITS) {
    if (Math.abs(diffSeconds) >= seconds) {
      return rtf.format(Math.round(diffSeconds / seconds), unit);
    }
  }

  return rtf.format(Math.round(diffSeconds / 60), "minute");
}
