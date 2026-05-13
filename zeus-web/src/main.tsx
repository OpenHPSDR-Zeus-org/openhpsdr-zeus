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

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
// PERF_PASS_3_DEBUG: expose tx-store + audio client on window so playwright
// can drive MOX edges from synthetic mode (no radio = no MOX button).
// Uncommitted local edit; stash before merge.
import { useTxStore } from './state/tx-store';
import { getAudioClient } from './audio/audio-client';
import { decodeAudioFrame } from './audio/frame';
import './index.css';
import './styles/tokens.css';
import './styles/layout.css';
import './styles/filter-ribbon.css';
import './styles/toolbar-favorites.css';
import './styles/nr-settings.css';
import './styles/meters-grid.css';
import './styles/all-panels.css';
import './styles/ps-settings.css';
import './styles/pa-settings.css';
import './styles/analog-meter.css';
import App from './App.tsx';
import { installFetchInterceptor } from './serverUrl';

// Capacitor / standalone-host builds set localStorage["zeus.serverUrl"]
// to a LAN address; on plain web this is a no-op (relative paths).
installFetchInterceptor();

// PERF_PASS_3_DEBUG: window debug helpers for playwright-driven validation.
(window as unknown as Record<string, unknown>).__zeusPerf3 = {
  txStore: useTxStore,
  audioClient: () => getAudioClient(),
  decodeAudioFrame,
  setMoxOn: (on: boolean) => useTxStore.getState().setMoxOn(on),
  captures: [] as Array<{
    cycle: number;
    t0_mox_off: number;
    t4_audio_scheduled?: number;
    nextPlayTime?: number;
    now?: number;
    delta_ms?: number;
  }>,
};

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('root element missing');

createRoot(rootEl).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
