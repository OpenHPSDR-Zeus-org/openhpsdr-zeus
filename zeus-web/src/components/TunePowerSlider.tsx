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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback, useEffect, useRef } from 'react';
import { setTuneDrive } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { usePaStore } from '../state/pa-store';

// Same 0..100 range and debounce shape as DriveSlider; the backend picks
// between the two sources based on TUN keying state so the UX/wire are
// symmetric.
const MIN = 0;
const MAX = 100;
const DEBOUNCE_MS = 100;

export function TunePowerSlider() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const tunePercent = useTxStore((s) => s.tunePercent);
  const setTunePercent = useTxStore((s) => s.setTunePercent);
  // Live target-watts readout (see DriveSlider for rationale).
  const paMaxWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const targetWatts = paMaxWatts > 0 ? Math.round((paMaxWatts * tunePercent) / 100) : null;

  const inflightAbort = useRef<AbortController | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef<number>(tunePercent);
  const previousOnError = useRef<number>(tunePercent);

  const sendDebounced = useCallback((v: number) => {
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
    debounceTimer.current = setTimeout(() => {
      if (v === lastSent.current) return;
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      const prevValue = lastSent.current;
      lastSent.current = v;
      previousOnError.current = prevValue;
      setTuneDrive(v, ac.signal)
        .then((r) => {
          if (ac.signal.aborted) return;
          if (r.tunePercent !== v) setTunePercent(r.tunePercent);
        })
        .catch((err) => {
          if (ac.signal.aborted) return;
          if (err instanceof DOMException && err.name === 'AbortError') return;
          setTunePercent(previousOnError.current);
          lastSent.current = previousOnError.current;
        });
    }, DEBOUNCE_MS);
  }, [setTunePercent]);

  useEffect(() => () => {
    inflightAbort.current?.abort();
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
  }, []);

  // Server is authoritative for tunePercent (StateDto.TunePct, persisted via
  // RadioStateStore, hydrated into tx-store on every fresh RadioStateDto).
  // The previous push-on-connect clobbered the server's hydrated value with
  // the localStorage mirror. Removed deliberately — see DriveSlider.tsx.

  const onChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setTunePercent(v);
    sendDebounced(v);
  };

  return (
    <label className="knob-group">
      <span className="label-xs">TUN</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={tunePercent}
        disabled={!connected}
        onChange={onChange}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span
        className="mono"
        style={{
          width: 40,
          textAlign: 'right',
          color: 'var(--power)',
          fontSize: 11,
          fontWeight: 700,
        }}
      >
        {tunePercent}%
      </span>
      {targetWatts !== null && (
        <span
          className="mono"
          style={{ width: 44, textAlign: 'right', color: 'var(--neutral-400, #888)', fontSize: 10 }}
          title="Target tune watts = Rated PA Output × Tune %"
        >
          ~{targetWatts} W
        </span>
      )}
    </label>
  );
}
