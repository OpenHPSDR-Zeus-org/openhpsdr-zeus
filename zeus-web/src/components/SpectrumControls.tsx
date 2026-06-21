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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useNotchStore } from '../state/notch-store';

// Panadapter display toggles — dB auto/fixed range, Signal Pop, Snap-to-signal,
// and the Notch arm/clear. These used to overlay the panadapter top-right, where
// they collided with the frequency-axis labels; they now live in the hero tile
// header alongside the zoom slider. State is shared (same stores the spectrum
// surfaces read), so behaviour is identical — only the mount point moved.
export function SpectrumControls() {
  const autoRange = useDisplaySettingsStore((s) => s.autoRange);
  const setAutoRange = useDisplaySettingsStore((s) => s.setAutoRange);
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const togglePop = useSignalEnhanceStore((s) => s.togglePop);
  const snapEnabled = useSignalEnhanceStore((s) => s.snapEnabled);
  const toggleSnap = useSignalEnhanceStore((s) => s.toggleSnap);
  const notchArmed = useNotchStore((s) => s.armed);
  const toggleNotchArmed = useNotchStore((s) => s.toggleArmed);
  const notchCount = useNotchStore((s) => s.notches.length);
  const clearNotches = useNotchStore((s) => s.clearAll);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
      <button
        type="button"
        onClick={() => setAutoRange(!autoRange)}
        aria-pressed={autoRange}
        title={
          autoRange
            ? 'Auto dB range: tracking p5/p95 of waterfall samples'
            : 'Fixed dB range: −120 to −30 dBFS'
        }
        className={`btn sm ${autoRange ? 'active' : ''}`}
      >
        {autoRange ? 'dB: AUTO' : 'dB: FIXED'}
      </button>
      <button
        type="button"
        onClick={togglePop}
        aria-pressed={popEnabled}
        title={
          popEnabled
            ? 'Signal Pop ON: per-bin noise-floor subtraction (weak signals brightened)'
            : 'Signal Pop: subtract the adaptive noise floor so weak signals pop'
        }
        className={`btn sm pop-btn ${popEnabled ? 'active' : ''}`}
      >
        POP
      </button>
      <button
        type="button"
        onClick={toggleSnap}
        aria-pressed={snapEnabled}
        title={
          snapEnabled
            ? 'Snap-to-signal ON: clicks tune to the nearest carrier peak'
            : 'Snap-to-signal: click near a signal to tune exactly onto it'
        }
        className={`btn sm ${snapEnabled ? 'active' : ''}`}
      >
        SNAP
      </button>
      <button
        type="button"
        onClick={toggleNotchArmed}
        aria-pressed={notchArmed}
        title={
          notchArmed
            ? 'Notch armed: drag across the spectrum to mask out EMF/birdies'
            : 'Notch: arm, then drag across an interfering signal to notch it'
        }
        className={`btn sm ${notchArmed ? 'active' : ''}`}
      >
        NOTCH
      </button>
      {notchCount > 0 && (
        <button
          type="button"
          onClick={clearNotches}
          title={`Clear all ${notchCount} notch${notchCount === 1 ? '' : 'es'}`}
          className="btn sm"
        >
          ✕{notchCount}
        </button>
      )}
    </div>
  );
}
