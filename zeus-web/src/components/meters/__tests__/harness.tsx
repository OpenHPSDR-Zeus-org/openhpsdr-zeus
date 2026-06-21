// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Minimal, dependency-free test harness for the Meters Panel tests. The
// project does NOT take a dependency on @testing-library/react in the
// package.json, so we roll the bare-minimum renderHook + render + act here
// using react-dom/client directly. Scope is intentionally small —
// just enough to drive the four Meters Panel test files.

// jsdom's localStorage emits a "--localstorage-file was provided without a
// valid path" warning when invoked without a backing file, which leaves
// us with a Storage stub whose `setItem` is missing — that breaks Zustand's
// persist middleware on module-load (it captures a reference to
// localStorage via `createJSONStorage(() => localStorage)`). Install a
// dependable in-memory polyfill BEFORE any store module is imported. Test
// files import `./harness` before their store imports, so this side-effect
// runs first.
(() => {
  if (typeof globalThis === 'undefined') return;
  const g = globalThis as unknown as { localStorage?: Storage };
  if (g.localStorage && typeof g.localStorage.setItem === 'function') {
    try {
      g.localStorage.setItem('__zeus-test', '1');
      g.localStorage.removeItem('__zeus-test');
      return;
    } catch {
      // fall through and install the polyfill
    }
  }
  const store = new Map<string, string>();
  const polyfill: Storage = {
    get length() {
      return store.size;
    },
    clear() {
      store.clear();
    },
    getItem(key) {
      return store.has(key) ? (store.get(key) as string) : null;
    },
    key(index) {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key) {
      store.delete(key);
    },
    setItem(key, value) {
      store.set(key, String(value));
    },
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: polyfill,
  });
})();

// React reads this global to decide whether `act()` is allowed; without it
// every act() call logs a "not configured" warning even when the test is
// otherwise green. Set it before importing react-dom/client to be safe.
(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT?: boolean })
  .IS_REACT_ACT_ENVIRONMENT = true;

import { act as reactAct } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { createElement, type ReactElement } from 'react';

export interface RenderHookResult<T> {
  result: { current: T };
  rerender: () => void;
  unmount: () => void;
  container: HTMLElement;
}

/** Render a hook and expose its return value through `result.current`. */
export function renderHook<T>(callback: () => T): RenderHookResult<T> {
  const result = { current: undefined as unknown as T };
  const container = document.createElement('div');
  document.body.appendChild(container);
  let root: Root;

  function HookProbe() {
    const value = callback();
    // Capture synchronously during render so consumers reading
    // result.current after `act` see the latest value. No useEffect —
    // we don't want a post-render state-update wave that re-triggers act
    // warnings outside the test's act() boundary.
    result.current = value;
    return null;
  }

  reactAct(() => {
    root = createRoot(container);
    root.render(createElement(HookProbe));
  });

  return {
    result,
    rerender: () =>
      reactAct(() => {
        root.render(createElement(HookProbe));
      }),
    unmount: () => {
      reactAct(() => {
        root.unmount();
      });
      container.remove();
    },
    get container() {
      return container;
    },
  };
}

export interface RenderResult {
  container: HTMLElement;
  rerender: (element: ReactElement) => void;
  unmount: () => void;
}

/** Render a React element into the document and return the container. */
export function render(element: ReactElement): RenderResult {
  const container = document.createElement('div');
  document.body.appendChild(container);
  let root: Root;
  reactAct(() => {
    root = createRoot(container);
    root.render(element);
  });
  return {
    container,
    rerender: (next) =>
      reactAct(() => {
        root.render(next);
      }),
    unmount: () => {
      reactAct(() => {
        root.unmount();
      });
      container.remove();
    },
  };
}

/** Re-export React's act so callers can wrap user-event-style mutations. */
export const act = reactAct;
