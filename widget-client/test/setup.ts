// jsdom under this Node version doesn't wire up window.localStorage by
// default (confirmed the same way for frontend/vitest.setup.ts) — state.ts
// reads/writes it unconditionally, so tests need a minimal in-memory
// stand-in.
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

if (typeof window !== "undefined" && !window.localStorage) {
  Object.defineProperty(window, "localStorage", { value: createMemoryStorage(), writable: true });
}
