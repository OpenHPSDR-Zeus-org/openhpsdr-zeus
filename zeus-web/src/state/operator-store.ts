// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Operator identity (own callsign + Maidenhead grid) for the digital modes.
// Used by the FT8/FT4/WSPR workspaces for the "calling me" decode highlight and
// click-to-call message generation, and by the spotting uploaders / FreeDV
// Reporter.
//
// SERVER-AUTHORITATIVE (PR: FT8 TX-ready). Identity is persisted in zeus-prefs.db
// via /api/operator and is the SAME override every backend resolver reads first
// (FT8/FT4 TX, PSK Reporter, WSPRnet, FreeDV Reporter), with the QRZ home station
// as the fallback. The old localStorage system-of-record was scoped to the
// desktop webview's loopback port, which the OS reassigns each launch, so the
// operator's call was silently lost on every restart and FT8 TX stayed gated.
//
//  - `call` / `grid`       — the saved OVERRIDE (what the operator typed; bound
//                            to the Settings inputs and the spotting prefill).
//  - `resolvedCall` / `…`  — the EFFECTIVE identity (override else QRZ home) the
//                            TX path and gating use, so a QRZ-home operator can
//                            transmit without retyping.

import { create } from 'zustand';
import { getOperator, postOperator, type OperatorIdentityStatus } from '../api/operator';

interface OperatorState {
  /** Saved override callsign (empty when unset). */
  call: string;
  /** Saved override grid (empty when unset). */
  grid: string;
  /** Effective callsign: override else QRZ home. */
  resolvedCall: string;
  /** Effective grid: override else QRZ home. */
  resolvedGrid: string;
  /** True when the resolved field fell back to the QRZ home station. */
  callFromQrz: boolean;
  gridFromQrz: boolean;
  /** True once the first server hydrate has completed (success or failure). */
  hydrated: boolean;

  /** Load the saved + resolved identity from the server. Safe to call repeatedly
   *  (e.g. on connect and on workspace open — QRZ home may resolve late). */
  hydrate: (signal?: AbortSignal) => Promise<void>;
  /** Set the override callsign and persist it to the server. */
  setCall: (call: string) => void;
  /** Set the override grid and persist it to the server. */
  setGrid: (grid: string) => void;
  /** Persist both override fields at once. */
  save: (identity: { call: string; grid: string }) => Promise<void>;
}

/** Full reconcile: server is the source of truth for BOTH the saved override and
 *  the resolved identity. Used on hydrate and on an explicit save() commit. */
function applyStatus(status: OperatorIdentityStatus): Partial<OperatorState> {
  return {
    call: status.callsign,
    grid: status.grid,
    ...applyResolved(status),
  };
}

/** Resolved-only reconcile: apply the effective/QRZ fields WITHOUT touching the
 *  override the operator is actively typing. This is the keystroke-edit path —
 *  echoing the server-normalized override back into the bound input is what made
 *  the Grid field unusable (the server returns "" for a partial Maidenhead
 *  prefix, so every early character wiped itself before the next was typed). */
function applyResolved(status: OperatorIdentityStatus): Partial<OperatorState> {
  return {
    resolvedCall: status.resolvedCallsign,
    resolvedGrid: status.resolvedGrid,
    callFromQrz: status.callsignFromQrz,
    gridFromQrz: status.gridFromQrz,
    hydrated: true,
  };
}

export const useOperatorStore = create<OperatorState>((set, get) => {
  // Monotonic id so an out-of-order POST response (multiple keystrokes in flight)
  // can never clobber a newer one's reconcile.
  let saveSeq = 0;

  /** Persist the current override from a keystroke edit. Optimistic local value
   *  is already set; only the resolved/QRZ fields are reconciled from the server
   *  so the edit buffer the operator is typing is never overwritten mid-word. */
  const persistOverride = async () => {
    const seq = ++saveSeq;
    try {
      const status = await postOperator({ callsign: get().call, grid: get().grid });
      if (seq !== saveSeq) return; // a newer keystroke superseded this write
      set(applyResolved(status));
    } catch {
      // Offline — the optimistic local value stands; a later hydrate reconciles.
    }
  };

  return {
    call: '',
    grid: '',
    resolvedCall: '',
    resolvedGrid: '',
    callFromQrz: false,
    gridFromQrz: false,
    hydrated: false,

    hydrate: async (signal) => {
      try {
        const status = await getOperator(signal);
        set(applyStatus(status));
      } catch {
        // Transient (not connected yet / offline) — keep current values and let a
        // later hydrate recover. Mark hydrated so the UI stops showing a spinner.
        set({ hydrated: true });
      }
    },

    setCall: (call) => {
      set({ call: call.toUpperCase().trim() }); // optimistic so the input is responsive
      void persistOverride();
    },

    setGrid: (grid) => {
      set({ grid: grid.toUpperCase().trim() });
      void persistOverride();
    },

    save: async ({ call, grid }) => {
      const seq = ++saveSeq;
      try {
        const status = await postOperator({ callsign: call, grid });
        if (seq !== saveSeq) return;
        set(applyStatus(status));
      } catch {
        // Persist failed (offline) — the optimistic local value stands; a later
        // hydrate/save reconciles. Never throw into the UI for an identity edit.
      }
    },
  };
});

// One-shot hydrate on module load (mirrors wsjtx-store / spotting-store). Guarded
// so test environments and pre-connect loads never throw — hydrate swallows its
// own errors.
if (typeof window !== 'undefined') {
  void useOperatorStore.getState().hydrate();
}
