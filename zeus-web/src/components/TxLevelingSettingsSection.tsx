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
// implementation in the OpenHPSDR ecosystem.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// TX leveling — ALC (max-gain + decay), Leveler (on/off + max-gain + decay),
// Compressor (on/off + gain) (issue: DSP controls Thetis parity §6.1-6.3, §7a).
// The ALC run state is intentionally NOT exposed — it stays on or the SSB
// modulator emits zero IQ. The Leveler MAX-GAIN ("top") is the existing
// /api/tx/leveler-max-gain control surfaced here too (reuses tx-store +
// setLevelerMaxGain) rather than duplicated into the leveling config.
// Optimistic send + applyState reconcile, mirroring AgcSettingsSection.

import { useCallback, useEffect, useRef } from 'react';
import {
  setLevelerMaxGain,
  setTxLeveling,
  type TxLevelingConfigDto,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

// One labelled number input rendered in a dsp-row. Reuses the shared token
// classes (label-xs, mono) — no new colours. Matches AgcSettingsSection.NumField.
function NumField(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  unit?: string;
  disabled: boolean;
  onCommit: (v: number) => void;
}) {
  const { label, value, min, max, step, unit, disabled, onCommit } = props;
  return (
    <label className="dsp-row" style={{ gap: 8 }}>
      <span className="label-xs" style={{ minWidth: 86 }}>
        {label}
      </span>
      <input
        type="number"
        className="mono"
        min={min}
        max={max}
        step={step ?? 1}
        value={value}
        disabled={disabled}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          if (Number.isFinite(v)) {
            onCommit(Math.max(min, Math.min(max, v)));
          }
        }}
        style={{
          width: 72,
          background: 'var(--bg-0)',
          color: 'var(--fg-0)',
          border: '1px solid var(--line)',
          borderRadius: 3,
          padding: '2px 6px',
          fontSize: 12,
        }}
      />
      {unit != null && (
        <span className="label-xs" style={{ color: 'var(--fg-2)' }}>
          {unit}
        </span>
      )}
    </label>
  );
}

// One labelled range slider in a dsp-row with a live mono read-out.
function SliderField(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  unit: string;
  disabled: boolean;
  onCommit: (v: number) => void;
}) {
  const { label, value, min, max, step, unit, disabled, onCommit } = props;
  return (
    <label className="dsp-row" style={{ gap: 8 }}>
      <span className="label-xs" style={{ minWidth: 86 }}>
        {label}
      </span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={(e) => onCommit(Number(e.currentTarget.value))}
        style={{ flex: 1, accentColor: 'var(--accent)' }}
      />
      <span
        className="mono label-xs"
        style={{ minWidth: 56, textAlign: 'right' }}
      >
        {value.toFixed(step < 1 ? 1 : 0)} {unit}
      </span>
    </label>
  );
}

export function TxLevelingSettingsSection() {
  const txLeveling = useConnectionStore((s) => s.txLeveling);
  const setLocalTxLeveling = useConnectionStore((s) => s.setTxLeveling);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  // Leveler max-gain lives on its own server path (tx-store + /api/tx/
  // leveler-max-gain), surfaced here so the operator gets the full Leveler
  // editor in one place. Not duplicated into TxLevelingConfig.
  const levelerMaxGainDb = useTxStore((s) => s.levelerMaxGainDb);
  const setLocalLevelerMaxGain = useTxStore((s) => s.setLevelerMaxGainDb);

  const inflightAbort = useRef<AbortController | null>(null);
  const maxGainAbort = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      inflightAbort.current?.abort();
      maxGainAbort.current?.abort();
    },
    [],
  );

  const send = useCallback(
    (next: TxLevelingConfigDto) => {
      setLocalTxLeveling(next);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setTxLeveling(next, ac.signal)
        .then((s) => {
          if (!ac.signal.aborted) applyState(s);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalTxLeveling, applyState],
  );

  const sendMaxGain = useCallback(
    (db: number) => {
      const clamped = Math.max(0, Math.min(20, db));
      setLocalLevelerMaxGain(clamped);
      maxGainAbort.current?.abort();
      const ac = new AbortController();
      maxGainAbort.current = ac;
      setLevelerMaxGain(clamped, ac.signal)
        .then((r) => {
          if (!ac.signal.aborted) setLocalLevelerMaxGain(r.levelerMaxGainDb);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalLevelerMaxGain],
  );

  return (
    <div className="dsp-grid" style={{ paddingTop: 0 }}>
      {/* ALC — max gain + decay. The ALC on/off is intentionally absent. */}
      <div className="dsp-row" style={{ flexWrap: 'wrap' }}>
        <span className="label-xs" style={{ minWidth: 86 }}>
          ALC
        </span>
      </div>
      <SliderField
        label="Max Gain"
        value={txLeveling.alcMaxGainDb}
        min={0}
        max={120}
        step={1}
        unit="dB"
        disabled={!connected}
        onCommit={(v) => send({ ...txLeveling, alcMaxGainDb: v })}
      />
      <NumField
        label="Decay"
        value={txLeveling.alcDecayMs}
        min={1}
        max={50}
        unit="ms"
        disabled={!connected}
        onCommit={(v) => send({ ...txLeveling, alcDecayMs: v })}
      />

      {/* Leveler — enable + max gain (existing path) + decay. */}
      <div className="dsp-row" style={{ gap: 8 }}>
        <span className="label-xs" style={{ minWidth: 86 }}>
          Leveler
        </span>
        <button
          type="button"
          disabled={!connected}
          onClick={() =>
            send({ ...txLeveling, levelerEnabled: !txLeveling.levelerEnabled })
          }
          className={`btn sm ${txLeveling.levelerEnabled ? 'active' : ''}`}
          title="Toggle the TXA Leveler stage"
        >
          {txLeveling.levelerEnabled ? 'ON' : 'OFF'}
        </button>
      </div>
      <SliderField
        label="Max Gain"
        value={levelerMaxGainDb}
        min={0}
        max={20}
        step={0.5}
        unit="dB"
        disabled={!connected}
        onCommit={sendMaxGain}
      />
      <NumField
        label="Decay"
        value={txLeveling.levelerDecayMs}
        min={1}
        max={5000}
        unit="ms"
        disabled={!connected}
        onCommit={(v) => send({ ...txLeveling, levelerDecayMs: v })}
      />

      {/* Compressor (CPDR) — enable + gain. */}
      <div className="dsp-row" style={{ gap: 8 }}>
        <span className="label-xs" style={{ minWidth: 86 }}>
          Compressor
        </span>
        <button
          type="button"
          disabled={!connected}
          onClick={() =>
            send({
              ...txLeveling,
              compressorEnabled: !txLeveling.compressorEnabled,
            })
          }
          className={`btn sm ${txLeveling.compressorEnabled ? 'active' : ''}`}
          title="Toggle the TXA Compressor (CPDR) stage"
        >
          {txLeveling.compressorEnabled ? 'ON' : 'OFF'}
        </button>
      </div>
      <SliderField
        label="Gain"
        value={txLeveling.compressorGainDb}
        min={0}
        max={20}
        step={1}
        unit="dB"
        disabled={!connected}
        onCommit={(v) => send({ ...txLeveling, compressorGainDb: v })}
      />
    </div>
  );
}
