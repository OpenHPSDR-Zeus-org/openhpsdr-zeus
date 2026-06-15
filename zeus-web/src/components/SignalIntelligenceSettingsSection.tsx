// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import {
  SIGNAL_ENHANCE_PROFILE_ORDER,
  useSignalEnhanceStore,
  type SignalEnhanceProfileId,
  type SignalEnhancePresetId,
} from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { Slider } from './design/Slider';

const PROFILE_LABEL: Record<SignalEnhancePresetId, string> = {
  balanced: 'Balanced',
  dx: 'DX',
  cw: 'CW',
  digital: 'Digital',
  voice: 'Voice',
  contest: 'Contest',
};

function profileLabel(id: SignalEnhanceProfileId): string {
  return id === 'custom' ? 'Custom' : PROFILE_LABEL[id];
}

type TuneSliderProps = {
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  precision?: number;
  onChange: (v: number) => void;
};

function roundTo(v: number, precision: number): number {
  const m = 10 ** precision;
  return Math.round(v * m) / m;
}

function TuneSlider({ label, value, min, max, unit = '', precision = 1, onChange }: TuneSliderProps) {
  const shown = roundTo(value, precision);
  return (
    <div className="sig-tune">
      <Slider
        label={label}
        value={shown}
        min={min}
        max={max}
        onChange={(v) => onChange(roundTo(v, precision))}
        formatValue={(v) => `${roundTo(v, precision).toFixed(precision)}${unit}`}
      />
      <input
        className="sig-tune-input"
        type="number"
        min={min}
        max={max}
        step={precision === 0 ? 1 : 1 / 10 ** precision}
        value={shown}
        onChange={(e) => {
          const next = Number(e.currentTarget.value);
          if (Number.isFinite(next)) onChange(roundTo(next, precision));
        }}
      />
    </div>
  );
}

