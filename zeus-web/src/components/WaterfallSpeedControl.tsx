// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

import {
  DEFAULT_WF_SCROLL_SPEED,
  WATERFALL_SCROLL_SPEED_MAX,
  WATERFALL_SCROLL_SPEED_MIN,
  WATERFALL_SCROLL_SPEED_STEP,
  useDisplaySettingsStore,
} from '../state/display-settings-store';

function speedLabel(speed: number): string {
  return `${speed.toFixed(2)}x`;
}

export function WaterfallSpeedControl() {
  const speed = useDisplaySettingsStore((s) => s.waterfallScrollSpeed);
  const setSpeed = useDisplaySettingsStore((s) => s.setWaterfallScrollSpeed);

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
      }}
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
        SPEED
      </span>
      <input
        type="range"
        min={WATERFALL_SCROLL_SPEED_MIN}
        max={WATERFALL_SCROLL_SPEED_MAX}
        step={WATERFALL_SCROLL_SPEED_STEP}
        value={speed}
        onDoubleClick={() => setSpeed(DEFAULT_WF_SCROLL_SPEED)}
        onChange={(e) => setSpeed(Number(e.currentTarget.value))}
        aria-label="Waterfall scroll speed"
        style={{
          width: 76,
          cursor: 'pointer',
          accentColor: 'var(--accent)',
          margin: 0,
        }}
      />
      <span
        className="v"
        style={{
          minWidth: 38,
          textAlign: 'right',
          color: 'var(--fg-0)',
          fontWeight: 700,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {speedLabel(speed)}
      </span>
    </label>
  );
}
