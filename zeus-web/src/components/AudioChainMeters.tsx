// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioChainMeters — chain-level IN / OUT signal meters for the Audio
// Suite, modelled on the VSTHost rack meters. Polls
// /api/{tx,rx}-audio-suite/chain/meters at ~15 Hz while the window is open
// and renders two horizontal bars (green -> yellow -> red) with peak-hold.
//
// Isolated into its own component so the high-frequency level updates
// re-render only the meters, never the rack / sidebar (which would
// cancel an in-flight drag). All chrome uses Zeus tokens.
//
// Note: the chain only processes audio during MOX/TX or desktop-mode
// preview, so both bars sit at the floor when idle — that's expected,
// not a bug.

import { useEffect, useRef, useState } from 'react';
import { recessedWellBackground, recessedWellShadow } from './meters/render/recessedWell';
import { mercuryGradientCss } from './meters/render/fillGradient';
import { MeterGlass } from './meters/render/MeterGlass';
import { startEfficientPolling } from '../util/efficient-polling';

const POLL_MS = 66; // ~15 Hz
const HIDDEN_POLL_MS = false;
const PEAK_HOLD_MS = 1200; // peak-tick decay
const FLOOR_DB = -60; // bottom of the visual scale

interface ChainMetersDto {
  inputPeak: number;
  outputPeak: number;
  inputDb: number;
  outputDb: number;
}

type AudioChainMetersProps = {
  compact?: boolean;
  route?: 'tx' | 'rx';
  title?: string;
};

/** Map a linear peak (0..1+) to a 0..1 bar fraction on a dB scale. */
function fractionFromDb(db: number): number {
  if (!Number.isFinite(db)) return 0;
  if (db <= FLOOR_DB) return 0;
  if (db >= 0) return 1;
  return 1 - db / FLOOR_DB; // db in (FLOOR_DB,0) → (0,1)
}

function normalizeMeters(body: Partial<ChainMetersDto>): ChainMetersDto {
  return {
    inputPeak: Number.isFinite(body.inputPeak) ? body.inputPeak! : 0,
    outputPeak: Number.isFinite(body.outputPeak) ? body.outputPeak! : 0,
    inputDb: Number.isFinite(body.inputDb) ? body.inputDb! : FLOOR_DB,
    outputDb: Number.isFinite(body.outputDb) ? body.outputDb! : FLOOR_DB,
  };
}

function zoneColor(frac: number): string {
  if (frac > 0.9) return 'var(--tx)'; // red — clipping risk
  if (frac > 0.7) return 'var(--power)'; // yellow — hot
  return 'var(--ok)'; // green — nominal
}

function LevelBar({ label, db }: { label: string; db: number }) {
  const frac = fractionFromDb(db);
  const [peak, setPeak] = useState(0);
  const peakTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (frac > peak) {
      setPeak(frac);
      if (peakTimer.current) clearTimeout(peakTimer.current);
      peakTimer.current = setTimeout(() => setPeak(0), PEAK_HOLD_MS);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [frac]);

  useEffect(
    () => () => {
      if (peakTimer.current) clearTimeout(peakTimer.current);
    },
    [],
  );

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, height: 12 }}>
      <span
        style={{
          width: 34,
          fontSize: 9,
          fontWeight: 600,
          letterSpacing: 0.8,
          color: 'var(--fg-3)',
          textTransform: 'uppercase',
          flex: '0 0 auto',
          whiteSpace: 'nowrap',
          lineHeight: 1,
        }}
      >
        {label}
      </span>
      <div
        style={{
          position: 'relative',
          flex: 1,
          height: 10,
          background: recessedWellBackground(),
          border: '1px solid var(--line)',
          borderRadius: 3,
          overflow: 'hidden',
          boxShadow: recessedWellShadow(),
        }}
      >
        <div
          style={{
            position: 'absolute',
            left: 0,
            top: 0,
            bottom: 0,
            width: `${frac * 100}%`,
            background: `${mercuryGradientCss()}, linear-gradient(to right, var(--ok) 0%, var(--ok) 65%, var(--power) 85%, var(--tx) 100%)`,
            transition: 'width 0.05s linear',
          }}
        />
        {peak > 0.02 && (
          <div
            style={{
              position: 'absolute',
              top: 0,
              bottom: 0,
              left: `calc(${Math.min(peak, 1) * 100}% - 1px)`,
              width: 2,
              background: zoneColor(peak),
              boxShadow: `0 0 4px ${zoneColor(peak)}`,
            }}
          />
        )}
        <MeterGlass radius={3} />
      </div>
      <span
        style={{
          // Reserve a fixed slot and keep digit advance constant so the
          // readout can't change width as the level changes (incl. the
          // −∞ ↔ -NN toggle at the floor). A changing-width number here
          // would reflow the chip strip mounted directly below — same
          // tabular-nums + reserved-width fix used on the filter ribbon
          // and mic meter.
          width: 38,
          flex: '0 0 38px',
          textAlign: 'right',
          fontSize: 9,
          fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
          fontVariantNumeric: 'tabular-nums',
          color: 'var(--fg-3)',
          whiteSpace: 'nowrap',
          overflow: 'hidden',
          lineHeight: 1,
        }}
      >
        {db <= FLOOR_DB ? '−∞' : `${db.toFixed(0)}`}
      </span>
    </div>
  );
}

export function AudioChainMeters({
  compact = false,
  route = 'tx',
  title,
}: AudioChainMetersProps = {}) {
  const [meters, setMeters] = useState<ChainMetersDto>({
    inputPeak: 0,
    outputPeak: 0,
    inputDb: FLOOR_DB,
    outputDb: FLOOR_DB,
  });

  useEffect(() => {
    const endpoint =
      route === 'rx'
        ? '/api/rx-audio-suite/chain/meters'
        : '/api/tx-audio-suite/chain/meters';
    return startEfficientPolling(
      async (signal) => {
        const res = await fetch(endpoint, { signal });
        if (res.ok) {
          const body = (await res.json()) as Partial<ChainMetersDto>;
          setMeters(normalizeMeters(body));
        }
      },
      {
        intervalMs: POLL_MS,
        hiddenIntervalMs: HIDDEN_POLL_MS,
        onError: () => {
          /* transient — keep last reading, try again next tick */
        },
      },
    );
  }, [route]);

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: compact ? 5 : 6,
        padding: compact ? '7px 8px' : '10px 14px',
        background: compact ? 'var(--bg-2)' : 'var(--bg-1)',
        border: compact ? '1px solid var(--line)' : undefined,
        borderBottom: compact ? undefined : '1px solid var(--line)',
        borderRadius: compact ? 4 : undefined,
        minWidth: 0,
      }}
    >
      {title && (
        <div
          className="label-xs"
          style={{
            color: 'var(--fg-2)',
            fontWeight: 800,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {title}
        </div>
      )}
      <LevelBar label="In" db={meters.inputDb} />
      <LevelBar label="Out" db={meters.outputDb} />
    </div>
  );
}
