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

import { useCallback, useState } from 'react';
import { zeroBeat } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

/**
 * One-shot CW Zero Beat trigger (issue #300).
 *
 * Renders only when the current mode is CWL or CWU. While running, the
 * button gets an `var(--accent)` border via the `running` class so the
 * operator sees the action took effect. The ~2 s perceived latency is
 * dominated by phase 1 (~500 ms); phase 2 settles silently after the dial
 * has already moved.
 *
 * No toast on failure: the VFO not moving is itself the signal that
 * nothing was found. Matches Thetis / standard CW-rig convention.
 */
export function ZeroBeatButton() {
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const [running, setRunning] = useState(false);

  const onClick = useCallback(async () => {
    if (running) return;
    setRunning(true);
    try {
      const next = await zeroBeat();
      if (next) applyState(next);
    } catch {
      // swallow — no toast, no flash; VFO stays put
    } finally {
      setRunning(false);
    }
  }, [running, applyState]);

  if (mode !== 'CWL' && mode !== 'CWU') return null;

  return (
    <button
      type="button"
      className={`btn sm${running ? ' running' : ''}`}
      onClick={onClick}
      disabled={running}
      title="Zero Beat — snap VFO to strongest CW carrier in passband (Z)"
    >
      0 BEAT
    </button>
  );
}
