// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

import { AgcSettingsSection } from './AgcSettingsSection';
import { AdcProtectionSettingsSection } from './AdcProtectionSettingsSection';
import { BandwidthSettingsSection } from './BandwidthSettingsSection';
import { DspFilterArchitectureSection } from './DspFilterArchitectureSection';
import { FilterShapeSettingsSection } from './FilterShapeSettingsSection';
import { SquelchSettingsSection } from './SquelchSettingsSection';
import { SignalIntelligenceSettingsSection } from './SignalIntelligenceSettingsSection';
import { SmartNrSettingsSection } from './SmartNrSettingsSection';
import { TxLevelingSettingsSection } from './TxLevelingSettingsSection';
import { useEasterEggStore } from '../state/easter-egg-store';

// Verbose DSP editor. The control strip carries quick controls (AGC dropdown,
// SQL toggle); this tab is the full editor exposing every wired parameter, and
// both drive the same store + endpoints so they stay in sync. One ps-card per
// control family, mirroring Thetis's Setup ▸ DSP layout.
export function DspSettingsPanel() {
  // WDSP Filter Architecture (which also exposes the Active RXA/TXA Filter
  // readouts) is a hidden diagnostics surface, gated behind the same
  // header-bolt easter egg as the HARDWARE settings folder. Locked by default;
  // re-locks every launch.
  const hardwareUnlocked = useEasterEggStore((s) => s.hardwareUnlocked);
  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          Bandwidth
          <span className="ps-card-hint">DDC sample rate (48…1536 kHz)</span>
        </h4>
        <BandwidthSettingsSection />
      </div>
      {hardwareUnlocked && (
        <div className="ps-card">
          <h4>
            WDSP Filter Architecture
            <span className="ps-card-hint">buffers / taps / window / cache</span>
          </h4>
          <DspFilterArchitectureSection />
        </div>
      )}
      <div className="ps-card">
        <h4>
          SSB Filter Shape
          <span className="ps-card-hint">RX / TX shoulder rectangularity (#871)</span>
        </h4>
        <FilterShapeSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          AGC
          <span className="ps-card-hint">mode / max-gain / custom</span>
        </h4>
        <AgcSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          ADC Protection
          <span className="ps-card-hint">P2 overload / max-magnitude auto-ATT</span>
        </h4>
        <AdcProtectionSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          RX Squelch
          <span className="ps-card-hint">mode-aware (SSB/AM/FM)</span>
        </h4>
        <SquelchSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          TX Leveling
          <span className="ps-card-hint">ALC / Leveler / Compressor</span>
        </h4>
        <TxLevelingSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          Signal Intelligence
          <span className="ps-card-hint">Pop / Snap / Markers</span>
        </h4>
        <SignalIntelligenceSettingsSection />
      </div>
      <div className="ps-card">
        <h4>
          Smart NR Automation
          <span className="ps-card-hint">panadapter-driven NR policy</span>
        </h4>
        <SmartNrSettingsSection />
      </div>
    </div>
  );
}
