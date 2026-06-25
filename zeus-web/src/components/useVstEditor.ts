// SPDX-License-Identifier: GPL-2.0-or-later
//
// useVstEditor — shared open/close state for a hosted VST3's native
// editor window. A VST3's real GUI is a native OS window opened by the
// in-process bridge (POST opens, DELETE closes, GET reports state); the
// host can't render it inside the browser. This hook is the single
// source of truth for that toggle, used both by the rack slot header
// (click the VST to open its window) and the GenericVstPanel fallback.

import { useCallback, useEffect, useRef, useState } from 'react';

export type VstEditorRoute = 'tx' | 'rx';

export interface VstEditorState {
  /** True while the plugin's native editor window is up. */
  open: boolean;
  /** True while an open/close request is in flight. */
  busy: boolean;
  /**
   * True while we're waiting for the out-of-process VST engine to finish
   * starting before (re)trying the open. Lets the UI show "engine starting…"
   * instead of a failure during the fresh-install cold-start window.
   */
  starting: boolean;
  /**
   * True when the open failed because the engine is installed but keeps
   * crashing on startup (Faulted), not merely warming up. The UI surfaces a
   * "Repair engine" action in this case; the server also auto-repairs once.
   */
  crashed: boolean;
  /** Last failed request's message (e.g. the VST didn't load), else null. */
  error: string | null;
  /** Open the editor if closed, close it if open. No-op while busy. */
  toggle(): void;
  /** Open the editor (no-op if already open or busy). */
  openEditor(): void;
}

// On a fresh install the engine binary is downloaded and then cold-started for
// the first time (Defender/SmartScreen scan + JUCE/VST init), which can take a
// few seconds — longer than the editor-open click that races it. Poll the
// processing-mode for the engine to come up, then retry the open once.
const ENGINE_START_POLLS = 20;
const ENGINE_START_INTERVAL_MS = 1000;

type EngineWaitResult = 'active' | 'crash' | 'gaveup';

/**
 * Wait for the out-of-process VST engine to report active, polling the route's
 * processing-mode. Returns 'active' once it's routing; 'crash' if the engine is
 * installed but crash-looping (Faulted — the caller offers Repair); 'gaveup' if
 * it never comes up within the budget, the hook unmounted, or the route is no
 * longer in VST mode (a Native-mode 409 means "switch to VST mode" — there is
 * no engine to wait for, so the original error should surface instead).
 */
async function waitForEngineActive(
  routePrefix: string,
  alive: () => boolean,
  onStarting: () => void,
): Promise<EngineWaitResult> {
  for (let i = 0; i < ENGINE_START_POLLS; i += 1) {
    if (!alive()) return 'gaveup';
    let body:
      | { mode?: string; engineActive?: boolean; engineCrashLooping?: boolean }
      | null = null;
    try {
      const res = await fetch(`${routePrefix}/processing-mode`);
      body = res.ok ? await res.json() : null;
    } catch {
      body = null;
    }
    // Not in VST mode → the engine won't activate; nothing to wait for.
    if (body && body.mode !== 'vst') return 'gaveup';
    if (body?.engineActive === true) return 'active';
    // Installed but Faulted → waiting won't help; surface a repair path.
    if (body?.engineCrashLooping === true) return 'crash';
    onStarting();
    await new Promise((r) => setTimeout(r, ENGINE_START_INTERVAL_MS));
  }
  return 'gaveup';
}

/**
 * @param pluginId  The plugin id whose editor to drive.
 * @param enabled   When false the hook stays inert (no mount fetch, no
 *                  requests) — lets a generic list render it for every
 *                  slot while only VST slots actually talk to the bridge.
 * @param route     Which audio-suite route owns this plugin instance.
 */
export function useVstEditor(
  pluginId: string,
  enabled = true,
  route: VstEditorRoute = 'tx',
): VstEditorState {
  const routePrefix = route === 'rx' ? '/api/rx-audio-suite' : '/api/tx-audio-suite';
  const base = `${routePrefix}/plugins/${encodeURIComponent(pluginId)}/editor`;
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [starting, setStarting] = useState(false);
  const [crashed, setCrashed] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Tracks mount state so the (potentially multi-second) engine-start wait
  // never writes to an unmounted hook or keeps polling after teardown.
  const aliveRef = useRef(true);
  useEffect(() => {
    aliveRef.current = true;
    return () => {
      aliveRef.current = false;
    };
  }, []);

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
      setStarting(false);
      setCrashed(false);
      try {
        let res = await fetch(base, { method: wantOpen ? 'POST' : 'DELETE' });
        let body = await res.json().catch(() => null);

        // Fresh-install cold-start race: the editor 409s with "isn't routing
        // yet" while the engine is still coming up. Instead of surfacing a
        // scary error and giving up, wait for the engine to finish starting and
        // retry the open once. Only meaningful for an open in VST mode —
        // waitForEngineActive bails immediately when the route isn't VST.
        if (!res.ok && res.status === 409 && wantOpen && aliveRef.current) {
          const outcome = await waitForEngineActive(
            routePrefix,
            () => aliveRef.current,
            () => {
              if (aliveRef.current) setStarting(true);
            },
          );
          if (outcome === 'active' && aliveRef.current) {
            res = await fetch(base, { method: 'POST' });
            body = await res.json().catch(() => null);
          } else if (outcome === 'crash' && aliveRef.current) {
            // Installed but crash-looping — waiting won't fix it. Surface the
            // repair path instead of the optimistic "give it a moment" hint.
            setCrashed(true);
            setError(
              'The VST engine is installed but keeps crashing on startup. ' +
                'Use Repair to re-download the verified engine.',
            );
            return;
          }
        }

        if (!aliveRef.current) return;
        if (res.ok) {
          setOpen(body && typeof body.open === 'boolean' ? body.open : wantOpen);
        } else {
          setError(body?.error ?? `Request failed (${res.status})`);
        }
      } catch (e) {
        if (aliveRef.current) {
          setError(e instanceof Error ? e.message : 'Request failed');
        }
      } finally {
        if (aliveRef.current) {
          setBusy(false);
          setStarting(false);
        }
      }
    },
    [base, routePrefix],
  );

  const toggle = useCallback(() => {
    if (busy) return;
    void request(!open);
  }, [busy, open, request]);

  const openEditor = useCallback(() => {
    if (busy || open) return;
    void request(true);
  }, [busy, open, request]);

  return { open, busy, starting, crashed, error, toggle, openEditor };
}
