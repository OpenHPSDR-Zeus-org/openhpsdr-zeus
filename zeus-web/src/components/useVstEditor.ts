// SPDX-License-Identifier: GPL-2.0-or-later
//
// useVstEditor — shared open/close state for a hosted VST3's native
// editor window. A VST3's real GUI is a native OS window opened by the
// in-process bridge (POST opens, DELETE closes, GET reports state); the
// host can't render it inside the browser. This hook is the single
// source of truth for that toggle, used both by the rack slot header
// (click the VST to open its window) and the GenericVstPanel fallback.

import { useCallback, useEffect, useState } from 'react';

export interface VstEditorState {
  /** True while the plugin's native editor window is up. */
  open: boolean;
  /** True while an open/close request is in flight. */
  busy: boolean;
  /** Last failed request's message (e.g. the VST didn't load), else null. */
  error: string | null;
  /** Open the editor if closed, close it if open. No-op while busy. */
  toggle(): void;
  /** Open the editor (no-op if already open or busy). */
  openEditor(): void;
}

/**
 * @param pluginId  The plugin id whose editor to drive.
 * @param enabled   When false the hook stays inert (no mount fetch, no
 *                  requests) — lets a generic list render it for every
 *                  slot while only VST slots actually talk to the bridge.
 */
export function useVstEditor(pluginId: string, enabled = true): VstEditorState {
  const base = `/api/audio-suite/plugins/${encodeURIComponent(pluginId)}/editor`;
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reflect the actual editor state on mount — the native window may
  // already be open from a previous interaction (state lives server-side).
  useEffect(() => {
    if (!enabled) return;
    let alive = true;
    fetch(base)
      .then((r) => (r.ok ? r.json() : null))
      .then((j) => {
        if (alive && j && typeof j.open === 'boolean') setOpen(j.open);
      })
      .catch(() => {
        /* transient — leave state as-is */
      });
    return () => {
      alive = false;
    };
  }, [base, enabled]);

  const request = useCallback(
    async (wantOpen: boolean) => {
      setBusy(true);
      setError(null);
      try {
        const res = await fetch(base, { method: wantOpen ? 'POST' : 'DELETE' });
        const body = await res.json().catch(() => null);
        if (res.ok) {
          setOpen(body && typeof body.open === 'boolean' ? body.open : wantOpen);
        } else {
          setError(body?.error ?? `Request failed (${res.status})`);
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Request failed');
      } finally {
        setBusy(false);
      }
    },
    [base],
  );

  const toggle = useCallback(() => {
    if (busy) return;
    void request(!open);
  }, [busy, open, request]);

  const openEditor = useCallback(() => {
    if (busy || open) return;
    void request(true);
  }, [busy, open, request]);

  return { open, busy, error, toggle, openEditor };
}
