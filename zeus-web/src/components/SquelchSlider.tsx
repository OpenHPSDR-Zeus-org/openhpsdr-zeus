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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing and WDSP integration were informed by
// studying the Thetis project (the authoritative OpenHPSDR reference).
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Inline RX squelch control: SQL on/off + threshold slider. A single
// mode-aware control — the server routes run + threshold to the WDSP squelch
// stage matching the current RX mode (SSB/CW → SSQL, AM/SAM → AMSQ, FM → FMSQ).
// Level 0..100 in fixed mode, higher = tighter. DYN mode is automatic and owns
// the threshold. Mirrors AgcSlider's toolbar idiom (live-slider stream-on-drag,
// optimistic send + applyState reconcile).

import { useCallback, useEffect, useRef, useState } from 'react';
import { setSquelch } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

const MIN = 0;
const MAX = 100;

export function SquelchSlider() {
  const squelch = useConnectionStore((s) => s.squelch);
  const setLocalSquelch = useConnectionStore((s) => s.setSquelch);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  // Local drag state overrides the store while the user is dragging so echoed
  // state updates don't yank the thumb back (same pattern as AgcSlider).
  const [dragValue, setDragValue] = useState<number | null>(null);
  const sliderValue = dragValue ?? squelch.level;
  // FIX/DYN and the threshold are meaningless while SQL is OFF; gray them out
  // so the operator can't dial a phantom setting that won't affect audio.
  const modeDisabled = !connected || !squelch.enabled;
  const levelDisabled = modeDisabled || squelch.adaptive;

  const toggleAbort = useRef<AbortController | null>(null);

  // Stream the threshold during drag, flush on release. Read the live config
  // from the store inside send() so a concurrent on/off toggle isn't lost to a
  // stale closure.
  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) => {
        const cur = useConnectionStore.getState().squelch;
        return setSquelch({ ...cur, level: v }, signal)
          .then((next) => {
            if (!signal.aborted) applyState(next);
          })
          .catch(() => {
            /* next poll will reconcile; don't log on abort */
          });
      },
      [applyState],
    ),
  });

  const toggle = useCallback(() => {
    if (!connected) return;
    toggleAbort.current?.abort();
    const ac = new AbortController();
    toggleAbort.current = ac;
    const next = { ...squelch, enabled: !squelch.enabled };
    setLocalSquelch(next);
    setSquelch(next, ac.signal)
      .then((s) => {
        if (!ac.signal.aborted) applyState(s);
      })
      .catch(() => {
        /* state subscription will reconcile on next broadcast */
      });
  }, [squelch, connected, setLocalSquelch, applyState]);

  const toggleAdaptive = useCallback(() => {
    if (!connected || !squelch.enabled) return;
    toggleAbort.current?.abort();
    const ac = new AbortController();
    toggleAbort.current = ac;
    const next = { ...squelch, adaptive: !squelch.adaptive };
    setLocalSquelch(next);
    setSquelch(next, ac.signal)
      .then((s) => {
        if (!ac.signal.aborted) applyState(s);
      })
      .catch(() => {
        /* state subscription will reconcile on next broadcast */
      });
  }, [squelch, connected, setLocalSquelch, applyState]);

  useEffect(
    () => () => {
      toggleAbort.current?.abort();
    },
    [],
  );

  return (
    <label className="knob-group" style={{ minWidth: 210 }}>
      <button
        type="button"
        onClick={toggle}
        disabled={!connected}
        aria-pressed={squelch.enabled}
        aria-label={squelch.enabled ? 'Squelch on' : 'Squelch off'}
        title={
          squelch.enabled
            ? 'Squelch ON (mode-aware; click to disable)'
            : 'Squelch OFF (click to enable)'
        }
        className={`btn sm ${squelch.enabled ? 'active' : ''}`}
        style={{ whiteSpace: 'nowrap' }}
      >
        SQL
      </button>
      <button
        type="button"
        onClick={toggleAdaptive}
        disabled={modeDisabled}
        aria-pressed={squelch.enabled && squelch.adaptive}
        aria-label={squelch.adaptive ? 'Adaptive squelch' : 'Fixed squelch'}
        title={
          !squelch.enabled
            ? 'Enable SQL to switch between FIX and DYN'
            : squelch.adaptive
              ? 'Adaptive squelch tracks the noise floor'
              : 'Fixed WDSP squelch threshold'
        }
        className={`btn sm ${squelch.enabled && squelch.adaptive ? 'active' : ''}`}
        style={{ whiteSpace: 'nowrap', minWidth: 42 }}
      >
        {squelch.adaptive ? 'DYN' : 'FIX'}
      </button>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={sliderValue}
        disabled={levelDisabled}
        title={squelch.adaptive ? 'DYN mode tracks the noise floor automatically' : 'Fixed squelch threshold'}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          setDragValue(v);
          setLocalSquelch({ ...squelch, level: v });
          liveSlider.push(v);
        }}
        onMouseUp={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        onTouchEnd={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        onKeyUp={() => {
          liveSlider.flush();
          setDragValue(null);
        }}
        style={{
          flex: 1,
          cursor: levelDisabled ? 'not-allowed' : 'pointer',
          accentColor: levelDisabled ? 'var(--fg-3)' : 'var(--accent)',
          opacity: levelDisabled ? 0.55 : 1,
        }}
      />
      <span
        className="mono"
        style={{
          width: 36,
          textAlign: 'right',
          color: squelch.adaptive ? 'var(--fg-3)' : 'var(--fg-1)',
          fontSize: 11,
        }}
      >
        {squelch.adaptive ? 'AUTO' : sliderValue}
      </span>
    </label>
  );
}
