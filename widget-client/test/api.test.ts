import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { initSession, sendMessage, ApiError, NetworkError } from "../src/api";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("api", () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  describe("initSession", () => {
    it("POSTs to /api/widget/init with the public key", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }));

      await initSession("https://api.example.com", "pk_123");

      expect(fetchMock).toHaveBeenCalledWith(
        "https://api.example.com/api/widget/init",
        expect.objectContaining({ method: "POST" }),
      );
      const [, init] = fetchMock.mock.calls[0];
      const body = JSON.parse(init.body as string);
      expect(body.publicKey).toBe("pk_123");
    });

    it("omits VisitorSessionId (sends null) when not supplied", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }));

      await initSession("https://api.example.com", "pk_123");

      const [, init] = fetchMock.mock.calls[0];
      const body = JSON.parse(init.body as string);
      expect(body.visitorSessionId).toBeNull();
    });

    it("includes the cached VisitorSessionId when supplied", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }));

      await initSession("https://api.example.com", "pk_123", "existing-visitor");

      const [, init] = fetchMock.mock.calls[0];
      const body = JSON.parse(init.body as string);
      expect(body.visitorSessionId).toBe("existing-visitor");
    });

    it("resolves with the visitorSessionId and token from a successful response", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { visitorSessionId: "v-returned", token: "t-returned" }));

      const result = await initSession("https://api.example.com", "pk_123");

      expect(result).toEqual({ visitorSessionId: "v-returned", token: "t-returned" });
    });

    it("does not send an Authorization header", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { visitorSessionId: "v1", token: "t1" }));

      await initSession("https://api.example.com", "pk_123");

      const [, init] = fetchMock.mock.calls[0];
      expect((init.headers as Record<string, string>).Authorization).toBeUndefined();
    });
  });

  describe("sendMessage", () => {
    it("POSTs to /api/widget/messages with the Authorization header", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { id: "m1", createdAt: "2026-01-01T00:00:00Z", aiReply: "hi" }));

      await sendMessage("https://api.example.com", "the-token", "hello");

      expect(fetchMock).toHaveBeenCalledWith(
        "https://api.example.com/api/widget/messages",
        expect.objectContaining({ method: "POST" }),
      );
      const [, init] = fetchMock.mock.calls[0];
      expect((init.headers as Record<string, string>).Authorization).toBe("Bearer the-token");
      const body = JSON.parse(init.body as string);
      expect(body.content).toBe("hello");
    });

    it("resolves with id/createdAt/aiReply from a successful response", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { id: "m9", createdAt: "2026-02-02T00:00:00Z", aiReply: "reply text" }));

      const result = await sendMessage("https://api.example.com", "t", "hello");

      expect(result).toEqual({ id: "m9", createdAt: "2026-02-02T00:00:00Z", aiReply: "reply text" });
    });

    it("resolves with a null aiReply when Claude declined to reply", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { id: "m9", createdAt: "2026-02-02T00:00:00Z", aiReply: null }));

      const result = await sendMessage("https://api.example.com", "t", "hello");

      expect(result.aiReply).toBeNull();
    });
  });

  describe("error handling", () => {
    it.each([400, 401, 403, 429, 500, 503])(
      "throws ApiError with status %d for a non-2xx response",
      async (status) => {
        fetchMock.mockResolvedValue(new Response("", { status }));

        await expect(sendMessage("https://api.example.com", "t", "hello")).rejects.toMatchObject({
          name: "ApiError",
          status,
        });
      },
    );

    it("throws NetworkError, not ApiError, when fetch itself rejects", async () => {
      fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

      await expect(initSession("https://api.example.com", "pk_123")).rejects.toBeInstanceOf(NetworkError);
    });

    it("ApiError is an instance of Error and carries the status", async () => {
      fetchMock.mockResolvedValue(new Response("", { status: 429 }));

      try {
        await sendMessage("https://api.example.com", "t", "hello");
        expect.unreachable("expected sendMessage to throw");
      } catch (err) {
        expect(err).toBeInstanceOf(ApiError);
        expect(err).toBeInstanceOf(Error);
        expect((err as ApiError).status).toBe(429);
      }
    });
  });
});
