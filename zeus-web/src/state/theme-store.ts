// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
// See LICENSE for the full GPL text.

import { create } from 'zustand';
import { fetchThemeSettings, updateThemeSettings } from '../api/themeSettings';

// Theme + per-token colour overrides for the Zeus UI.
//
// `theme` flips the global `data-theme` attribute on <html>, which selects
// either the default dark token set in tokens.css or the brushed-silver
// LIGHT overlay (`:root[data-theme="light"]`). The operator can additionally
// override individual CSS variables — e.g. nudge --accent from electric blue
// to teal — and those overrides apply across BOTH themes because they're
// injected via a runtime <style> tag that sets `:root { … }` after the
// theme-block in the stylesheet.
//
// Persistence is two-layered:
//   - localStorage acts as a fast-paint cache so the theme attribute lands
//     on <html> before the first paint (no HTTP wait, no light/dark flash).
//   - LiteDB (server) is the source of truth across devices — `hydrate()`
//     pulls it on mount and reconciles, and every mutation fires a
//     debounced PUT so a single setting follows the operator from desktop
//     to tablet without a per-browser reset.

export type ThemeId = 'dark' | 'light';

// Tokens we expose in the operator-facing colour-tweak UI. Two groups:
//   - ACCENT tokens — the "feel" tokens an operator notices: accent for
//     active controls, signal chain colours, warm halos on meters.
//   - SURFACE tokens — chassis / panel / line / text. These were historically
//     hidden because flipping one surface in isolation can break contrast
//     (e.g. a dark --bg-0 with light --fg-0 already painted by the theme).
//     They're exposed now because Brian asked for per-shack chassis tuning;
//     the panel renders them in a separate group with a warning that they
//     can break contrast if pushed too far from the theme defaults.
// Add a token here + a label in TOKEN_META if you want a new picker row.
export type TweakableToken =
  // Accent group
  | '--accent'
  | '--accent-bright'
  | '--tx'
  | '--power'
  | '--amber'
  | '--cyan'
  | '--ok'
  | '--orange'
  // Surface group — chassis
  | '--bg-0'
  | '--bg-1'
  | '--bg-2'
  | '--bg-3'
  // Surface group — line / edge
  | '--line'
  | '--line-soft'
  | '--line-strong'
  // Surface group — text
  | '--fg-0'
  | '--fg-1'
  | '--fg-2';

export const TWEAKABLE_TOKENS: ReadonlyArray<TweakableToken> = [
  '--accent',
  '--accent-bright',
  '--tx',
  '--power',
  '--amber',
  '--cyan',
  '--ok',
  '--orange',
  '--bg-0',
  '--bg-1',
  '--bg-2',
  '--bg-3',
  '--line',
  '--line-soft',
  '--line-strong',
  '--fg-0',
  '--fg-1',
  '--fg-2',
];

type ThemeState = {
  theme: ThemeId;
  // Map of CSS-variable-name → hex colour (e.g. `--accent` → `#FF00FF`).
  // Only entries present here override the stylesheet default; deleting a
  // key restores the original token value from tokens.css.
  overrides: Partial<Record<TweakableToken, string>>;
  // True once `hydrate()` has reconciled local cache with the server. Stays
  // false on offline / first-launch failures so we don't keep flapping.
  hydrated: boolean;
  setTheme: (t: ThemeId) => void;
  setOverride: (token: TweakableToken, hex: string | null) => void;
  resetOverrides: () => void;
  // Pull the authoritative copy from /api/theme-settings and apply it if it
  // differs from the localStorage fast-paint cache. Called once by
  // ThemeApplier on mount; safe to invoke repeatedly.
  hydrate: () => Promise<void>;
};

const THEME_KEY = 'zeus.theme';
const OVERRIDES_KEY = 'zeus.theme.overrides';

function isThemeId(v: unknown): v is ThemeId {
  return v === 'dark' || v === 'light';
}

function isHexColor(v: unknown): v is string {
  return typeof v === 'string' && /^#[0-9A-Fa-f]{6}$/.test(v);
}

