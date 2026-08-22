import { describe, it, expect } from "vitest";
import { formatRelativeTime } from "./relative-time";

describe("formatRelativeTime", () => {
  const now = new Date("2026-08-22T12:00:00.000Z");

  it("returns 'Just now' for anything under a minute old", () => {
    expect(formatRelativeTime("2026-08-22T11:59:31.000Z", now)).toBe("Just now");
  });

  it("formats minutes ago", () => {
    expect(formatRelativeTime("2026-08-22T11:58:00.000Z", now)).toBe("2 minutes ago");
  });

  it("formats hours ago", () => {
    expect(formatRelativeTime("2026-08-22T09:00:00.000Z", now)).toBe("3 hours ago");
  });

  it("formats days ago", () => {
    expect(formatRelativeTime("2026-08-19T12:00:00.000Z", now)).toBe("3 days ago");
  });

  it("formats yesterday using Intl's natural phrasing", () => {
    expect(formatRelativeTime("2026-08-21T12:00:00.000Z", now)).toBe("yesterday");
  });

  it("formats weeks ago", () => {
    expect(formatRelativeTime("2026-08-01T12:00:00.000Z", now)).toBe("3 weeks ago");
  });

  it("is timezone-safe: an ISO timestamp with a non-UTC offset compares correctly", () => {
    // 10:00 in UTC+2 is 08:00 UTC — 4 hours before `now` (12:00 UTC).
    expect(formatRelativeTime("2026-08-22T10:00:00.000+02:00", now)).toBe("4 hours ago");
  });
});