export function SignalIntelligenceSettingsSection() {
  const mode = useConnectionStore((s) => s.mode);
  const state = useSignalEnhanceStore();
  const {
    profileId,
    popEnabled,
    snapEnabled,
    autoProfileEnabled,
    visualAgcEnabled,
    impulseRejectEnabled,
    sceneStatus,
    setPopEnabled,
    setSnapEnabled,
    setVisualAgcEnabled,
    setImpulseRejectEnabled,
    setSignalEnhanceTuning,
    applySignalEnhanceProfile,
    setSignalEnhanceAutoProfile,
    resetSignalEnhanceTuning,
  } = state;

  return (
    <div className="sig-intel">
      <div className="sig-intel-top">
        <div className="sig-profile-row" role="group" aria-label="Signal intelligence profile">
          {SIGNAL_ENHANCE_PROFILE_ORDER.map((id) => (
            <button
              key={id}
              type="button"
              className="sig-profile-btn"
              aria-pressed={profileId === id}
              onClick={() => applySignalEnhanceProfile(id)}
            >
              {PROFILE_LABEL[id]}
            </button>
          ))}
        </div>
        <div className="sig-status mono">
          {autoProfileEnabled ? `AUTO ${profileLabel(profileId)}` : profileLabel(profileId).toUpperCase()}
        </div>
      </div>

      <div className="sig-toggle-row">
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={autoProfileEnabled}
            onChange={(e) => setSignalEnhanceAutoProfile(e.currentTarget.checked, mode)}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Auto Profile</span>
        </label>
        <label className="sig-switch">
          <input type="checkbox" checked={popEnabled} onChange={(e) => setPopEnabled(e.currentTarget.checked)} />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Signal Pop</span>
        </label>
        <label className="sig-switch">
          <input type="checkbox" checked={snapEnabled} onChange={(e) => setSnapEnabled(e.currentTarget.checked)} />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Snap</span>
        </label>
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={visualAgcEnabled}
            onChange={(e) => setVisualAgcEnabled(e.currentTarget.checked)}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Visual AGC</span>
        </label>
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={impulseRejectEnabled}
            onChange={(e) => setImpulseRejectEnabled(e.currentTarget.checked)}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Spike Reject</span>
        </label>
        <button type="button" className="sig-reset-btn" onClick={resetSignalEnhanceTuning}>
          Reset
        </button>
      </div>

      {sceneStatus && (
        <div className="smart-nr-status">
          <span className="mono">{PROFILE_LABEL[sceneStatus.profileId]}</span>
          <span>{sceneStatus.reason}</span>
          <span className="mono">
            SNR {sceneStatus.maxSnrDb.toFixed(1)} dB · OCC {sceneStatus.occupiedPct.toFixed(1)}% · PK {sceneStatus.peakCount}
          </span>
        </div>
      )}

      <div className="sig-grid">
        <TuneSlider
          label="Pop Gate"
          value={state.popFloorDb}
          min={0}
          max={12}
          unit=" dB"
          onChange={(popFloorDb) => setSignalEnhanceTuning({ popFloorDb })}
        />
        <TuneSlider
          label="Pop Span"
          value={state.popSpanDb}
          min={12}
          max={60}
          unit=" dB"
          onChange={(popSpanDb) => setSignalEnhanceTuning({ popSpanDb })}
        />
        <TuneSlider
          label="Gamma"
          value={state.popGamma}
          min={0.3}
          max={1.2}
          precision={2}
          onChange={(popGamma) => setSignalEnhanceTuning({ popGamma })}
        />
        <TuneSlider
          label="Pop Glow"
          value={state.popRenderIntensity}
          min={0}
          max={100}
          unit="%"
          precision={0}
          onChange={(popRenderIntensity) => setSignalEnhanceTuning({ popRenderIntensity })}
        />
        <TuneSlider
          label="Coherence Gate"
          value={state.coherenceHoldGate}
          min={0.2}
          max={0.8}
          precision={2}
          onChange={(coherenceHoldGate) => setSignalEnhanceTuning({ coherenceHoldGate })}
        />
        <TuneSlider
          label="Coherence Boost"
          value={state.coherenceBoostDb}
          min={0}
          max={8}
          unit=" dB"
          onChange={(coherenceBoostDb) => setSignalEnhanceTuning({ coherenceBoostDb })}
        />
        <TuneSlider
          label="Ridge Gain"
          value={state.ridgeBoost}
          min={0}
          max={0.8}
          precision={2}
          onChange={(ridgeBoost) => setSignalEnhanceTuning({ ridgeBoost })}
        />
        <TuneSlider
          label="Ridge Cap"
          value={state.ridgeMaxBoostDb}
          min={0}
          max={12}
          unit=" dB"
          onChange={(ridgeMaxBoostDb) => setSignalEnhanceTuning({ ridgeMaxBoostDb })}
        />
        <TuneSlider
          label="Visual AGC"
          value={state.visualAgcStrength}
          min={0}
          max={100}
          unit="%"
          precision={0}
          onChange={(visualAgcStrength) => setSignalEnhanceTuning({ visualAgcStrength })}
        />
        <TuneSlider
          label="Spike Reject"
          value={state.impulseRejectDb}
          min={8}
          max={32}
          unit=" dB"
          precision={0}
          onChange={(impulseRejectDb) => setSignalEnhanceTuning({ impulseRejectDb })}
        />
        <TuneSlider
          label="Snap Radius"
          value={state.snapRadiusHz}
          min={500}
          max={12_000}
          unit=" Hz"
          precision={0}
          onChange={(snapRadiusHz) => setSignalEnhanceTuning({ snapRadiusHz })}
        />
        <TuneSlider
          label="Snap SNR"
          value={state.snapMinSnrDb}
          min={3}
          max={16}
          unit=" dB"
          onChange={(snapMinSnrDb) => setSignalEnhanceTuning({ snapMinSnrDb })}
        />
        <TuneSlider
          label="Marker SNR"
          value={state.peakMinSnrDb}
          min={4}
          max={20}
          unit=" dB"
          onChange={(peakMinSnrDb) => setSignalEnhanceTuning({ peakMinSnrDb })}
        />
      </div>
    </div>
  );
}
