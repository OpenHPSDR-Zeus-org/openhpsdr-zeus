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

import { createContext, useContext, type FormEvent, type ReactNode, type RefObject } from 'react';
import type { Contact } from '../components/design/data';
import type { BackgroundImageFit, PanBackgroundMode } from '../state/display-settings-store';

export interface EffectiveHome {
  call: string;
  lat: number;
  lon: number;
  grid: string;
  imageUrl: string | null;
}

// All workspace-level state and callbacks that panel components need.
// App.tsx creates this context; panels consume it via useWorkspace().
// Keeping state in App.tsx (rather than lifting to Zustand) is intentional
// for Phase 1 — the context is just plumbing, not a state refactor.
export interface WorkspaceCtx {
  // Connection
  connected: boolean;
  moxOn: boolean;
  tunOn: boolean;
  mode: string;
  vfoHz: number;

  // QRZ / Terminator
  callsign: string;
  setCallsign: (v: string) => void;
  // terminatorActive is derived: panBackground === 'beam-map'.
  terminatorActive: boolean;
  // imageMode: panBackground === 'image' AND a backgroundImage is loaded.
  imageMode: boolean;
  // bgActive: any non-basic background overlay is active (map or image).
  bgActive: boolean;
  panBackground: PanBackgroundMode;
  backgroundImage: string | null;
  backgroundImageFit: BackgroundImageFit;
  enriching: boolean;
  lookupKey: number;
  contact: Contact | null;
  qrzLookupError: string | null;
  qrzActive: boolean;
  mapAvailable: boolean;
  setMapAvailable: (v: boolean) => void;
  mapInteractive: boolean;
  effectiveHome: EffectiveHome | null;
  beamOverrideDeg: number | null;
  setBeamOverrideDeg: (v: number | null) => void;
  beamInputStr: string;
  setBeamInputStr: (v: string) => void;
  rotLiveAz: number | null;
  sp: number;
  lp: number;
  dist: number;
  heroTitle: ReactNode;
  csInputRef: RefObject<HTMLInputElement | null>;
  runQrzLookup: (cs?: string) => void;
  onCallsignSubmit: (e: FormEvent<HTMLFormElement>) => void;
  submitBeam: (e: FormEvent<HTMLFormElement>) => void;
  handleLogQso: () => void;
  handleClearQrz: () => void;

  // DSP
  dspActive: boolean;

  // CW
  wpm: number;
  setWpm: (v: number) => void;

  // Logbook
  logbookTitle: string;
  logbookActions: ReactNode;
}

export const WorkspaceContext = createContext<WorkspaceCtx | null>(null);

export function useWorkspace(): WorkspaceCtx {
  const ctx = useContext(WorkspaceContext);
  if (!ctx) throw new Error('useWorkspace must be used inside WorkspaceContext.Provider');
  return ctx;
}