function readTheme(): ThemeId {
  try {
    if (typeof localStorage === 'undefined') return 'dark';
    const raw = localStorage.getItem(THEME_KEY);
    return isThemeId(raw) ? raw : 'dark';
  } catch {
    return 'dark';
  }
}

function writeTheme(t: ThemeId): void {
  try {
    if (typeof localStorage !== 'undefined') localStorage.setItem(THEME_KEY, t);
  } catch {
    /* quota / private mode — accept silently */
  }
}

function readOverrides(): Partial<Record<TweakableToken, string>> {
  try {
    if (typeof localStorage === 'undefined') return {};
    const raw = localStorage.getItem(OVERRIDES_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as Partial<Record<string, unknown>>;
    const out: Partial<Record<TweakableToken, string>> = {};
    for (const k of TWEAKABLE_TOKENS) {
      const v = parsed[k];
      if (isHexColor(v)) out[k] = v.toUpperCase();
    }
    return out;
  } catch {
    return {};
  }
}

function writeOverrides(o: Partial<Record<TweakableToken, string>>): void {
  try {
    if (typeof localStorage !== 'undefined')
      localStorage.setItem(OVERRIDES_KEY, JSON.stringify(o));
  } catch {
    /* quota / private mode — accept silently */
  }
}

// Debounced PUT to /api/theme-settings — collapses the colour-picker drag
// stream into ~one write every 300 ms while still flushing the final value.
// Fire-and-forget; persistence failures are logged but never block the UI.
let pushTimer: ReturnType<typeof setTimeout> | null = null;
function schedulePush(state: { theme: ThemeId; overrides: Partial<Record<TweakableToken, string>> }) {
  if (pushTimer) clearTimeout(pushTimer);
  pushTimer = setTimeout(() => {
    pushTimer = null;
    updateThemeSettings({ theme: state.theme, overrides: state.overrides as Record<string, string> })
      .catch((err) => {
        // Server unreachable / 5xx — keep the local change, retry on next mutation.
        // Cast to keep this file lint-clean without pulling a logger.
        // eslint-disable-next-line no-console
        console.warn('theme-store: PUT /api/theme-settings failed', err);
      });
  }, 300);
}

export const useThemeStore = create<ThemeState>((set, get) => ({
  theme: readTheme(),
  overrides: readOverrides(),
  hydrated: false,
  setTheme: (theme) => {
    writeTheme(theme);
    set({ theme });
    schedulePush({ theme, overrides: get().overrides });
  },
  setOverride: (token, hex) => {
    const next = { ...get().overrides };
    if (hex == null) {
      delete next[token];
    } else {
      if (!isHexColor(hex)) return;
      next[token] = hex.toUpperCase();
    }
    writeOverrides(next);
    set({ overrides: next });
    schedulePush({ theme: get().theme, overrides: next });
  },
  resetOverrides: () => {
    writeOverrides({});
    set({ overrides: {} });
    schedulePush({ theme: get().theme, overrides: {} });
  },
  hydrate: async () => {
    if (get().hydrated) return;
    try {
      const server = await fetchThemeSettings();
      // Project the server overrides into the typed TweakableToken keyspace —
      // anything the client doesn't know about is dropped (forward-compat:
      // newer server, older client). Same shape readOverrides() returns.
      const serverOverrides: Partial<Record<TweakableToken, string>> = {};
      for (const k of TWEAKABLE_TOKENS) {
        const v = server.overrides[k];
        if (isHexColor(v)) serverOverrides[k] = v.toUpperCase();
      }
      // Apply server values to local state + cache. If they match what we
      // already had, this is a no-op — but we mark hydrated either way so
      // subsequent mutations push through schedulePush.
      writeTheme(server.theme);
      writeOverrides(serverOverrides);
      set({ theme: server.theme, overrides: serverOverrides, hydrated: true });
    } catch (err) {
      // Backend unreachable — stay on the localStorage cache. Mark hydrated
      // so we don't loop forever; the next mutation will attempt a PUT and
      // recover the link. Surface for debugging.
      // eslint-disable-next-line no-console
      console.warn('theme-store: hydrate failed, using localStorage cache', err);
      set({ hydrated: true });
    }
  },
}));
