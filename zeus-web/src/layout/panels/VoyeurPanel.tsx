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
  cancelVoyeurInstall,
  getVoyeurInstallStatus,
  getVoyeurModels,
  getVoyeurStatus,
  getVoyeurTranscription,
  installVoyeurModel,
  listVoyeurSessions,
  startVoyeur,
  stopVoyeur,
  updateVoyeurSession,
  type VoyeurInstall,
  type VoyeurModel,
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
  const [asrReady, setAsrReady] = useState<boolean | null>(null);
  const [modelDir, setModelDir] = useState<string>('');
  const [showHelp, setShowHelp] = useState(false);
  const [models, setModels] = useState<VoyeurModel[]>([]);
  const [chosenModel, setChosenModel] = useState('small.en');
  const [install, setInstall] = useState<VoyeurInstall | null>(null);
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

  const refreshAsr = useCallback(async () => {
    try {
      const t = await getVoyeurTranscription();
      setAsrReady(t.available);
      setModelDir(t.modelDir);
    } catch {
      /* ignore */
    }
  }, []);

  useEffect(() => {
    void refreshAsr();
    void getVoyeurModels().then(setModels).catch(() => {});
    void getVoyeurInstallStatus().then(setInstall).catch(() => {});
  }, [refreshAsr]);

  // Poll install progress while a download is running; refresh ASR readiness
  // when it finishes (discovery is dynamic, so no restart needed).
  useEffect(() => {
    if (install?.phase !== 'Downloading') return;
    const h = setInterval(async () => {
      try {
        const s = await getVoyeurInstallStatus();
        setInstall(s);
        if (s.phase === 'Done') void refreshAsr();
      } catch {
        /* ignore */
      }
    }, 1000);
    return () => clearInterval(h);
  }, [install?.phase, refreshAsr]);

  const onInstall = async () => {
    try {
      setInstall(await installVoyeurModel(chosenModel));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  const onCancelInstall = async () => {
    try {
      setInstall(await cancelVoyeurInstall());
    } catch {
      /* ignore */
    }
  };

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

        {/* Transcription readiness + setup */}
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 8,
            marginBottom: 6,
            fontSize: 11,
          }}
        >
          <span
            style={{ color: asrReady ? 'var(--accent)' : 'var(--power)' }}
            title="Transcription needs whisper.cpp + a model. Without it, Voyeur Mode still captures overs; it just won't transcribe or identify callsigns."
          >
            {asrReady === null
              ? 'transcription: checking…'
              : asrReady
                ? '● transcription ready'
                : '○ transcription off (capture-only)'}
          </span>
          <button
            type="button"
            className="btn sm"
            onClick={() => setShowHelp((v) => !v)}
            aria-expanded={showHelp}
          >
            {showHelp ? 'Hide setup' : 'How to set up & use'}
          </button>
        </div>

        {showHelp && (
          <div
            style={{
              fontSize: 11,
              lineHeight: 1.5,
              padding: '8px 10px',
              marginBottom: 8,
              borderRadius: 4,
              background: 'var(--panel-bot)',
              border: '1px solid var(--panel-top)',
            }}
          >
            <div style={{ fontWeight: 600, marginBottom: 4 }}>What it does</div>
            Park the radio on a busy frequency (a net, a rag-chew) and press
            LISTEN. Voyeur Mode records each transmission, then — if transcription
            is set up — writes out what was said and who said it. Walk away; come
            back to a log of the activity.
            <div style={{ fontWeight: 600, margin: '8px 0 4px' }}>Using it</div>
            <ol style={{ margin: 0, paddingLeft: 18 }}>
              <li>Tune to the frequency you want to monitor (USB/LSB as normal).</li>
              <li>Press <strong>LISTEN</strong>. The status line shows overs being captured.</li>
              <li>Leave it running. Open a log anytime to read the transcript and roster.</li>
              <li>★ <strong>saves</strong> a log (protects it from auto-cleanup); ✕ <strong>deletes</strong> it and its audio. Click a name to rename.</li>
            </ol>
            <div style={{ fontWeight: 600, margin: '8px 0 4px' }}>
              Enable transcription (one-time, optional)
            </div>
            Transcription runs locally — audio never leaves your computer. Pick a
            speech model and click Download; no terminal needed. The bigger model
            is more accurate on noisy SSB; the smaller one downloads faster.
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, margin: '6px 0' }}>
              <select
                value={chosenModel}
                onChange={(e) => setChosenModel(e.target.value)}
                disabled={install?.phase === 'Downloading'}
                aria-label="Speech model"
                style={{ flex: 1, minWidth: 0 }}
              >
                {(models.length
                  ? models
                  : [
                      { id: 'small.en', label: 'Small (English) — fast' },
                      { id: 'medium.en', label: 'Medium (English) — most accurate' },
                    ]
                ).map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.label}
                  </option>
                ))}
              </select>
              {install?.phase === 'Downloading' ? (
                <button type="button" className="btn sm tx" onClick={onCancelInstall}>
                  Cancel
                </button>
              ) : (
                <button type="button" className="btn sm accent" onClick={onInstall}>
                  Download
                </button>
              )}
            </div>
            {install?.phase === 'Downloading' && (
              <div style={{ margin: '4px 0' }}>
                <div
                  style={{
                    height: 6,
                    borderRadius: 3,
                    background: 'var(--panel-top)',
                    overflow: 'hidden',
                  }}
                >
                  <div
                    style={{
                      width: `${install.percent}%`,
                      height: '100%',
                      background: 'var(--accent)',
                      transition: 'width 0.4s',
                    }}
                  />
                </div>
                <div style={{ fontSize: 10, opacity: 0.7, marginTop: 2 }}>{install.message}</div>
              </div>
            )}
            {install?.phase === 'Done' && (
              <div style={{ color: 'var(--accent)', fontSize: 11 }}>✓ {install.message}</div>
            )}
            {install?.phase === 'Error' && (
              <div style={{ color: 'var(--tx)', fontSize: 11 }}>Download failed: {install.message}</div>
            )}
            {install && !install.binaryPresent && (
              <div style={{ fontSize: 10, opacity: 0.7, marginTop: 4 }}>
                Note: the whisper engine for your platform ({install.rid}) ships
                with Zeus. If transcription stays off after the model downloads,
                the engine isn’t bundled in this build yet — advanced users can
                place a <code>whisper-cli</code> binary in{' '}
                <code style={{ wordBreak: 'break-all' }}>
                  {modelDir ? `${modelDir}/bin` : '…/Zeus/whisper/bin'}
                </code>
                .
              </div>
            )}
            <div style={{ fontWeight: 600, margin: '8px 0 4px' }}>
              Reading the roster
            </div>
            <span style={{ color: 'var(--accent)' }}>Blue</span> callsign = QRZ-confirmed
            (real licensee, name shown). <span style={{ color: 'var(--power)' }}>Amber</span>{' '}
            = heard but unverified. Grey “callsign unknown” = an over with no
            decodable ID. HF voice is noisy, so expect a useful gist — not a
            perfect transcript.
          </div>
        )}

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
                          {seg.callsignName ? ` (${seg.callsignName})` : ''}
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
