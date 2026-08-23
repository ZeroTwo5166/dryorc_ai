import { describe, it, expect, beforeEach } from "vitest";
import { loadState, saveState, clearState, type WidgetState } from "../src/state";

const PUBLIC_KEY = "abc123";

describe("state", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("returns null when there is no existing cached state", () => {
    expect(loadState(PUBLIC_KEY)).toBeNull();
  });

  it("round-trips a saved state through loadState", () => {
    const state: WidgetState = {
      visitorSessionId: "visitor-1",
      token: "jwt-token-value",
      transcript: [
        { id: "m1", role: "customer", content: "hi", createdAt: "2026-01-01T00:00:00Z" },
        { id: "m2", role: "ai", content: "hello!", createdAt: "2026-01-01T00:00:01Z" },
      ],
    };

    saveState(PUBLIC_KEY, state);
    const loaded = loadState(PUBLIC_KEY);

    expect(loaded).toEqual(state);
  });

  it("persists VisitorSessionId specifically", () => {
    saveState(PUBLIC_KEY, { visitorSessionId: "visitor-xyz", token: "t", transcript: [] });
    expect(loadState(PUBLIC_KEY)?.visitorSessionId).toBe("visitor-xyz");
  });

  it("persists the token specifically", () => {
    saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "token-xyz", transcript: [] });
    expect(loadState(PUBLIC_KEY)?.token).toBe("token-xyz");
  });

  it("persists the full transcript specifically", () => {
    const transcript = [
      { id: "m1", role: "customer" as const, content: "one", createdAt: "2026-01-01T00:00:00Z" },
      { id: "m2", role: "ai" as const, content: "two", createdAt: "2026-01-01T00:00:01Z" },
      { id: "m3", role: "customer" as const, content: "three", createdAt: "2026-01-01T00:00:02Z" },
    ];
    saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "t", transcript });
    expect(loadState(PUBLIC_KEY)?.transcript).toEqual(transcript);
  });

  it("scopes state independently per PublicKey", () => {
    saveState("key-a", { visitorSessionId: "a", token: "ta", transcript: [] });
    saveState("key-b", { visitorSessionId: "b", token: "tb", transcript: [] });

    expect(loadState("key-a")?.visitorSessionId).toBe("a");
    expect(loadState("key-b")?.visitorSessionId).toBe("b");
  });

  it("returns null for unparseable JSON rather than throwing", () => {
    window.localStorage.setItem(`nivora_widget_${PUBLIC_KEY}`, "{not valid json");
    expect(() => loadState(PUBLIC_KEY)).not.toThrow();
    expect(loadState(PUBLIC_KEY)).toBeNull();
  });

  it("returns null when required fields are missing", () => {
    window.localStorage.setItem(`nivora_widget_${PUBLIC_KEY}`, JSON.stringify({ transcript: [] }));
    expect(loadState(PUBLIC_KEY)).toBeNull();
  });

  it("returns null when a field has the wrong type", () => {
    window.localStorage.setItem(
      `nivora_widget_${PUBLIC_KEY}`,
      JSON.stringify({ visitorSessionId: 123, token: "t", transcript: [] }),
    );
    expect(loadState(PUBLIC_KEY)).toBeNull();
  });

  it("drops individual malformed transcript entries instead of rejecting the whole cached session", () => {
    window.localStorage.setItem(
      `nivora_widget_${PUBLIC_KEY}`,
      JSON.stringify({
        visitorSessionId: "v",
        token: "t",
        transcript: [
          { id: "m1", role: "customer", content: "valid", createdAt: "2026-01-01T00:00:00Z" },
          { id: "m2", role: "not-a-real-role", content: "bad", createdAt: "2026-01-01T00:00:01Z" },
          "not even an object",
          { id: "m3", role: "ai", content: "also valid", createdAt: "2026-01-01T00:00:02Z" },
        ],
      }),
    );

    const loaded = loadState(PUBLIC_KEY);
    expect(loaded).not.toBeNull();
    expect(loaded!.transcript).toHaveLength(2);
    expect(loaded!.transcript.map((m) => m.id)).toEqual(["m1", "m3"]);
  });

  it("round-trips a customer message's status field", () => {
    const transcript = [
      { id: "m1", role: "customer" as const, content: "hi", createdAt: "2026-01-01T00:00:00Z", status: "failed" as const },
    ];
    saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "t", transcript });
    expect(loadState(PUBLIC_KEY)?.transcript[0].status).toBe("failed");
  });

  it("drops a transcript entry with an invalid status value rather than accepting it", () => {
    window.localStorage.setItem(
      `nivora_widget_${PUBLIC_KEY}`,
      JSON.stringify({
        visitorSessionId: "v",
        token: "t",
        transcript: [
          { id: "m1", role: "customer", content: "bad status", createdAt: "2026-01-01T00:00:00Z", status: "delivered" },
          { id: "m2", role: "customer", content: "good", createdAt: "2026-01-01T00:00:01Z", status: "sent" },
        ],
      }),
    );

    const loaded = loadState(PUBLIC_KEY);
    expect(loaded!.transcript).toHaveLength(1);
    expect(loaded!.transcript[0].id).toBe("m2");
  });

  it("accepts a message with no status field at all (AI messages never carry one)", () => {
    const transcript = [{ id: "m1", role: "ai" as const, content: "hello", createdAt: "2026-01-01T00:00:00Z" }];
    saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "t", transcript });
    expect(loadState(PUBLIC_KEY)?.transcript[0].status).toBeUndefined();
  });

  it("clearState removes a stale/cached session entirely", () => {
    saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "t", transcript: [] });
    expect(loadState(PUBLIC_KEY)).not.toBeNull();

    clearState(PUBLIC_KEY);

    expect(loadState(PUBLIC_KEY)).toBeNull();
  });

  it("does not throw when localStorage.setItem fails (e.g. quota exceeded)", () => {
    const original = window.localStorage.setItem;
    window.localStorage.setItem = () => {
      throw new Error("QuotaExceededError");
    };

    try {
      expect(() => saveState(PUBLIC_KEY, { visitorSessionId: "v", token: "t", transcript: [] })).not.toThrow();
    } finally {
      window.localStorage.setItem = original;
    }
  });
});
