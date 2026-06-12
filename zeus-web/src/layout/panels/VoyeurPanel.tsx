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
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

import { useCallback, useEffect, useRef, useState } from 'react';
import { TileChrome } from '../TileChrome';
import type { PanelComponentProps } from '../panels';
import {
  deleteVoyeurSession,
  getVoyeurSession,
  getVoyeurStatus,
  listVoyeurSessions,
  startVoyeur,
  stopVoyeur,
  updateVoyeurSession,
  type VoyeurSession,
  type VoyeurSessionDetail,
  type VoyeurStatus,
} from '../../api/voyeur';

// Voyeur Mode (zeus-la5) — Phase 1 panel. Park the radio on a frequency, let
// the backend capture each transmission ("over") to a log, then review / save
// / delete those logs. The transcript + callsign columns light up in Phase 2
// (ASR); Phase 1 surfaces the captured-over metadata and the management UI.
//
// Visual design is intentionally restrained and token-only (KB2UKA owns all
// design decisions on this repo); structure first, polish on his call.

function fmtFreq(hz: number): string {
  return `${(hz / 1_000_000).toFixed(3)} MHz`;
}
function fmtDur(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s}s` : `${s}s`;
}
function fmtWhen(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function VoyeurPanel({ onRemove }: PanelComponentProps) {
  const handleRemove = onRemove ?? (() => {});
  const [status, setStatus] = useState<VoyeurStatus | null>(null);
  const [sessions, setSessions] = useState<VoyeurSession[]>([]);
  const [openId, setOpenId] = useState<string | null>(null);
  const [detail, setDetail] = useState<VoyeurSessionDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const editingRef = useRef<HTMLInputElement | null>(null);

  const refreshSessions = useCallback(async () => {
    try {
      setSessions(await listVoyeurSessions());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  // Poll status while mounted (1 Hz — cheap, and shows the live segment count
  // and the safety drop counter climbing during a session).
  useEffect(() => {
    let alive = true;
    const tick = async () => {
      try {
        const s = await getVoyeurStatus();
        if (alive) setStatus(s);
      } catch {
        /* transient */
      }
    };
    void tick();
    const h = setInterval(tick, 1000);
    return () => {
      alive = false;
      clearInterval(h);
    };
  }, []);

  useEffect(() => {
    void refreshSessions();
  }, [refreshSessions]);

  // When a session is active, refresh the list as its segment count grows.
  useEffect(() => {
    if (!status?.active) return;
    const h = setInterval(() => void refreshSessions(), 3000);
    return () => clearInterval(h);
  }, [status?.active, refreshSessions]);

  const onStart = async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await startVoyeur(true));
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const onStop = async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await stopVoyeur());
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const openSession = async (id: string) => {
    if (openId === id) {
      setOpenId(null);
      setDetail(null);
      return;
    }
    setOpenId(id);
    setDetail(null);
    try {
      setDetail(await getVoyeurSession(id));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onTogglePin = async (s: VoyeurSession) => {
    try {
      await updateVoyeurSession(s.id, { pinned: !s.pinned });
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onRename = async (s: VoyeurSession, label: string) => {
    const trimmed = label.trim();
    if (!trimmed || trimmed === s.label) return;
    try {
      await updateVoyeurSession(s.id, { label: trimmed });
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const onDelete = async (s: VoyeurSession) => {
    if (!window.confirm(`Delete log "${s.label}"? This removes its captured audio too.`)) return;
    try {
      await deleteVoyeurSession(s.id);
      if (openId === s.id) {
        setOpenId(null);
        setDetail(null);
      }
      await refreshSessions();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const active = status?.active ?? false;

  return (
    <>
      <TileChrome
        title="Voyeur Mode"
        onRemove={handleRemove}
        rightSlot={
          <button
            type="button"
            className={`btn ${active ? 'tx' : 'accent'}`}
            disabled={busy}
            onClick={active ? onStop : onStart}
            aria-label={active ? 'Stop monitoring' : 'Start monitoring'}
          >
            {active ? 'STOP' : 'LISTEN'}
          </button>
        }
      />
      <div style={{ padding: '8px 10px', overflow: 'auto', fontSize: 12 }}>
        {/* Live status */}
        <div
          style={{
            display: 'flex',
            gap: 12,
            flexWrap: 'wrap',
            padding: '6px 8px',
            borderRadius: 4,
            background: 'var(--panel-bot)',
            marginBottom: 8,
          }}
        >
          {active && status ? (
            <>
              <span>
                <strong style={{ color: 'var(--accent)' }}>● listening</strong>{' '}
                {fmtFreq(status.freqHz)} {status.mode} {status.band}
              </span>
              <span>overs: {status.segmentCount}</span>
              <span>captured: {fmtDur(status.capturedSeconds)}</span>
              {status.droppedSamples > 0 && (
                <span title="Samples dropped because the CPU briefly fell behind. RX is unaffected — this is just a capture gap.">
                  dropped: {status.droppedSamples}
                </span>
              )}
              {status.degraded && (
                <span style={{ color: 'var(--tx)' }} title="The monitor faulted and detached. RX is unaffected.">
                  degraded
                </span>
              )}
            </>
          ) : (
            <span style={{ opacity: 0.7 }}>
              Idle — press LISTEN to log the current frequency. Phase 1 captures each
              transmission; transcription + callsigns arrive in Phase 2.
            </span>
          )}
        </div>

        {error && (
          <div style={{ color: 'var(--tx)', marginBottom: 6 }}>{error}</div>
        )}

        {/* Saved logs */}
        <div style={{ fontWeight: 600, margin: '4px 0', opacity: 0.8 }}>
          Logs ({sessions.length})
        </div>
        {sessions.length === 0 && (
          <div style={{ opacity: 0.6 }}>No logs yet.</div>
        )}
        {sessions.map((s) => (
          <div
            key={s.id}
            style={{
              borderTop: '1px solid var(--panel-top)',
              padding: '4px 0',
            }}
          >
            <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <button
                type="button"
                className="btn sm"
                title={s.pinned ? 'Saved (won’t be auto-pruned). Click to unsave.' : 'Save this log'}
                onClick={() => onTogglePin(s)}
                style={{ color: s.pinned ? 'var(--power)' : undefined }}
              >
                {s.pinned ? '★' : '☆'}
              </button>
              <input
                ref={editingRef}
                defaultValue={s.label}
                onBlur={(e) => onRename(s, e.currentTarget.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') e.currentTarget.blur();
                }}
                style={{
                  flex: 1,
                  minWidth: 0,
                  background: 'transparent',
                  border: 'none',
                  color: 'inherit',
                  font: 'inherit',
                }}
                aria-label="Log name"
              />
              <button type="button" className="btn sm" onClick={() => openSession(s.id)}>
                {openId === s.id ? 'Hide' : 'Open'}
              </button>
              <button
                type="button"
                className="btn sm tx"
                title="Delete this log and its audio"
                onClick={() => onDelete(s)}
              >
                ✕
              </button>
            </div>
            <div style={{ opacity: 0.65, fontSize: 11, paddingLeft: 30 }}>
              {fmtWhen(s.startedUtc)} · {fmtFreq(s.freqHz)} {s.mode} · {s.segmentCount} overs ·{' '}
              {fmtDur(s.capturedSeconds)}
              {s.hasAudio ? ' · audio' : ''}
            </div>

            {openId === s.id && (
              <div style={{ paddingLeft: 30, marginTop: 4 }}>
                {!detail && <div style={{ opacity: 0.6 }}>Loading…</div>}
                {detail && detail.segments.length === 0 && (
                  <div style={{ opacity: 0.6 }}>No overs captured.</div>
                )}
                {detail &&
                  detail.segments.map((seg, i) => (
                    <div
                      key={seg.id}
                      style={{
                        display: 'grid',
                        gridTemplateColumns: 'auto 1fr',
                        gap: 8,
                        padding: '2px 0',
                        borderTop: i === 0 ? 'none' : '1px dotted var(--panel-top)',
                      }}
                    >
                      <span style={{ opacity: 0.7, fontVariantNumeric: 'tabular-nums' }}>
                        {new Date(seg.startedUtc).toLocaleTimeString()} ·{' '}
                        {(seg.durationMs / 1000).toFixed(1)}s
                      </span>
                      <span>
                        {/* Phase 2: callsign + transcript. Phase 1: the over
                            with no attribution yet. */}
                        <strong
                          style={{
                            color:
                              seg.callsignState === 'confirmed'
                                ? 'var(--accent)'
                                : seg.callsignState === 'tentative'
                                  ? 'var(--power)'
                                  : 'inherit',
                            opacity: seg.callsign ? 1 : 0.5,
                          }}
                        >
                          {seg.callsign ?? 'callsign unknown'}
                        </strong>
                        {seg.transcript ? ` — ${seg.transcript}` : ''}
                      </span>
                    </div>
                  ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </>
  );
}
