// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DSP settings tab — the full editor for the RX DSP controls that also have
// inline quick-controls on the main workspace (issue: DSP controls Thetis
// parity §7a, maintainer directive). Mirrors Thetis's DSP Setup tab structure
// with one section per control family. Sections reuse the same store +
// endpoints as the inline DspPanel controls, so the two stay in sync via the
// optimistic-send + applyState reconcile already built into each section.
//
// Visual idiom borrowed from PsSettingsPanel's `.ps-card` so this reads as the
// same surface family as the other settings tabs — no new chrome, tokens only.

import { TxLevelingSettingsSection } from './TxLevelingSettingsSection';

// AGC and Squelch moved inline onto the control strip (AgcSlider / SquelchSlider)
// — they're frequently-changed operating controls. This tab now hosts the
// less-frequently-touched TX leveling config (ALC / Leveler / Compressor).
export function DspSettingsPanel() {
  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          TX Leveling
          <span className="ps-card-hint">ALC / Leveler / Compressor</span>
        </h4>
        <TxLevelingSettingsSection />
      </div>
    </div>
  );
}
