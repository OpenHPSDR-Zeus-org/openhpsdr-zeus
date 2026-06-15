// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import { qrzLogin, qrzLogout, qrzLookup, qrzStatus, qrzSetApiKey, type QrzStation, type QrzStatus } from '../api/qrz';
import { ApiError } from '../api/client';

const USERNAME_STORAGE_KEY = 'zeus.qrz.username';

function readSavedUsername(): string {
  try {
    if (typeof localStorage === 'undefined') return '';
    return localStorage.getItem(USERNAME_STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

function writeSavedUsername(user: string): void {
  try {
    if (typeof localStorage === 'undefined') return;
    if (user) localStorage.setItem(USERNAME_STORAGE_KEY, user);
    else localStorage.removeItem(USERNAME_STORAGE_KEY);
  } catch {
    /* quota / private mode — accept silently */
  }
}

export type QrzStoreState = {
  // Session state mirrored from the backend; null means we haven't checked yet.
  connected: boolean;
  hasXmlSubscription: boolean;
  hasApiKey: boolean;
  home: QrzStation | null;
  // Remembered username (password is never persisted).
  rememberedUsername: string;
  // Last lookup result, shown in QrzCard / used to render map target.
  lastLookup: QrzStation | null;
  // Per-callsign cache of resolved stations (null = looked up, no result), used
  // by bulk consumers like the Spots panel to show operator names without
  // re-hitting the XML API for a call already seen this session.
  nameCache: Record<string, QrzStation | null>;
  // Transient UI state for async ops.
  loginInFlight: boolean;
  lookupInFlight: boolean;
  loginError: string | null;
  lookupError: string | null;

  // Actions.
  refreshStatus: () => Promise<void>;
  login: (username: string, password: string) => Promise<boolean>;
  logout: () => Promise<void>;
  lookup: (callsign: string) => Promise<QrzStation | null>;
  // Cached, in-flight-deduped lookup for bulk callers. Returns immediately from
  // cache when possible; only fires one network request per callsign. No-ops
  // (returns null) when QRZ isn't connected, so it's safe to call optimistically.
  lookupCached: (callsign: string) => Promise<QrzStation | null>;
  clearLookup: () => void;
  setApiKey: (apiKey: string | null) => Promise<void>;
};

// In-flight lookups, deduped by callsign. Module-level (not store state) so a
// pending fetch doesn't trigger re-renders; only the resolved value lands in
// nameCache, which is store state.
const inFlightLookups = new Map<string, Promise<QrzStation | null>>();

function applyStatus(status: QrzStatus): Partial<QrzStoreState> {
  return {
    connected: status.connected,
    hasXmlSubscription: status.hasXmlSubscription,
    hasApiKey: status.hasApiKey,
    home: status.home,
    loginError: status.error,
  };
}

export const useQrzStore = create<QrzStoreState>((set, get) => ({
  connected: false,
  hasXmlSubscription: false,
  hasApiKey: false,
  home: null,
  rememberedUsername: readSavedUsername(),
  lastLookup: null,
  nameCache: {},
  loginInFlight: false,
  lookupInFlight: false,
  loginError: null,
  lookupError: null,

  refreshStatus: async () => {
    try {
      const status = await qrzStatus();
      set(applyStatus(status));
    } catch {
      // Status is a read-only sanity probe; leave state as-is on transient fetch failures.
    }
  },

  login: async (username, password) => {
    set({ loginInFlight: true, loginError: null });
    try {
      const status = await qrzLogin(username, password);
      writeSavedUsername(username);
      set({ ...applyStatus(status), rememberedUsername: username, loginInFlight: false });
      return status.connected;
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ loginInFlight: false, loginError: msg, connected: false, home: null });
      return false;
    }
  },

  logout: async () => {
    try {
      await qrzLogout();
    } finally {
      inFlightLookups.clear();
      set({ connected: false, hasXmlSubscription: false, home: null, lastLookup: null, nameCache: {}, loginError: null, lookupError: null });
    }
  },

  lookup: async (callsign) => {
    const trimmed = callsign.trim().toUpperCase();
    if (!trimmed) return null;
    set({ lookupInFlight: true, lookupError: null });
    try {
      const station = await qrzLookup(trimmed);
      set({ lastLookup: station, lookupInFlight: false });
      return station;
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ lookupInFlight: false, lookupError: msg });
      return null;
    }
  },

  lookupCached: async (callsign) => {
    const key = callsign.trim().toUpperCase();
    if (!key) return null;

    // Already resolved this session (station or a definitive miss).
    const cached = get().nameCache[key];
    if (cached !== undefined) return cached;

    // Coalesce concurrent requests for the same call.
    const pending = inFlightLookups.get(key);
    if (pending) return pending;

    // No point hitting the API when there's no QRZ session — leave the cache
    // untouched so it retries once the operator logs in.
    if (!get().connected) return null;

    const p = (async (): Promise<QrzStation | null> => {
      try {
        const station = await qrzLookup(key);
        set((s) => ({ nameCache: { ...s.nameCache, [key]: station } }));
        return station;
      } catch {
        // Cache the miss so a not-found / errored call isn't retried every
        // render. clearLookup-on-logout (below) resets this for a new session.
        set((s) => ({ nameCache: { ...s.nameCache, [key]: null } }));
        return null;
      } finally {
        inFlightLookups.delete(key);
      }
    })();
    inFlightLookups.set(key, p);
    return p;
  },

  clearLookup: () => set({ lastLookup: null, lookupError: null }),

  setApiKey: async (apiKey) => {
    try {
      const status = await qrzSetApiKey(apiKey);
      set(applyStatus(status));
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ loginError: msg });
    }
  },
}));

// Kick off a status probe at module load so reconnected sessions (backend still has
// a session key from an earlier page) rehydrate without the user having to log in.
//
// Retry-on-load: a fresh backend has stored credentials in LiteDB but its
// silent QRZ XML-API re-login takes ~400-1000 ms (TLS + auth). If the
// frontend's first probe lands during that window, the response is
// connected=false even though the operator IS about to be logged in. We
// retry with backoff while the backend reports `hasStoredCredentials:true`
// and `connected:false` — that combination is the unambiguous "in flight"
// signal. Stops as soon as connected flips true, or hasStoredCredentials
// flips false (operator never set creds, or just logged out elsewhere).
async function probeStatusWithRetry(): Promise<void> {
  // Total budget ~6.2 s — well past a typical QRZ silent-login + slack.
  const backoffMs = [200, 400, 800, 1600, 3200];
  for (let attempt = 0; ; attempt++) {
    let status: QrzStatus;
    try {
      status = await qrzStatus();
    } catch {
      // Transient fetch failure (backend not yet listening on first
      // attempt is the common case). Sleep and try again — but stop
      // after the budget, no point hammering.
      if (attempt >= backoffMs.length) return;
      await new Promise((r) => setTimeout(r, backoffMs[attempt]));
      continue;
    }

    useQrzStore.setState(applyStatus(status));

    // Done — either we got connected, or the backend has nothing pending.
    if (status.connected) return;
    if (!status.hasStoredCredentials) return;

    // Out of retries.
    if (attempt >= backoffMs.length) return;

    await new Promise((r) => setTimeout(r, backoffMs[attempt]));
  }
}

void probeStatusWithRetry();
