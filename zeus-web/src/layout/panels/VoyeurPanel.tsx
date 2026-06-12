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
import './voyeur.css';
import {
  deleteVoyeurSession,
  getVoyeurSession,
  cancelVoyeurInstall,
  getVoyeurInstallStatus,
  getVoyeurModels,
  getVoyeurReport,
  getVoyeurStatus,
  getVoyeurTranscription,
  installVoyeurModel,
  listVoyeurSessions,
  searchVoyeur,
  voyeurSegmentAudioUrl,
  startVoyeur,
  stopVoyeur,
  updateVoyeurSession,
  type VoyeurInstall,
  type VoyeurModel,
  type VoyeurReport,
  type VoyeurSearchHit,
  type VoyeurSegment,
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
  const [chosenModel, setChosenModel] = useState('medium.en');
  const [install, setInstall] = useState<VoyeurInstall | null>(null);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<VoyeurSearchHit[] | null>(null);
  const [reports, setReports] = useState<Record<string, VoyeurReport>>({});
  const [view, setView] = useState<Record<string, 'log' | 'roster'>>({});
  const [playing, setPlaying] = useState<string | null>(null);
  const editingRef = useRef<HTMLInputElement | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);

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

  // Search across all logs (debounced). Empty query → normal session list.
  useEffect(() => {
    const q = query.trim();
    if (!q) {
      setHits(null);
      return;
    }
    const ctrl = new AbortController();
    const h = setTimeout(() => {
      void searchVoyeur(q, ctrl.signal)
        .then(setHits)
        .catch(() => {});
    }, 250);
    return () => {
      clearTimeout(h);
      ctrl.abort();
    };
  }, [query]);

  const loadReport = useCallback(async (id: string) => {
    try {
      const r = await getVoyeurReport(id);
      setReports((m) => ({ ...m, [id]: r }));
    } catch {
      /* ignore */
    }
  }, []);

  const setSessionView = (id: string, mode: 'log' | 'roster') => {
    setView((v) => ({ ...v, [id]: mode }));
    if (mode === 'roster' && !reports[id]) void loadReport(id);
  };

  const playSegment = (segId: string) => {
    let el = audioRef.current;
    if (!el) {
      el = new Audio();
      el.onended = () => setPlaying(null);
      audioRef.current = el;
    }
    if (playing === segId) {
      el.pause();
      setPlaying(null);
      return;
    }
    el.src = voyeurSegmentAudioUrl(segId);
    void el.play().then(() => setPlaying(segId)).catch(() => setPlaying(null));
  };

  const renderOver = (seg: VoyeurSegment) => {
    const state = seg.callsignState ?? 'unknown';
    return (
      <div key={seg.id} className={`voyeur-over voyeur-over--${state}`}>
        <span className="voyeur-over__time">
          {new Date(seg.startedUtc).toLocaleTimeString([], {
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
          })}
          <br />
          {(seg.durationMs / 1000).toFixed(0)}s
        </span>
        <span className="voyeur-over__body">
          {seg.hasAudio && (
            <button
              type="button"
              className={`voyeur-play ${playing === seg.id ? 'voyeur-play--on' : ''}`}
              onClick={() => playSegment(seg.id)}
              title="Play this over"
              aria-label="Play this over"
            >
              {playing === seg.id ? '⏸' : '▶'}
            </button>
          )}
          <span className={`voyeur-call voyeur-call--${state}`}>{seg.callsign ?? 'unknown'}</span>
          {seg.callsignName && <span className="voyeur-name">{seg.callsignName}</span>}
          {seg.transcript ? (
            <span className="voyeur-text">{seg.transcript}</span>
          ) : (
            <span className="voyeur-text voyeur-text--pending">
              {asrReady ? 'transcribing…' : 'audio captured'}
            </span>
          )}
        </span>
      </div>
    );
  };

  const renderRoster = (id: string) => {
    const r = reports[id];
    if (!r) return <div className="voyeur-empty" style={{ padding: '6px 10px' }}>Loading…</div>;
    return (
      <div className="voyeur-roster">
        <div className="voyeur-roster__stats">
          <span className="chip mono"><span className="k">stations</span><span className="v">{r.uniqueStations}</span></span>
          <span className="chip mono"><span className="k">confirmed</span><span className="v">{r.confirmedStations}</span></span>
          <span className="chip mono"><span className="k">overs</span><span className="v">{r.session.segmentCount}</span></span>
          <span className="chip mono"><span className="k">cap</span><span className="v">{fmtDur(r.session.capturedSeconds)}</span></span>
        </div>
        {r.digest && <div className="voyeur-digest">{r.digest}</div>}
        {r.roster.length === 0 && (
          <div className="voyeur-empty" style={{ padding: '4px 10px' }}>No callsigns identified.</div>
        )}
        {r.roster.map((e) => (
          <div key={e.callsign} className="voyeur-rosteritem">
            <span className={`voyeur-call voyeur-call--${e.state}`}>{e.callsign}</span>
            {e.name && <span className="voyeur-name">{e.name}</span>}
            <span className="voyeur-rosteritem__count">
              {e.overCount} {e.overCount === 1 ? 'over' : 'overs'}
            </span>
          </div>
        ))}
      </div>
    );
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
      <div className="voyeur">
        <div className="voyeur__controls">
          {/* Live receiver bar */}
          <div className={`voyeur-live ${active ? 'voyeur-live--on' : ''}`}>
            {active && status ? (
              <>
                <span className="voyeur-rec">
                  <span className="voyeur-rec__dot" />
                  Listening
                </span>
                <span className="voyeur-freq">{fmtFreq(status.freqHz)}</span>
                <span className="voyeur-mode">
                  {status.mode} · {status.band}
                </span>
                <span className="status-chips" style={{ marginLeft: 'auto' }}>
                  <span className="chip mono">
                    <span className="k">overs</span>
                    <span className="v">{status.segmentCount}</span>
                  </span>
                  <span className="chip mono">
                    <span className="k">cap</span>
                    <span className="v">{fmtDur(status.capturedSeconds)}</span>
                  </span>
                  {status.droppedSamples > 0 && (
                    <span
                      className="chip mono"
                      title="Samples dropped because the CPU briefly fell behind. RX is unaffected — just a capture gap."
                    >
                      <span className="k">drop</span>
                      <span className="v">{status.droppedSamples}</span>
                    </span>
                  )}
                  {status.degraded && (
                    <span className="chip tx" title="The monitor faulted and detached. RX is unaffected.">
                      <span className="v">degraded</span>
                    </span>
                  )}
                </span>
              </>
            ) : (
              <span className="voyeur-idle">
                Idle — tune to a busy frequency and press <strong>LISTEN</strong> to
                log who’s on and what’s said.
              </span>
            )}
          </div>

          {/* Transcription status + setup toggle */}
          <div className="voyeur-row">
            <span
              className={`voyeur-asr ${asrReady ? 'voyeur-asr--on' : 'voyeur-asr--off'}`}
              title="Transcription runs locally via whisper. Without it, Voyeur Mode still captures overs; it just won't transcribe or identify callsigns."
            >
              <span className="voyeur-asr__dot" />
              {asrReady === null ? 'checking…' : asrReady ? 'transcription on' : 'transcription off'}
            </span>
            <span style={{ flex: 1 }} />
            <button
              type="button"
              className="btn sm"
              onClick={() => setShowHelp((v) => !v)}
              aria-expanded={showHelp}
            >
              {showHelp ? 'Hide setup' : 'How to set up & use'}
            </button>
          </div>

          {/* Prominent model-download control whenever transcription is off */}
          {asrReady === false && (
            <div className="voyeur-dl">
              {install?.phase === 'Downloading' ? (
                <>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="voyeur-bar">
                      <div className="voyeur-bar__fill" style={{ width: `${install.percent}%` }} />
                    </div>
                    <div className="voyeur-dl__msg">{install.message}</div>
                  </div>
                  <button type="button" className="btn sm tx" onClick={onCancelInstall}>
                    Cancel
                  </button>
                </>
              ) : (
                <>
                  <span className="voyeur-dl__label">Speech model:</span>
                  <select
                    value={chosenModel}
                    onChange={(e) => setChosenModel(e.target.value)}
                    aria-label="Speech model"
                    style={{ flex: 1, minWidth: 0 }}
                  >
                    {(models.length
                      ? models
                      : [
                          { id: 'medium.en', label: 'Medium — recommended' },
                          { id: 'small.en', label: 'Small — faster download' },
                        ]
                    ).map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.label}
                      </option>
                    ))}
                  </select>
                  <button type="button" className="btn sm accent" onClick={onInstall}>
                    Download
                  </button>
                </>
              )}
            </div>
          )}

          {showHelp && (
            <div className="voyeur-help">
              <h4>What it does</h4>
              Park the radio on a busy frequency (a net, a rag-chew) and press
              LISTEN. Voyeur Mode records each transmission, then — if transcription
              is set up — writes out what was said and who said it. Walk away; come
              back to a log of the activity.
              <h4>Using it</h4>
              <ol>
                <li>Tune to the frequency you want to monitor (USB/LSB as normal).</li>
                <li>
                  Press <strong>LISTEN</strong>. The live bar shows overs being captured.
                </li>
                <li>Leave it running. Open a log anytime to read the transcript and roster.</li>
                <li>
                  ★ <strong>saves</strong> a log (protects it from auto-cleanup); ✕{' '}
                  <strong>deletes</strong> it and its audio. Click a name to rename.
                </li>
              </ol>
              <h4>Transcription (one-time, optional)</h4>
              Runs locally — audio never leaves your computer. Pick a model above and
              click Download (no terminal). The bigger model is more accurate on noisy
              SSB; the smaller one downloads faster. You only download once.
              {install?.phase === 'Done' && (
                <div style={{ color: 'var(--green-soft)', marginTop: 4 }}>✓ {install.message}</div>
              )}
              {install?.phase === 'Error' && (
                <div className="voyeur-error" style={{ marginTop: 4 }}>
                  Download failed: {install.message}
                </div>
              )}
              {install && !install.binaryPresent && (
                <div style={{ fontSize: 10, color: 'var(--fg-3)', marginTop: 6 }}>
                  Note: the whisper engine for your platform ({install.rid}) ships with
                  Zeus. If transcription stays off after the model downloads, the engine
                  isn’t bundled in this build yet — advanced users can place a{' '}
                  <code>whisper-cli</code> binary in{' '}
                  <code>{modelDir ? `${modelDir}/bin` : '…/Zeus/whisper/bin'}</code>.
                </div>
              )}
              <h4>Reading the roster</h4>
              <span style={{ color: 'var(--accent)' }}>Blue</span> = QRZ-confirmed (real
              licensee, name shown). <span style={{ color: 'var(--power)' }}>Amber</span> =
              heard but unverified. Grey = no decodable callsign. HF voice is noisy, so
              expect a useful gist — not a perfect transcript.
            </div>
          )}

          {error && <div className="voyeur-error">{error}</div>}
        </div>

        {/* The intercepted-comms log */}
        <div className="voyeur__log">
          <div className="voyeur-loghdr">
            <span>Logs · {sessions.length}</span>
            <input
              className="voyeur-search"
              type="search"
              placeholder="search callsign or text…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label="Search logs"
            />
          </div>

          {hits !== null && (
            <>
              {hits.length === 0 && (
                <div className="voyeur-empty">No matches for “{query}”.</div>
              )}
              {hits.map((hit) => (
                <div className="voyeur-card" key={hit.sessionId}>
                  <div className="voyeur-card__meta" style={{ paddingTop: 6 }}>
                    <span className="chip mono"><span className="v">{fmtFreq(hit.freqHz)}</span></span>
                    <span className="chip mono"><span className="k">when</span><span className="v">{fmtWhen(hit.startedUtc)}</span></span>
                    <span className="chip mono"><span className="k">hits</span><span className="v">{hit.matches.length}</span></span>
                    <span style={{ flex: 1 }} />
                    <button
                      type="button"
                      className="btn sm"
                      onClick={() => {
                        setQuery('');
                        void openSession(hit.sessionId);
                      }}
                    >
                      Open log
                    </button>
                  </div>
                  <div className="voyeur-overs">{hit.matches.map(renderOver)}</div>
                </div>
              ))}
            </>
          )}

          {hits === null && sessions.length === 0 && (
            <div className="voyeur-empty">No logs yet — press LISTEN.</div>
          )}
          {hits === null &&
            sessions.map((s) => (
            <div className="voyeur-card" key={s.id}>
              <div className="voyeur-card__head">
                <button
                  type="button"
                  className={`voyeur-pin ${s.pinned ? 'voyeur-pin--on' : ''}`}
                  title={s.pinned ? 'Saved (won’t be auto-pruned). Click to unsave.' : 'Save this log'}
                  onClick={() => onTogglePin(s)}
                >
                  {s.pinned ? '★' : '☆'}
                </button>
                <input
                  ref={editingRef}
                  className="voyeur-card__name"
                  defaultValue={s.label}
                  onBlur={(e) => onRename(s, e.currentTarget.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') e.currentTarget.blur();
                  }}
                  aria-label="Log name"
                />
                {openId === s.id && (
                  <div className="voyeur-viewtoggle">
                    <button
                      type="button"
                      className={`btn sm ${(view[s.id] ?? 'log') === 'log' ? 'active' : ''}`}
                      onClick={() => setSessionView(s.id, 'log')}
                    >
                      Log
                    </button>
                    <button
                      type="button"
                      className={`btn sm ${view[s.id] === 'roster' ? 'active' : ''}`}
                      onClick={() => setSessionView(s.id, 'roster')}
                    >
                      Roster
                    </button>
                  </div>
                )}
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
              <div className="voyeur-card__meta">
                <span className="chip mono">
                  <span className="v">{fmtFreq(s.freqHz)}</span>
                </span>
                <span className="chip">
                  <span className="v">{s.mode}</span>
                </span>
                <span className="chip mono">
                  <span className="k">overs</span>
                  <span className="v">{s.segmentCount}</span>
                </span>
                <span className="chip mono">
                  <span className="k">when</span>
                  <span className="v">{fmtWhen(s.startedUtc)}</span>
                </span>
                {s.hasAudio && (
                  <span className="chip">
                    <span className="v">audio</span>
                  </span>
                )}
              </div>

              {openId === s.id && (view[s.id] === 'roster' ? (
                renderRoster(s.id)
              ) : (
                <div className="voyeur-overs">
                  {!detail && <div className="voyeur-empty" style={{ padding: '6px 10px' }}>Loading…</div>}
                  {detail && detail.segments.length === 0 && (
                    <div className="voyeur-empty" style={{ padding: '6px 10px' }}>No overs captured.</div>
                  )}
                  {detail && detail.segments.map(renderOver)}
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </>
  );
}
