import "@testing-library/jest-dom/vitest";

// jsdom under this Node version doesn't wire up window.localStorage/
// sessionStorage by default (Node's own experimental localStorage warns
// instead of filling the gap) — components like Sidebar.tsx and
// SessionExpiryModal.tsx read them unconditionally, so tests that render
// those need a minimal in-memory stand-in for each.
function createMemoryStorage(): Storage {
  const store = new Map<string, string>();
  return {
    getItem: (key: string) => (store.has(key) ? store.get(key)! : null),
    setItem: (key: string, value: string) => {
      store.set(key, String(value));
    },
    removeItem: (key: string) => {
      store.delete(key);
    },
    clear: () => store.clear(),
    key: (index: number) => Array.from(store.keys())[index] ?? null,
    get length() {
      return store.size;
    },
  };
}

if (typeof window !== "undefined") {
  if (!window.localStorage) {
    Object.defineProperty(window, "localStorage", { value: createMemoryStorage(), writable: true });
  }
  if (!window.sessionStorage) {
    Object.defineProperty(window, "sessionStorage", { value: createMemoryStorage(), writable: true });
  }
}
