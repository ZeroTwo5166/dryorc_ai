import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { api, ApiError, extendSession, setUnauthorizedHandler } from "./api";

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("api client — centralized 401 handling", () => {
  let unauthorizedHandler: ReturnType<typeof vi.fn<() => void>>;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    unauthorizedHandler = vi.fn<() => void>();
    setUnauthorizedHandler(unauthorizedHandler);
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    setUnauthorizedHandler(null);
    vi.unstubAllGlobals();
  });

  it("returns the parsed body normally on a 200 response", async () => {
    fetchMock.mockResolvedValue(jsonResponse(200, { id: "abc123" }));

    const result = await api.get("/api/customers");

    expect(result).toEqual({ id: "abc123" });
    expect(unauthorizedHandler).not.toHaveBeenCalled();
  });

  it("calls the centralized handler on a genuine 401", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    await expect(api.get("/api/customers")).rejects.toThrow(ApiError);

    expect(unauthorizedHandler).toHaveBeenCalledTimes(1);
  });

  it("does NOT call the centralized handler on a 500", async () => {
    fetchMock.mockResolvedValue(new Response("Internal error", { status: 500 }));

    await expect(api.get("/api/customers")).rejects.toThrow(ApiError);

    expect(unauthorizedHandler).not.toHaveBeenCalled();
  });

  it("does NOT call the centralized handler on a network failure", async () => {
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    await expect(api.get("/api/customers")).rejects.toThrow();

    expect(unauthorizedHandler).not.toHaveBeenCalled();
  });

  it("does not repeatedly retry the request when it 401s (api.post)", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    await expect(api.post("/api/messages", { text: "hi" })).rejects.toThrow(ApiError);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(unauthorizedHandler).toHaveBeenCalledTimes(1);
  });

  describe("extendSession", () => {
    it("resolves normally on success and does not trigger the centralized handler", async () => {
      fetchMock.mockResolvedValue(jsonResponse(200, { expiresAtUtc: "2030-01-01T00:00:00Z" }));

      const result = await extendSession();

      expect(result).toEqual({ expiresAtUtc: "2030-01-01T00:00:00Z" });
      expect(unauthorizedHandler).not.toHaveBeenCalled();
    });

    // Requirement: an already-expired session trying to extend gets a 401
    // and follows the normal centralized logout path — without the
    // frontend looping by automatically retrying the extend call itself.
    it("on an already-expired session, 401s once, triggers the centralized handler, and does not retry", async () => {
      fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

      await expect(extendSession()).rejects.toThrow(ApiError);

      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(unauthorizedHandler).toHaveBeenCalledTimes(1);
    });
  });
});
