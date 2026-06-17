// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Display-only waterfall relief controls. These drive the EXISTING shared
// setter useSignalEnhanceStore.setSignalEnhanceTuning(patch); no per-field
// setters exist, so each slider passes a single-field patch. Relief / Smooth /
// Glow only affect the render path while Signal Pop is active, so the three
// sliders dim + disable when popEnabled is false.

import { useSignalEnhanceStore } from '../dsp/signal-estimator';

// The estimator does NOT export per-field range constants and
// setSignalEnhanceTuning clamps to 0..100 in normalizeTuning, so the
// documented min/max/step/default live here locally.
const RELIEF_MIN = 0;
const RELIEF_MAX = 100;
const RELIEF_STEP = 1;
const DEFAULT_RELIEF_DEPTH = 92;
const DEFAULT_SMOOTHNESS = 64;
const DEFAULT_GLOW = 72;

type ReliefSliderProps = {
  shortKey: string;
  ariaLabel: string;
  value: number;
  defaultValue: number;
  enabled: boolean;
  onCommit: (n: number) => void;
};

function ReliefSlider({
  shortKey,
  ariaLabel,
  value,
  defaultValue,
  enabled,
  onCommit,
}: ReliefSliderProps) {
  return (
    <label
      className="mono"
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        background: 'transparent',
        border: 'none',
        padding: '0 2px',
        fontSize: 10,
        opacity: enabled ? 1 : 0.45,
        cursor: enabled ? 'default' : 'not-allowed',
      }}
      title={
        enabled
          ? undefined
          : 'Relief, Smooth and Glow only affect the render while Signal Pop is on'
      }
    >
      <span
        className="k"
        style={{
          color: 'var(--fg-2)',
          fontWeight: 600,
          letterSpacing: '0.06em',
          textTransform: 'uppercase',
          fontSize: 9,
        }}
      >
        {shortKey}
      </span>
      <input
        type="range"
        min={RELIEF_MIN}
        max={RELIEF_MAX}
        step={RELIEF_STEP}
        value={value}
        disabled={!enabled}
        onDoubleClick={() => enabled && onCommit(defaultValue)}
        onChange={(e) => onCommit(Number(e.currentTarget.value))}
        aria-label={ariaLabel}
        style={{
          width: 64,
          cursor: enabled ? 'pointer' : 'not-allowed',
          accentColor: enabled ? 'var(--accent)' : 'var(--fg-2)',
          margin: 0,
          pointerEvents: enabled ? 'auto' : 'none',
        }}
      />
      <span
        className="v"
        style={{
          minWidth: 24,
          textAlign: 'right',
          color: enabled ? 'var(--fg-0)' : 'var(--fg-2)',
          fontWeight: 700,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {Math.round(value)}
      </span>
    </label>
  );
}

export function WaterfallReliefControl() {
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const reliefDepth = useSignalEnhanceStore((s) => s.waterfallReliefDepth);
  const smoothness = useSignalEnhanceStore((s) => s.waterfallSmoothness);
  const glow = useSignalEnhanceStore((s) => s.popRenderIntensity);
  const setTuning = useSignalEnhanceStore((s) => s.setSignalEnhanceTuning);

  return (
    <>
      <ReliefSlider
        shortKey="RELIEF"
        ariaLabel="Waterfall relief depth"
        value={reliefDepth}
        defaultValue={DEFAULT_RELIEF_DEPTH}
        enabled={popEnabled}
        onCommit={(n) => setTuning({ waterfallReliefDepth: n })}
      />
      <ReliefSlider
        shortKey="SMOOTH"
        ariaLabel="Waterfall smoothness"
        value={smoothness}
        defaultValue={DEFAULT_SMOOTHNESS}
        enabled={popEnabled}
        onCommit={(n) => setTuning({ waterfallSmoothness: n })}
      />
      <ReliefSlider
        shortKey="GLOW"
        ariaLabel="Signal Pop glow intensity"
        value={glow}
        defaultValue={DEFAULT_GLOW}
        enabled={popEnabled}
        onCommit={(n) => setTuning({ popRenderIntensity: n })}
      />
    </>
  );
}
