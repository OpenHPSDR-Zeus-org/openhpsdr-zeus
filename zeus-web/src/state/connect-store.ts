// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { BoardKind } from '../api/radio';

export type ProtocolChoice = 'P1' | 'P2' | 'P3';
export type SampleRate = 48_000 | 96_000 | 192_000 | 384_000 | 768_000 | 1_536_000;
export type ConnectMode = 'discover' | 'manual';

// `board` is optional on saved endpoints so older persisted entries (which
// pre-date the Manual-mode radio-type dropdown) deserialize cleanly. Treat
// missing board as 'Auto' on read.
export interface SavedEndpoint {
  id: string;
  label?: string;
  ip: string;
  port: number;
  protocol: ProtocolChoice;
  sampleRate: SampleRate;
  board?: BoardKind;
  lastUsedUtc: string;
}

export interface ManualFormDefaults {
  ip: string;
  port: number;
  protocol: ProtocolChoice;
  sampleRate: SampleRate;
  board: BoardKind;
  label: string;
}

const DEFAULT_FORM: ManualFormDefaults = {
  ip: '',
  port: 1024,
  protocol: 'P1',
  sampleRate: 192_000,
  board: 'Auto',
  label: '',
};

export interface ConnectState {
  mode: ConnectMode;
  savedEndpoints: SavedEndpoint[];
  lastConnectedId?: string;
  manualFormDefaults: ManualFormDefaults;
  setMode: (m: ConnectMode) => void;
  saveEndpoint: (
    e: Omit<SavedEndpoint, 'id' | 'lastUsedUtc'>,
  ) => string;
  removeEndpoint: (id: string) => void;
  touchEndpoint: (id: string) => void;
  setManualFormDefaults: (d: ManualFormDefaults) => void;
}

function newId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `ep-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export const useConnectStore = create<ConnectState>()(
  persist(
    (set, get) => ({
      mode: 'discover',
      savedEndpoints: [],
      lastConnectedId: undefined,
      manualFormDefaults: DEFAULT_FORM,
      setMode: (m) => set({ mode: m }),
      saveEndpoint: (e) => {
        const existing = get().savedEndpoints.find(
          (x) => x.ip === e.ip && x.port === e.port && x.protocol === e.protocol,
        );
        const now = new Date().toISOString();
        if (existing) {
          set((s) => ({
            savedEndpoints: s.savedEndpoints.map((x) =>
              x.id === existing.id
                ? {
                    ...x,
                    label: e.label ?? x.label,
                    sampleRate: e.sampleRate,
                    board: e.board ?? x.board,
                    lastUsedUtc: now,
                  }
                : x,
            ),
            lastConnectedId: existing.id,
          }));
          return existing.id;
        }
        const id = newId();
        set((s) => ({
          savedEndpoints: [...s.savedEndpoints, { ...e, id, lastUsedUtc: now }],
          lastConnectedId: id,
        }));
        return id;
      },
      removeEndpoint: (id) =>
        set((s) => ({
          savedEndpoints: s.savedEndpoints.filter((x) => x.id !== id),
          lastConnectedId:
            s.lastConnectedId === id ? undefined : s.lastConnectedId,
        })),
      touchEndpoint: (id) =>
        set((s) => ({
          savedEndpoints: s.savedEndpoints.map((x) =>
            x.id === id ? { ...x, lastUsedUtc: new Date().toISOString() } : x,
          ),
          lastConnectedId: id,
        })),
      setManualFormDefaults: (d) => set({ manualFormDefaults: d }),
    }),
    {
      name: 'zeus-connect',
      partialize: (s) => ({
        mode: s.mode,
        savedEndpoints: s.savedEndpoints,
        lastConnectedId: s.lastConnectedId,
        manualFormDefaults: s.manualFormDefaults,
      }),
    },
  ),
);
