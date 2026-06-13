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

import { useCallback, useEffect, useRef, useState } from 'react';
import './WavRecorderPanel.css';

type Status = {
  state: 'idle' | 'recording' | 'playing';
  source: 'rx' | 'tx';
  file: string | null;
  seconds: number;
  mox: boolean;
  onAir: boolean;
};

type Recording = { name: string; bytes: number; modifiedUnixMs: number };

async function post(path: string, body?: unknown): Promise<void> {
  await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
}

function fmtBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(0)} KB`;
  return `${(b / 1024 / 1024).toFixed(1)} MB`;
}

/** A single tape reel: metal flange with three windows revealing the spooled
 *  tape, a central spindle. The inner group spins via CSS when active. */
function Reel({ active }: { active: 'rec' | 'play' | null }) {
  const spinClass =
    active === 'rec' ? 'reel__spin is-rec' : active === 'play' ? 'reel__spin is-play' : 'reel__spin';
  return (
    <svg className="reel" viewBox="0 0 100 100" aria-hidden="true">
      {/* tape pack (revealed through the flange windows) */}
      <circle cx="50" cy="50" r="46" fill="var(--tape)" />
      <circle cx="50" cy="50" r="46" fill="none" stroke="var(--tape-edge)" strokeWidth="1.5" />
      <g className={spinClass}>
        {/* metal flange */}
        <circle cx="50" cy="50" r="44" fill="var(--flange)" />
        <circle cx="50" cy="50" r="44" fill="none" stroke="var(--flange-shade)" strokeWidth="2" />
        {/* three windows showing tape underneath */}
        {[0, 120, 240].map((deg) => {
          const rad = (deg * Math.PI) / 180;
          const cx = 50 + 26 * Math.cos(rad);
          const cy = 50 + 26 * Math.sin(rad);
          return <circle key={deg} cx={cx} cy={cy} r="11" fill="var(--tape)" stroke="var(--flange-shade)" strokeWidth="1" />;
        })}
        {/* hub + spindle */}
        <circle cx="50" cy="50" r="13" fill="var(--hub)" stroke="var(--flange-shade)" strokeWidth="1.5" />
        <circle cx="50" cy="50" r="3.5" fill="#0c0d0e" />
        {/* a spoke so rotation is visible */}
        <rect x="49" y="6" width="2" height="10" fill="var(--flange-shade)" />
      </g>
    </svg>
  );
}

export function WavRecorderPanel() {
  const [status, setStatus] = useState<Status>({
    state: 'idle', source: 'rx', file: null, seconds: 0, mox: false, onAir: false,
  });
  const [list, setList] = useState<Recording[]>([]);
  const [source, setSource] = useState<'rx' | 'tx'>('rx');
  const timer = useRef<number | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [s, l] = await Promise.all([
        fetch('/api/wav/status').then((r) => r.json()),
        fetch('/api/wav/list').then((r) => r.json()),
      ]);
      setStatus(s as Status);
      setList((l.recordings ?? []) as Recording[]);
    } catch {
      /* transient — next tick retries */
    }
  }, []);

  useEffect(() => {
    refresh();
    timer.current = window.setInterval(refresh, 1000);
    return () => {
      if (timer.current !== null) window.clearInterval(timer.current);
    };
  }, [refresh]);

  const recording = status.state === 'recording';
  const playing = status.state === 'playing';
  const idle = status.state === 'idle';
  const reelActive: 'rec' | 'play' | null = recording ? 'rec' : playing ? 'play' : null;

  const onRec = async () => {
    if (recording) await post('/api/wav/record/stop');
    else await post('/api/wav/record/start', { source });
    refresh();
  };
  const onPlay = async (file: string) => {
    if (playing) await post('/api/wav/stop');
    else await post('/api/wav/play', { file });
    refresh();
  };
  const onDelete = async (file: string) => {
    await fetch(`/api/wav/${encodeURIComponent(file)}`, { method: 'DELETE' });
    refresh();
  };

  const counter = recording ? status.seconds : 0;

  return (
    <div className="reel-deck">
      <div className="reel-deck__deck">
        <div className="reel-deck__reels">
          <div className="reel-deck__tape-path" />
          <Reel active={reelActive} />
          <div className="reel-deck__counter" style={{ alignSelf: 'center' }}>
            <span className={recording ? 'reel-deck__counter is-rec' : 'reel-deck__counter'}>
              {String(Math.floor(counter / 60)).padStart(2, '0')}:
              {String(Math.floor(counter % 60)).padStart(2, '0')}
            </span>
          </div>
          <Reel active={reelActive} />
        </div>

        <div className="reel-deck__transport">
          <button
            className={recording ? 'reel-btn is-rec-active' : 'reel-btn'}
            onClick={onRec}
            disabled={playing}
            title={recording ? 'Stop recording' : 'Start recording'}
          >
            {recording ? '■ STOP' : '● REC'}
          </button>

          <div style={{ display: 'flex', gap: 2, opacity: idle ? 1 : 0.5 }}>
            {(['rx', 'tx'] as const).map((s) => (
              <button
                key={s}
                className={source === s ? 'reel-btn is-on' : 'reel-btn'}
                onClick={() => setSource(s)}
                disabled={!idle}
                style={{ padding: '5px 9px' }}
                title={s === 'rx' ? 'Record received audio' : 'Record your mic (transmit audio) — silent, no monitor'}
              >
                {s.toUpperCase()}
              </button>
            ))}
          </div>

          <span
            style={{
              marginLeft: 'auto',
              fontWeight: status.onAir ? 700 : 400,
              color: status.onAir ? 'var(--tx, #e63a2b)' : 'var(--text)',
              opacity: status.onAir ? 1 : 0.8,
            }}
          >
            {recording
              ? `REC ${status.source.toUpperCase()}`
              : status.onAir
                ? '🔴 ON AIR'
                : playing
                  ? '▶ playing…'
                  : status.mox
                    ? 'MOX up — Play transmits'
                    : 'idle'}
          </span>
        </div>
      </div>

      {/* Recordings */}
      <div className="reel-deck__list">
        {list.length === 0 && (
          <div className="reel-deck__hint" style={{ padding: '6px 2px' }}>
            No recordings yet. Hit REC to capture {source.toUpperCase()} audio.
          </div>
        )}
        {list.map((r) => {
          const isThis = playing && !!status.file && status.file.endsWith(r.name);
          return (
            <div key={r.name} className={isThis ? 'reel-deck__row is-playing' : 'reel-deck__row'}>
              <button
                className="reel-btn"
                onClick={() => onPlay(r.name)}
                disabled={recording}
                style={{ padding: '2px 8px', minWidth: 38 }}
                title={
                  isThis
                    ? 'Stop playback'
                    : status.mox
                      ? 'TRANSMIT this recording (MOX is on)'
                      : 'Play locally (no transmit)'
                }
              >
                {isThis ? '■' : status.mox ? '📡' : '▶'}
              </button>
              <span className="reel-deck__name" title={r.name}>
                {r.name.replace(/^zeus-/, '')}
              </span>
              <span style={{ opacity: 0.55, fontSize: 11, fontVariantNumeric: 'tabular-nums' }}>
                {fmtBytes(r.bytes)}
              </span>
              <button
                className="reel-btn"
                onClick={() => onDelete(r.name)}
                disabled={recording || isThis}
                style={{ padding: '2px 7px', opacity: 0.7, fontWeight: 400 }}
                title="Delete recording"
              >
                ✕
              </button>
            </div>
          );
        })}
      </div>

      <div className="reel-deck__hint">
        Saves to Downloads · float32 WAV · Play with <strong>MOX up = on the air</strong>, MOX off = local
      </div>
    </div>
  );
}
