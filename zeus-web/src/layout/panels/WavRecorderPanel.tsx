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
// ZEUS WAV Recorder — rack-mount WAV recorder panel. Segmented LED level
// meter, round transport buttons, a waveform strip, a folder/file browser with
// rename/move/delete, and a metadata column. Theming is token-only
// (zeus-web/src/styles/tokens.css). Backed by the /api/wav/* endpoints.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './WavRecorderPanel.css';
import { DirectoryPickerModal } from './DirectoryPickerModal';
import {
  fmtBytes,
  fmtClock,
  fmtDb,
  fmtDate,
  parentOf,
  baseName,
  joinFolder,
  childFolders,
  breadcrumb,
  segmentZone,
  litSegments,
  abbreviatePath,
} from './wavRecorder.format';

// ---------------------------------------------------------------------------
// Wire types — mirror the /api/wav/* JSON contract exactly (camelCase).
// ---------------------------------------------------------------------------

type RecState = 'idle' | 'recording' | 'playing';
type Source = 'rx' | 'tx';
type FileSource = 'rx' | 'tx' | 'unknown';

type Status = {
  state: RecState;
  source: Source;
  file: string | null;
  seconds: number;
  durationSec: number;
  mox: boolean;
  onAir: boolean;
  peak: number; // 0..1
  rms: number; // 0..1
  peakDb: number; // dBFS, floor -100
  clip: boolean;
};

type Recording = {
  name: string;
  fileName: string;
  relPath: string;
  folder: string;
  bytes: number;
  durationSec: number;
  source: FileSource;
  modifiedUnixMs: number;
};

type ListResp = {
  root: string;
  folders: string[];
  recordings: Recording[];
};

type WaveformResp = { file: string; buckets: number[] };

type RootResp = { root: string; isDefault: boolean };

const IDLE_STATUS: Status = {
  state: 'idle',
  source: 'rx',
  file: null,
  seconds: 0,
  durationSec: 0,
  mox: false,
  onAir: false,
  peak: 0,
  rms: 0,
  peakDb: -100,
  clip: false,
};

const SEGMENTS = 40;
const WAVE_BUCKETS = 400;
const STATUS_POLL_ACTIVE_MS = 90;
const STATUS_POLL_IDLE_MS = 500;
const LIST_POLL_MS = 1000;
// Waveform cache bound — keep the most-recently-used N entries so a long
// session can't grow the map without limit.
const WAVE_CACHE_MAX = 50;
// Below this displayed/target level the meter is treated as fully settled.
const METER_EPS = 0.001;

// ---------------------------------------------------------------------------
// Fetch helpers
// ---------------------------------------------------------------------------

class HttpError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function postJson<T = unknown>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`.trim();
    try {
      const t = await res.text();
      if (t) msg = t.slice(0, 160);
    } catch {
      /* ignore */
    }
    throw new HttpError(res.status, msg);
  }
  const ct = res.headers.get('content-type') ?? '';
  if (ct.includes('application/json')) return (await res.json()) as T;
  return undefined as unknown as T;
}

// ---------------------------------------------------------------------------
// Canvas colour resolution (token-driven, refreshed on theme change)
// ---------------------------------------------------------------------------

type MeterColors = {
  green: string;
  amber: string;
  red: string;
  well: string;
  line: string;
  accent: string;
  text: string;
};

function readColors(el: HTMLElement | null): MeterColors {
  const fb: MeterColors = {
    green: '#1aa84e',
    amber: '#ffb13c',
    red: '#ff4a59',
    well: '#050507',
    line: '#1f1f23',
    accent: '#4ea6ff',
    text: '#cccccc',
  };
  if (!el) return fb;
  const cs = getComputedStyle(el);
  const get = (n: string, f: string) => cs.getPropertyValue(n).trim() || f;
  return {
    green: get('--green-soft', fb.green),
    amber: get('--amber', fb.amber),
    red: get('--tx', fb.red),
    well: get('--bg-meter', fb.well),
    line: get('--line', fb.line),
    accent: get('--accent-bright', fb.accent),
    text: get('--fg-1', fb.text),
  };
}

function zoneColor(c: MeterColors, index: number): string {
  switch (segmentZone(index, SEGMENTS)) {
    case 'red':
      return c.red;
    case 'amber':
      return c.amber;
    default:
      return c.green;
  }
}

/**
 * Convert a `#rgb` / `#rrggbb` token colour to an `rgba()` string at `alpha`.
 * Inputs that are already `rgb()`/`rgba()` are returned unchanged. Lets the
 * canvas build glows + gradient fades from palette tokens without new hex.
 */
function withAlpha(color: string, alpha: number): string {
  const c = color.trim();
  if (c.charCodeAt(0) === 35 /* '#' */) {
    let r = 0;
    let g = 0;
    let b = 0;
    if (c.length === 4) {
      r = parseInt(c[1]! + c[1]!, 16);
      g = parseInt(c[2]! + c[2]!, 16);
      b = parseInt(c[3]! + c[3]!, 16);
    } else if (c.length >= 7) {
      r = parseInt(c.slice(1, 3), 16);
      g = parseInt(c.slice(3, 5), 16);
      b = parseInt(c.slice(5, 7), 16);
    }
    if (Number.isFinite(r) && Number.isFinite(g) && Number.isFinite(b)) {
      return `rgba(${r},${g},${b},${alpha})`;
    }
  }
  return c;
}

/** Resize a canvas's backing store to its CSS box (DPR-aware). */
function fitCanvas(canvas: HTMLCanvasElement): { ctx: CanvasRenderingContext2D; w: number; h: number } | null {
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const w = Math.max(1, Math.floor(canvas.clientWidth));
  const h = Math.max(1, Math.floor(canvas.clientHeight));
  const bw = Math.floor(w * dpr);
  const bh = Math.floor(h * dpr);
  if (canvas.width !== bw || canvas.height !== bh) {
    canvas.width = bw;
    canvas.height = bh;
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { ctx, w, h };
}

// --- Waveform cache (relPath:mtime keyed, bounded LRU) ----------------------

/** Insert/refresh a cache entry, evicting the oldest beyond WAVE_CACHE_MAX. */
function waveCachePut(map: Map<string, number[]>, key: string, buckets: number[]): void {
  map.delete(key); // re-insert moves it to the most-recent (Map iteration order)
  map.set(key, buckets);
  while (map.size > WAVE_CACHE_MAX) {
    const oldest = map.keys().next().value;
    if (oldest === undefined) break;
    map.delete(oldest);
  }
}

/** Read a cache entry and mark it most-recently-used. */
function waveCacheGet(map: Map<string, number[]>, key: string): number[] | undefined {
  const hit = map.get(key);
  if (hit) {
    map.delete(key);
    map.set(key, hit);
  }
  return hit;
}

/** Drop every cache entry for a relPath, across all of its mtime variants. */
function waveCacheDropPath(map: Map<string, number[]>, relPath: string): void {
  const prefix = `${relPath}:`;
  for (const k of [...map.keys()]) {
    if (k === relPath || k.startsWith(prefix)) map.delete(k);
  }
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function WavRecorderPanel() {
  const [status, setStatus] = useState<Status>(IDLE_STATUS);
  const [folders, setFolders] = useState<string[]>([]);
  const [recordings, setRecordings] = useState<Recording[]>([]);
  const [recordSource, setRecordSource] = useState<Source>('rx');
  const [currentFolder, setCurrentFolder] = useState<string>('');
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [narrow, setNarrow] = useState(false);

  // Save-location (recordings root) + folder picker.
  const [root, setRoot] = useState<string>('');
  const [rootIsDefault, setRootIsDefault] = useState(true);
  const [pickerOpen, setPickerOpen] = useState(false);

  // CRUD interaction state
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameDraft, setRenameDraft] = useState('');
  // Rename guards: `cancelRenameRef` lets Escape's unmount-blur bail without
  // POSTing; `committingRenameRef` makes a commit single-shot so Enter (which
  // also triggers blur on unmount) can't double-submit with a stale `from`.
  const cancelRenameRef = useRef(false);
  const committingRenameRef = useRef(false);
  const [moveMenu, setMoveMenu] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [confirmFolderDelete, setConfirmFolderDelete] = useState<string | null>(null);

  const rootRef = useRef<HTMLDivElement | null>(null);
  const meterCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const vmeterCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const waveCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const colorsRef = useRef<MeterColors>(readColors(null));
  const reducedMotionRef = useRef(false);

  // Meter smoothing state lives in refs (RAF loop owns it; no re-render churn).
  const targetRef = useRef({ peak: 0, rms: 0, clip: false });
  const dispRef = useRef({ peak: 0, rms: 0, hold: 0 });
  const rafRef = useRef<number | null>(null);
  // Idle-gating for the meter RAF: it stops when fully settled and is woken
  // from the status-poll path when a non-zero level / clip / active state
  // arrives. `wakeMeterRef` is installed by the RAF effect.
  const meterRunningRef = useRef(false);
  const wakeMeterRef = useRef<(() => void) | null>(null);

  // Waveform cache: `relPath:mtime` → buckets (bounded LRU).
  const waveCacheRef = useRef<Map<string, number[]>>(new Map());
  // Live mirrors of the waveform inputs so the ResizeObserver can subscribe
  // once and still read current buckets/progress on resize.
  const waveBucketsRef = useRef<number[] | null>(null);
  const progressRef = useRef(-1);
  const [waveBuckets, setWaveBuckets] = useState<number[] | null>(null);
  // Bumped on theme/font change so the waveform repaints with fresh colours.
  const [themeTick, setThemeTick] = useState(0);

  const errorTimer = useRef<number | null>(null);
  const flashError = useCallback((msg: string) => {
    setError(msg);
    if (errorTimer.current !== null) window.clearTimeout(errorTimer.current);
    errorTimer.current = window.setTimeout(() => setError(null), 4000);
  }, []);

  const noticeTimer = useRef<number | null>(null);
  const flashNotice = useCallback((msg: string) => {
    setNotice(msg);
    if (noticeTimer.current !== null) window.clearTimeout(noticeTimer.current);
    noticeTimer.current = window.setTimeout(() => setNotice(null), 3500);
  }, []);

  useEffect(
    () => () => {
      if (errorTimer.current !== null) window.clearTimeout(errorTimer.current);
      if (noticeTimer.current !== null) window.clearTimeout(noticeTimer.current);
    },
    [],
  );

  // --- Save-location (recordings root) --------------------------------------

  const fetchRoot = useCallback(async () => {
    try {
      const r = (await fetch('/api/wav/root').then((res) => res.json())) as RootResp;
      setRoot(typeof r.root === 'string' ? r.root : '');
      setRootIsDefault(!!r.isDefault);
    } catch {
      /* transient */
    }
  }, []);

  useEffect(() => {
    void fetchRoot();
  }, [fetchRoot]);

  // --- Status + list polling -------------------------------------------------

  const fetchStatus = useCallback(async () => {
    try {
      const s = (await fetch('/api/wav/status').then((r) => r.json())) as Status;
      setStatus(s);
      const peak = s.peak ?? 0;
      const rms = s.rms ?? 0;
      targetRef.current = { peak, rms, clip: !!s.clip };
      // Wake the (possibly idle-stopped) meter when there's something to show
      // or the deck is actively recording/playing.
      if (peak > METER_EPS || rms > METER_EPS || s.clip || s.state !== 'idle') {
        wakeMeterRef.current?.();
      }
    } catch {
      /* transient */
    }
  }, []);

  const fetchList = useCallback(async () => {
    try {
      const l = (await fetch('/api/wav/list').then((r) => r.json())) as ListResp;
      setFolders(Array.isArray(l.folders) ? l.folders : []);
      setRecordings(Array.isArray(l.recordings) ? l.recordings : []);
    } catch {
      /* transient */
    }
  }, []);

  const refresh = useCallback(() => {
    void fetchStatus();
    void fetchList();
  }, [fetchStatus, fetchList]);

  const onRootChanged = useCallback(
    (newRoot: string, isDefault: boolean) => {
      setRoot(newRoot);
      setRootIsDefault(isDefault);
      waveCacheRef.current.clear();
      setSelected(null);
      void fetchList();
      flashNotice(isDefault ? 'Save folder reset to default' : 'Save folder updated');
    },
    [fetchList, flashNotice],
  );

  // Status poll cadence depends on activity.
  const active = status.state !== 'idle';
  useEffect(() => {
    void fetchStatus();
    const ms = active ? STATUS_POLL_ACTIVE_MS : STATUS_POLL_IDLE_MS;
    const id = window.setInterval(() => void fetchStatus(), ms);
    return () => window.clearInterval(id);
  }, [active, fetchStatus]);

  // List poll (slow, steady).
  useEffect(() => {
    void fetchList();
    const id = window.setInterval(() => void fetchList(), LIST_POLL_MS);
    return () => window.clearInterval(id);
  }, [fetchList]);

  // --- Theme colours + reduced-motion ---------------------------------------

  useEffect(() => {
    const refreshColors = () => {
      colorsRef.current = readColors(rootRef.current);
      // Meter picks colours up live via its RAF; nudge the waveform to repaint.
      setThemeTick((t) => t + 1);
    };
    refreshColors();
    const mo = new MutationObserver(refreshColors);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-fonts'] });

    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    const applyRm = () => {
      reducedMotionRef.current = mq.matches;
    };
    applyRm();
    mq.addEventListener('change', applyRm);
    return () => {
      mo.disconnect();
      mq.removeEventListener('change', applyRm);
    };
  }, []);

  // --- Responsive reflow -----------------------------------------------------

  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? el.clientWidth;
      setNarrow(w < 640);
      // The meter canvas re-fits its backing store only inside the RAF; wake
      // it for a settle cycle so a resize during silence isn't left stale.
      wakeMeterRef.current?.();
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // --- Meter RAF loop --------------------------------------------------------

  useEffect(() => {
    const draw = () => {
      const canvas = meterCanvasRef.current;
      const disp = dispRef.current;
      const target = targetRef.current;
      const reduced = reducedMotionRef.current;

      // Ease displayed levels: fast attack, slow release. Reduced motion snaps.
      const ease = (cur: number, to: number) => {
        if (reduced) return to;
        const k = to > cur ? 0.5 : 0.08;
        const next = cur + (to - cur) * k;
        return Math.abs(next - to) < 0.001 ? to : next;
      };
      disp.peak = ease(disp.peak, target.peak);
      disp.rms = ease(disp.rms, target.rms);
      // Peak-hold: instant rise, slow decay.
      if (target.peak >= disp.hold) disp.hold = target.peak;
      else disp.hold = Math.max(target.peak, disp.hold - (reduced ? 0.02 : 0.006));

      if (canvas) drawMeter(canvas, colorsRef.current, disp.peak, disp.rms, disp.hold, target.clip, reduced);
      const vcanvas = vmeterCanvasRef.current;
      if (vcanvas) drawVMeter(vcanvas, colorsRef.current, disp.peak, disp.rms, disp.hold, reduced);

      // Idle-gate: once the displayed bar AND the incoming target are all at
      // rest (and not clipping), draw this final settled frame and STOP. The
      // status poll calls wake() to restart when a level/clip returns. This
      // is the perf win — no 60fps canvas churn during silence. The active
      // visual is untouched: while any signal is present we keep scheduling.
      const settled =
        !target.clip &&
        target.peak < METER_EPS &&
        target.rms < METER_EPS &&
        disp.peak < METER_EPS &&
        disp.rms < METER_EPS &&
        disp.hold < METER_EPS;
      if (settled) {
        meterRunningRef.current = false;
        rafRef.current = null;
        return;
      }
      rafRef.current = window.requestAnimationFrame(draw);
    };

    const wake = () => {
      if (meterRunningRef.current) return;
      meterRunningRef.current = true;
      rafRef.current = window.requestAnimationFrame(draw);
    };
    wakeMeterRef.current = wake;

    // Kick once so the wells render even if no signal ever arrives.
    wake();
    return () => {
      if (rafRef.current !== null) window.cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
      meterRunningRef.current = false;
      wakeMeterRef.current = null;
    };
  }, []);

  // --- Selected recording + stale-selection reconciliation -------------------

  const selectedRec = useMemo(
    () => recordings.find((r) => r.relPath === selected) ?? null,
    [recordings, selected],
  );
  // mtime of the selected file (0 if not in the list yet). Drives the cache
  // key so a replaced file under the same name re-fetches instead of serving
  // stale buckets. Only changes when the selection or its mtime changes — not
  // on every list poll.
  const selectedMtime = selectedRec?.modifiedUnixMs ?? 0;

  // If the selected file vanishes from the list (deleted in another session,
  // or its folder removed), clear the selection so PLAY/TX can't POST a
  // missing file. Guard the optimistic-rename/move window: those paths
  // await fetchList() before re-selecting, so the new relPath is already
  // present here.
  useEffect(() => {
    if (selected && !recordings.some((r) => r.relPath === selected)) {
      setSelected(null);
    }
  }, [recordings, selected]);

  // --- Waveform fetch on selection change ------------------------------------

  useEffect(() => {
    if (!selected) {
      setWaveBuckets(null);
      return;
    }
    const key = selectedMtime > 0 ? `${selected}:${selectedMtime}` : selected;
    const cached = waveCacheGet(waveCacheRef.current, key);
    if (cached) {
      setWaveBuckets(cached);
      return;
    }
    const ctrl = new AbortController();
    const url = `/api/wav/waveform?file=${encodeURIComponent(selected)}&buckets=${WAVE_BUCKETS}`;
    fetch(url, { signal: ctrl.signal })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`${r.status}`))))
      .then((w: WaveformResp) => {
        const buckets = Array.isArray(w.buckets) ? w.buckets : [];
        waveCachePut(waveCacheRef.current, key, buckets);
        setWaveBuckets(buckets);
      })
      .catch(() => {
        /* leave empty well; selection still valid for playback */
      });
    return () => ctrl.abort();
  }, [selected, selectedMtime]);

  // --- Waveform redraw (buckets / progress / size) ---------------------------

  const playingThis = status.state === 'playing' && status.file !== null && status.file === selected;
  const progress = playingThis && status.durationSec > 0 ? status.seconds / status.durationSec : -1;

  useEffect(() => {
    waveBucketsRef.current = waveBuckets;
    progressRef.current = progress;
    const canvas = waveCanvasRef.current;
    if (!canvas) return;
    // `themeTick` is a dep so a live theme/font switch repaints with fresh
    // colours instead of waiting for the next bucket/resize.
    drawWaveform(canvas, colorsRef.current, waveBuckets, progress, reducedMotionRef.current);
  }, [waveBuckets, progress, narrow, themeTick]);

  useEffect(() => {
    const canvas = waveCanvasRef.current;
    if (!canvas || typeof ResizeObserver === 'undefined') return;
    // Subscribe ONCE — read live buckets/progress from refs so the fast-
    // changing `progress` (~11×/s during playback) doesn't tear down and
    // recreate the observer every tick.
    const ro = new ResizeObserver(() =>
      drawWaveform(canvas, colorsRef.current, waveBucketsRef.current, progressRef.current, reducedMotionRef.current),
    );
    ro.observe(canvas);
    return () => ro.disconnect();
  }, []);

  // --- Derived data ----------------------------------------------------------

  const folderChildren = useMemo(() => childFolders(folders, currentFolder), [folders, currentFolder]);
  const filesHere = useMemo(
    () =>
      recordings
        .filter((r) => r.folder === currentFolder)
        .slice()
        .sort((a, b) => b.modifiedUnixMs - a.modifiedUnixMs),
    [recordings, currentFolder],
  );
  const crumbs = useMemo(() => breadcrumb(currentFolder), [currentFolder]);
  const allFolders = useMemo(() => folders.slice().sort((a, b) => a.localeCompare(b)), [folders]);

  const recState = status.state;
  const isRecording = recState === 'recording';
  const isPlaying = recState === 'playing';
  const isIdle = recState === 'idle';

  // --- Transport handlers ----------------------------------------------------

  const onRecord = useCallback(async () => {
    try {
      if (isRecording) {
        await postJson('/api/wav/record/stop');
      } else {
        await postJson('/api/wav/record/start', {
          source: recordSource,
          folder: currentFolder || undefined,
        });
      }
    } catch (e) {
      flashError(e instanceof Error ? e.message : 'Record failed');
    } finally {
      refresh();
    }
  }, [isRecording, recordSource, currentFolder, refresh, flashError]);

  const onPlay = useCallback(
    async (dest: 'local' | 'air') => {
      if (!selected) return;
      try {
        await postJson('/api/wav/play', { file: selected, dest });
      } catch (e) {
        flashError(e instanceof Error ? e.message : 'Playback failed');
      } finally {
        refresh();
      }
    },
    [selected, refresh, flashError],
  );

  const onStop = useCallback(async () => {
    try {
      if (isRecording) await postJson('/api/wav/record/stop');
      else await postJson('/api/wav/stop');
    } catch (e) {
      flashError(e instanceof Error ? e.message : 'Stop failed');
    } finally {
      refresh();
    }
  }, [isRecording, refresh, flashError]);

  // --- File-system handlers --------------------------------------------------

  const onNewFolder = useCallback(async () => {
    const name = window.prompt('New folder name');
    if (!name) return;
    const path = joinFolder(currentFolder, name.trim());
    if (!path) return;
    try {
      await postJson('/api/wav/folder/create', { path });
    } catch (e) {
      flashError(e instanceof Error ? e.message : 'Create folder failed');
    } finally {
      fetchList();
    }
  }, [currentFolder, fetchList, flashError]);

  const onDeleteFolder = useCallback(
    async (path: string) => {
      try {
        await postJson('/api/wav/folder/delete', { path });
      } catch (e) {
        flashError(e instanceof Error ? e.message : 'Delete folder failed');
      } finally {
        setConfirmFolderDelete(null);
        fetchList();
      }
    },
    [fetchList, flashError],
  );

  const commitRename = useCallback(
    async (from: string) => {
      // Escape requested a cancel: bail without POSTing (the unmount-blur
      // would otherwise commit the draft). Consume the flag.
      if (cancelRenameRef.current) {
        cancelRenameRef.current = false;
        return;
      }
      // Single-shot: Enter + the unmount-blur both call this; only the first
      // wins, so no stale-`from` second POST (which would 404).
      if (committingRenameRef.current) return;
      committingRenameRef.current = true;

      const name = renameDraft.trim();
      setRenaming(null);
      if (!name) {
        committingRenameRef.current = false;
        return;
      }
      try {
        const res = await postJson<{ relPath: string }>('/api/wav/rename', { from, name });
        if (res?.relPath) {
          waveCacheDropPath(waveCacheRef.current, from);
          await fetchList(); // ensure the new relPath is in the list before reselect
          setSelected(res.relPath);
        }
      } catch (e) {
        flashError(e instanceof Error ? e.message : 'Rename failed');
      } finally {
        committingRenameRef.current = false;
        fetchList();
      }
    },
    [renameDraft, fetchList, flashError],
  );

  const onMove = useCallback(
    async (from: string, folder: string) => {
      setMoveMenu(null);
      try {
        const res = await postJson<{ relPath: string }>('/api/wav/move', { from, folder });
        if (res?.relPath) {
          waveCacheDropPath(waveCacheRef.current, from);
          await fetchList(); // ensure the moved relPath is in the list before reselect
          setSelected(res.relPath);
        }
      } catch (e) {
        flashError(e instanceof Error ? e.message : 'Move failed');
      } finally {
        fetchList();
      }
    },
    [fetchList, flashError],
  );

  const onDeleteFile = useCallback(
    async (relPath: string) => {
      setConfirmDelete(null);
      try {
        await postJson('/api/wav/delete', { file: relPath });
        waveCacheDropPath(waveCacheRef.current, relPath);
        if (selected === relPath) setSelected(null);
      } catch (e) {
        flashError(e instanceof Error ? e.message : 'Delete failed');
      } finally {
        fetchList();
      }
    },
    [selected, fetchList, flashError],
  );

  // --- Readouts --------------------------------------------------------------

  const peakDbText = fmtDb(status.peakDb);
  const heldDbText = useMemo(() => {
    // Convert the displayed peak-hold fraction back to a coarse dB label.
    const h = dispRef.current.hold;
    void status; // re-evaluate as status changes
    if (h <= 0.0001) return '-∞';
    return (20 * Math.log10(h)).toFixed(1);
  }, [status]);

  const counterText = isRecording
    ? fmtClock(status.seconds)
    : isPlaying
      ? `${fmtClock(status.seconds)} / ${fmtClock(status.durationSec)}`
      : selectedRec
        ? `00:00 / ${fmtClock(selectedRec.durationSec)}`
        : '00:00';

  const statusLabel = isRecording
    ? `RECORDING · ${status.source.toUpperCase()}`
    : isPlaying
      ? status.onAir
        ? 'ON AIR'
        : 'PLAYING'
      : 'IDLE';

  // --- Render ----------------------------------------------------------------

  return (
    <div ref={rootRef} className={`zdr${narrow ? ' zdr--narrow' : ''}`}>
      <div className="zdr__body">
        {/* ---- File system column ---- */}
        <section className="zdr__library" aria-label="Recordings library">
          <header className="zdr__libhead">
            <nav className="zdr__crumbs" aria-label="Folder breadcrumb">
              {crumbs.map((c, i) => (
                <span key={c.path || 'root'} className="zdr__crumb-wrap">
                  {i > 0 && <span className="zdr__crumb-sep">/</span>}
                  <button
                    type="button"
                    className={`zdr__crumb${c.path === currentFolder ? ' is-current' : ''}`}
                    onClick={() => setCurrentFolder(c.path)}
                  >
                    {c.label}
                  </button>
                </span>
              ))}
            </nav>
            <div className="zdr__libactions">
              <button
                type="button"
                className="zdr__dirbtn"
                onClick={() => setPickerOpen(true)}
                aria-label={`Change save folder (currently ${root || 'default'})`}
                title={`Save location: ${root || 'default'}${rootIsDefault ? ' (default)' : ''}\nClick to change`}
              >
                <span className="zdr__dirbtn-glyph" aria-hidden="true">
                  <svg viewBox="0 0 24 24" width="13" height="13" aria-hidden="true">
                    <path
                      d="M3 6.5A1.5 1.5 0 0 1 4.5 5h4l2 2.2H19.5A1.5 1.5 0 0 1 21 8.7v9.8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5Z"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="1.4"
                      strokeLinejoin="round"
                    />
                  </svg>
                </span>
                <span className="zdr__dirbtn-path">{root ? abbreviatePath(root, 2) : 'Default'}</span>
              </button>
              {currentFolder && (
                <button
                  type="button"
                  className="zdr__iconbtn"
                  aria-label="Up one folder"
                  title="Up one folder"
                  onClick={() => setCurrentFolder(parentOf(currentFolder))}
                >
                  ↑
                </button>
              )}
              <button
                type="button"
                className="zdr__iconbtn"
                aria-label="New folder"
                title="New folder"
                onClick={onNewFolder}
              >
                ＋▣
              </button>
            </div>
          </header>

          <div className="zdr__filelist">
            {folderChildren.map((f) => (
              <div key={f} className="zdr__folderrow">
                <button
                  type="button"
                  className="zdr__folderbtn"
                  onClick={() => setCurrentFolder(f)}
                  title={`Open ${baseName(f)}`}
                >
                  <span className="zdr__glyph" aria-hidden="true">
                    📁
                  </span>
                  <span className="zdr__folder-name">{baseName(f)}</span>
                </button>
                {confirmFolderDelete === f ? (
                  <span className="zdr__confirm">
                    <button
                      type="button"
                      className="zdr__iconbtn is-danger"
                      aria-label="Confirm delete folder"
                      onClick={() => onDeleteFolder(f)}
                    >
                      ✓
                    </button>
                    <button
                      type="button"
                      className="zdr__iconbtn"
                      aria-label="Cancel"
                      onClick={() => setConfirmFolderDelete(null)}
                    >
                      ✕
                    </button>
                  </span>
                ) : (
                  <button
                    type="button"
                    className="zdr__iconbtn zdr__rowdel"
                    aria-label={`Delete folder ${baseName(f)}`}
                    title="Delete folder"
                    onClick={() => setConfirmFolderDelete(f)}
                  >
                    🗑
                  </button>
                )}
              </div>
            ))}

            {filesHere.map((r) => {
              const isSel = r.relPath === selected;
              const isNowPlaying = isPlaying && status.file === r.relPath;
              return (
                <div
                  key={r.relPath}
                  className={`zdr__filerow${isSel ? ' is-selected' : ''}${isNowPlaying ? ' is-playing' : ''}`}
                >
                  {renaming === r.relPath ? (
                    <input
                      className="zdr__renameinput"
                      autoFocus
                      value={renameDraft}
                      aria-label="New name"
                      onChange={(e) => setRenameDraft(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') void commitRename(r.relPath);
                        if (e.key === 'Escape') {
                          // Flag cancel BEFORE unmounting so the autoFocus
                          // input's blur handler bails without committing.
                          cancelRenameRef.current = true;
                          setRenaming(null);
                        }
                      }}
                      onBlur={() => void commitRename(r.relPath)}
                    />
                  ) : (
                    <button
                      type="button"
                      className="zdr__filebtn"
                      onClick={() => setSelected(r.relPath)}
                      title={r.fileName}
                    >
                      <span className={`zdr__src zdr__src--${r.source}`} aria-hidden="true">
                        {r.source === 'tx' ? 'TX' : r.source === 'rx' ? 'RX' : '··'}
                      </span>
                      <span className="zdr__file-name">{r.name}</span>
                      <span className="zdr__file-meta">{fmtClock(r.durationSec)}</span>
                      <span className="zdr__file-meta">{fmtBytes(r.bytes)}</span>
                    </button>
                  )}

                  <div className="zdr__rowmenu">
                    {moveMenu === r.relPath ? (
                      <select
                        className="zdr__moveselect"
                        autoFocus
                        aria-label="Move to folder"
                        defaultValue={r.folder}
                        onChange={(e) => void onMove(r.relPath, e.target.value)}
                        onBlur={() => setMoveMenu(null)}
                      >
                        <option value="">Recordings (root)</option>
                        {allFolders.map((f) => (
                          <option key={f} value={f}>
                            {f}
                          </option>
                        ))}
                      </select>
                    ) : confirmDelete === r.relPath ? (
                      <span className="zdr__confirm">
                        <button
                          type="button"
                          className="zdr__iconbtn is-danger"
                          aria-label="Confirm delete"
                          onClick={() => void onDeleteFile(r.relPath)}
                        >
                          ✓
                        </button>
                        <button
                          type="button"
                          className="zdr__iconbtn"
                          aria-label="Cancel delete"
                          onClick={() => setConfirmDelete(null)}
                        >
                          ✕
                        </button>
                      </span>
                    ) : (
                      <>
                        <button
                          type="button"
                          className="zdr__iconbtn"
                          aria-label={`Rename ${r.name}`}
                          title="Rename"
                          onClick={() => {
                            cancelRenameRef.current = false;
                            committingRenameRef.current = false;
                            setRenameDraft(r.name);
                            setRenaming(r.relPath);
                          }}
                        >
                          ✎
                        </button>
                        <button
                          type="button"
                          className="zdr__iconbtn"
                          aria-label={`Move ${r.name}`}
                          title="Move to folder"
                          onClick={() => setMoveMenu(r.relPath)}
                        >
                          ⇄
                        </button>
                        <button
                          type="button"
                          className="zdr__iconbtn zdr__rowdel"
                          aria-label={`Delete ${r.name}`}
                          title="Delete"
                          onClick={() => setConfirmDelete(r.relPath)}
                        >
                          🗑
                        </button>
                      </>
                    )}
                  </div>
                </div>
              );
            })}

            {folderChildren.length === 0 && filesHere.length === 0 && (
              <div className="zdr__empty">
                No recordings here. Hit <strong>REC</strong> to capture {recordSource.toUpperCase()} audio.
              </div>
            )}
          </div>
        </section>

        {/* ---- Center column: meter · transport · waveform ---- */}
        <section className="zdr__center" aria-label="Recorder console">
          {/* Level meter */}
          <div className="zdr__meter">
            <div className="zdr__meterhead">
              <span className="zdr__meterlabel">{isRecording ? 'INPUT LEVEL' : 'OUTPUT LEVEL'}</span>
              <span className="zdr__lcd zdr__lcd--peak" aria-label="Peak level dBFS">
                {peakDbText}
                <small>dB</small>
              </span>
              <span className={`zdr__clip${status.clip ? ' is-clip' : ''}`} aria-live="polite">
                CLIP
              </span>
            </div>
            <canvas ref={meterCanvasRef} className="zdr__metercanvas" aria-hidden="true" />
          </div>

          {/* Transport */}
          <div className="zdr__transport">
            <button
              type="button"
              className={`zdr__xport zdr__xport--rec${isRecording ? ' is-on' : ''}`}
              onClick={onRecord}
              disabled={isPlaying}
              aria-label={isRecording ? 'Stop recording' : 'Record'}
              aria-pressed={isRecording}
            >
              <span className="zdr__xport-glyph zdr__glyph-rec" aria-hidden="true" />
              <span className="zdr__xport-text">{isRecording ? 'REC ●' : 'REC'}</span>
            </button>

            <button
              type="button"
              className={`zdr__xport zdr__xport--play${isPlaying && !status.onAir ? ' is-on' : ''}`}
              onClick={() => onPlay('local')}
              disabled={!selected || isRecording}
              aria-label="Play locally (monitor)"
            >
              <span className="zdr__xport-glyph zdr__glyph-play" aria-hidden="true" />
              <span className="zdr__xport-text">PLAY</span>
            </button>

            <button
              type="button"
              className={`zdr__xport zdr__xport--air${status.onAir ? ' is-on' : ''}`}
              onClick={() => onPlay('air')}
              disabled={!selected || isRecording}
              aria-label="Transmit over the air (engages MOX)"
            >
              <span className="zdr__xport-glyph zdr__glyph-air" aria-hidden="true">
                <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
                  <path
                    d="M12 3v9m0 0a3 3 0 1 0 0 6 3 3 0 0 0 0-6Zm-6.5-.5a9 9 0 0 1 13 0M8 6a5 5 0 0 1 8 0"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.6"
                    strokeLinecap="round"
                  />
                </svg>
              </span>
              <span className="zdr__xport-text">{status.onAir ? 'ON AIR' : 'TX'}</span>
            </button>

            <button
              type="button"
              className="zdr__xport zdr__xport--stop"
              onClick={onStop}
              disabled={isIdle}
              aria-label="Stop"
            >
              <span className="zdr__xport-glyph zdr__glyph-stop" aria-hidden="true" />
              <span className="zdr__xport-text">STOP</span>
            </button>

            <div className="zdr__xport-side">
              <div className="zdr__lcd zdr__lcd--clock" aria-label="Position">
                {counterText}
              </div>
              <div className="zdr__srctoggle" role="group" aria-label="Record source">
                {(['rx', 'tx'] as const).map((s) => (
                  <button
                    key={s}
                    type="button"
                    className={`zdr__srcbtn${recordSource === s ? ' is-on' : ''}`}
                    onClick={() => setRecordSource(s)}
                    disabled={!isIdle}
                    aria-pressed={recordSource === s}
                    title={s === 'rx' ? 'Record received audio' : 'Record transmit (mic) audio'}
                  >
                    {s.toUpperCase()}
                  </button>
                ))}
              </div>
            </div>
          </div>

          {/* Waveform */}
          <div className="zdr__wavewrap">
            <canvas ref={waveCanvasRef} className="zdr__wavecanvas" aria-hidden="true" />
            {!selected && <div className="zdr__waveempty">Select a recording to view its waveform</div>}
          </div>
        </section>

        {/* ---- Right rail: L/R meters + file info ---- */}
        <aside className="zdr__rail" aria-label="Meters and file info">
          {/* Vertical channel meters */}
          <div className="zdr__vmeters">
            <div className="zdr__railhead">METERS</div>
            <div className="zdr__vmeterbody">
              <canvas ref={vmeterCanvasRef} className="zdr__vmetercanvas" aria-hidden="true" />
              <div className="zdr__vmeterscale">
                <span>L</span>
                <span>R</span>
              </div>
            </div>
            <div className="zdr__railreadouts">
              <span className="zdr__lcd zdr__lcd--big" aria-label="Peak level dBFS">
                {peakDbText}
                <small>dB</small>
              </span>
              <span className="zdr__lcd zdr__lcd--hold" aria-label="Held peak dBFS">
                {heldDbText}
                <small>pk</small>
              </span>
            </div>
            <div className="zdr__railchips">
              <span className="zdr__chip">48.0 kHz</span>
              <span className="zdr__chip">f32 · mono</span>
            </div>
          </div>

          {/* File info */}
          <div className="zdr__info">
            <div className="zdr__railhead">RECORDING FILE INFO</div>
            <div className="zdr__info-title">{selectedRec ? selectedRec.name : 'No file selected'}</div>
            <dl className="zdr__info-grid">
              <div>
                <dt>Duration</dt>
                <dd>{selectedRec ? fmtClock(selectedRec.durationSec) : '—'}</dd>
              </div>
              <div>
                <dt>Size</dt>
                <dd>{selectedRec ? fmtBytes(selectedRec.bytes) : '—'}</dd>
              </div>
              <div>
                <dt>Format</dt>
                <dd>48 kHz · 32-bit float · mono</dd>
              </div>
              <div>
                <dt>Source</dt>
                <dd>
                  {selectedRec
                    ? selectedRec.source === 'tx'
                      ? 'TX (transmit)'
                      : selectedRec.source === 'rx'
                        ? 'RX (receive)'
                        : 'Unknown'
                    : '—'}
                </dd>
              </div>
              <div className="zdr__info-wide">
                <dt>Modified</dt>
                <dd>{selectedRec ? fmtDate(selectedRec.modifiedUnixMs) : '—'}</dd>
              </div>
            </dl>
          </div>
        </aside>
      </div>

      {/* Status bar */}
      <footer className="zdr__statusbar">
        <span className={`zdr__statepill zdr__statepill--${recState}`}>{statusLabel}</span>
        <span className={`zdr__onair${status.onAir ? ' is-on' : ''}`}>
          <span className="zdr__onair-dot" aria-hidden="true" />
          {status.onAir ? 'ON AIR' : status.mox ? 'MOX' : 'RX'}
        </span>
        {error ? (
          <span className="zdr__errmsg" role="alert">
            {error}
          </span>
        ) : notice ? (
          <span className="zdr__notemsg" role="status">
            {notice}
          </span>
        ) : null}
      </footer>

      {pickerOpen && (
        <DirectoryPickerModal
          initialPath={root}
          rootIsDefault={rootIsDefault}
          onClose={() => setPickerOpen(false)}
          onChanged={onRootChanged}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Canvas drawing (module-scope, pure of React)
// ---------------------------------------------------------------------------

function drawMeter(
  canvas: HTMLCanvasElement,
  c: MeterColors,
  peak: number,
  rms: number,
  hold: number,
  clip: boolean,
  reduced: boolean,
): void {
  const fit = fitCanvas(canvas);
  if (!fit) return;
  const { ctx, w, h } = fit;
  ctx.clearRect(0, 0, w, h);

  const gap = Math.max(1, Math.round(w / SEGMENTS / 6));
  const segW = (w - gap * (SEGMENTS - 1)) / SEGMENTS;
  const peakLit = litSegments(peak, SEGMENTS);
  const rmsLit = litSegments(rms, SEGMENTS);
  const holdIdx = Math.min(SEGMENTS - 1, litSegments(hold, SEGMENTS) - 1);

  // Two gradients built once per frame (reused across all 40 segments — no
  // per-segment garbage). `glass` gives lit segments a backlit-tube sheen;
  // `wellGrad` carves a faint inset into the dark unlit wells.
  const glass = ctx.createLinearGradient(0, 0, 0, h);
  glass.addColorStop(0, 'rgba(255,255,255,0.34)');
  glass.addColorStop(0.18, 'rgba(255,255,255,0.10)');
  glass.addColorStop(0.5, 'rgba(255,255,255,0)');
  glass.addColorStop(0.85, 'rgba(0,0,0,0.18)');
  glass.addColorStop(1, 'rgba(0,0,0,0.32)');
  const wellGrad = ctx.createLinearGradient(0, 0, 0, h);
  wellGrad.addColorStop(0, 'rgba(0,0,0,0.45)');
  wellGrad.addColorStop(0.5, 'rgba(255,255,255,0.03)');
  wellGrad.addColorStop(1, 'rgba(0,0,0,0.5)');

  for (let i = 0; i < SEGMENTS; i++) {
    const x = i * (segW + gap);
    const col = zoneColor(c, i);
    // Well (off) segment — dark base + inset depth + hairline frame.
    ctx.fillStyle = c.well;
    ctx.fillRect(x, 0, segW, h);
    ctx.fillStyle = wellGrad;
    ctx.fillRect(x, 0, segW, h);
    ctx.strokeStyle = c.line;
    ctx.lineWidth = 1;
    ctx.strokeRect(x + 0.5, 0.5, segW - 1, h - 1);

    if (i < rmsLit) {
      // Illuminated core (rms) — saturated fill + zone-colour outer glow…
      ctx.save();
      if (!reduced) {
        ctx.shadowColor = col;
        ctx.shadowBlur = Math.max(2, segW * 0.55);
      }
      ctx.fillStyle = col;
      ctx.fillRect(x, 0, segW, h);
      ctx.restore();
      // …then a glass sheen on top so it reads as a lit tube, not a flat bar.
      ctx.fillStyle = glass;
      ctx.fillRect(x, 0, segW, h);
    } else if (i < peakLit) {
      // Peak overhang — dimmer, lighter sheen, no heavy glow.
      ctx.globalAlpha = 0.4;
      ctx.fillStyle = col;
      ctx.fillRect(x, 0, segW, h);
      ctx.globalAlpha = 0.5;
      ctx.fillStyle = glass;
      ctx.fillRect(x, 0, segW, h);
      ctx.globalAlpha = 1;
    }
  }

  // Peak-hold tick — brighter than the bar, its own glow, lit end-caps.
  if (holdIdx >= 0) {
    const x = holdIdx * (segW + gap);
    const col = zoneColor(c, holdIdx);
    ctx.save();
    if (!reduced) {
      ctx.shadowColor = col;
      ctx.shadowBlur = Math.max(4, segW * 0.9);
    }
    ctx.fillStyle = col;
    ctx.fillRect(x, 0, segW, h);
    ctx.restore();
    ctx.fillStyle = glass;
    ctx.fillRect(x, 0, segW, h);
    const cap = Math.max(2, h * 0.16);
    ctx.fillStyle = 'rgba(255,255,255,0.7)';
    ctx.fillRect(x, 0, segW, cap);
    ctx.fillRect(x, h - cap, segW, cap);
  }

  // Clip flare on the last segment.
  if (clip) {
    const x = (SEGMENTS - 1) * (segW + gap);
    ctx.save();
    if (!reduced) {
      ctx.shadowColor = c.red;
      ctx.shadowBlur = segW * 1.3;
    }
    ctx.fillStyle = c.red;
    ctx.fillRect(x, 0, segW, h);
    ctx.restore();
    ctx.fillStyle = glass;
    ctx.fillRect(x, 0, segW, h);
  }
}

function drawWaveform(
  canvas: HTMLCanvasElement,
  c: MeterColors,
  buckets: number[] | null,
  progress: number,
  reduced: boolean,
): void {
  const fit = fitCanvas(canvas);
  if (!fit) return;
  const { ctx, w, h } = fit;
  ctx.clearRect(0, 0, w, h);

  // Well background + a faint vertical inset so the strip reads as recessed.
  ctx.fillStyle = c.well;
  ctx.fillRect(0, 0, w, h);
  const bg = ctx.createLinearGradient(0, 0, 0, h);
  bg.addColorStop(0, 'rgba(0,0,0,0.38)');
  bg.addColorStop(0.5, 'rgba(255,255,255,0.02)');
  bg.addColorStop(1, 'rgba(0,0,0,0.42)');
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, w, h);

  // Centre axis line (faint).
  const midY = Math.round(h / 2) + 0.5;
  ctx.strokeStyle = withAlpha(c.line, 0.9);
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, midY);
  ctx.lineTo(w, midY);
  ctx.stroke();

  if (!buckets || buckets.length === 0) return;

  const n = buckets.length;
  const mid = h / 2;
  const colW = w / n;

  // Amber gradient: brightest at the centre axis, fading toward the edges.
  const grad = ctx.createLinearGradient(0, 0, 0, h);
  grad.addColorStop(0, withAlpha(c.amber, 0.3));
  grad.addColorStop(0.42, withAlpha(c.amber, 0.95));
  grad.addColorStop(0.5, c.amber);
  grad.addColorStop(0.58, withAlpha(c.amber, 0.95));
  grad.addColorStop(1, withAlpha(c.amber, 0.3));

  ctx.save();
  if (!reduced) {
    ctx.shadowColor = withAlpha(c.amber, 0.5);
    ctx.shadowBlur = 4;
  }
  ctx.fillStyle = grad;
  for (let i = 0; i < n; i++) {
    const v = Math.max(0, Math.min(1, buckets[i] ?? 0));
    const half = Math.max(0.5, v * (h / 2 - 1));
    const x = i * colW;
    const bw = Math.max(0.6, colW - 0.4);
    ctx.fillRect(x, mid - half, bw, half * 2);
  }
  ctx.restore();

  // Playback progress: dim the un-played tail, then a crisp glowing playhead.
  if (progress >= 0) {
    const px = Math.max(0, Math.min(1, progress)) * w;
    ctx.fillStyle = 'rgba(0,0,0,0.42)';
    ctx.fillRect(px, 0, w - px, h);
    ctx.save();
    ctx.strokeStyle = c.accent;
    if (!reduced) {
      ctx.shadowColor = c.accent;
      ctx.shadowBlur = 8;
    }
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(px, 0);
    ctx.lineTo(px, h);
    ctx.stroke();
    ctx.restore();
  }
}

const VSEGMENTS = 28;

/** Two vertical segmented LED columns (L/R). Audio is mono, so both columns
 *  read the same level — a faithful "stereo meter" face on a mono source. */
function drawVMeter(
  canvas: HTMLCanvasElement,
  c: MeterColors,
  peak: number,
  rms: number,
  hold: number,
  reduced: boolean,
): void {
  const fit = fitCanvas(canvas);
  if (!fit) return;
  const { ctx, w, h } = fit;
  ctx.clearRect(0, 0, w, h);

  const cols = 2;
  const colGap = Math.max(3, w * 0.12);
  const colW = (w - colGap * (cols + 1)) / cols;
  const vgap = Math.max(1, Math.round(h / VSEGMENTS / 7));
  const segH = (h - vgap * (VSEGMENTS - 1)) / VSEGMENTS;
  const peakLit = litSegments(peak, VSEGMENTS);
  const rmsLit = litSegments(rms, VSEGMENTS);
  const holdIdx = Math.min(VSEGMENTS - 1, litSegments(hold, VSEGMENTS) - 1);

  // Zone by vertical position (top = hot).
  const vzone = (i: number): string => {
    const frac = VSEGMENTS <= 1 ? 0 : i / (VSEGMENTS - 1);
    if (frac >= 0.88) return c.red;
    if (frac >= 0.66) return c.amber;
    return c.green;
  };

  for (let col = 0; col < cols; col++) {
    const x = colGap + col * (colW + colGap);
    // Horizontal cylinder sheen for this column (one gradient per column —
    // gives each lit LED a rounded, backlit look).
    const cyl = ctx.createLinearGradient(x, 0, x + colW, 0);
    cyl.addColorStop(0, 'rgba(0,0,0,0.3)');
    cyl.addColorStop(0.5, 'rgba(255,255,255,0.22)');
    cyl.addColorStop(1, 'rgba(0,0,0,0.3)');
    for (let i = 0; i < VSEGMENTS; i++) {
      // i counts from bottom; y inverts.
      const y = h - (i + 1) * segH - i * vgap;
      const color = vzone(i);
      // Dark well + faint inset.
      ctx.fillStyle = c.well;
      ctx.fillRect(x, y, colW, segH);
      ctx.fillStyle = 'rgba(0,0,0,0.35)';
      ctx.fillRect(x, y, colW, segH);
      if (i < rmsLit) {
        ctx.save();
        if (!reduced) {
          ctx.shadowColor = color;
          ctx.shadowBlur = Math.max(2, colW * 0.45);
        }
        ctx.fillStyle = color;
        ctx.fillRect(x, y, colW, segH);
        ctx.restore();
        ctx.fillStyle = cyl;
        ctx.fillRect(x, y, colW, segH);
      } else if (i < peakLit) {
        ctx.globalAlpha = 0.4;
        ctx.fillStyle = color;
        ctx.fillRect(x, y, colW, segH);
        ctx.globalAlpha = 0.5;
        ctx.fillStyle = cyl;
        ctx.fillRect(x, y, colW, segH);
        ctx.globalAlpha = 1;
      }
    }
    if (holdIdx >= 0) {
      const y = h - (holdIdx + 1) * segH - holdIdx * vgap;
      const hc = vzone(holdIdx);
      ctx.save();
      if (!reduced) {
        ctx.shadowColor = hc;
        ctx.shadowBlur = Math.max(3, colW * 0.6);
      }
      ctx.fillStyle = hc;
      ctx.fillRect(x, y, colW, segH);
      ctx.restore();
      ctx.fillStyle = cyl;
      ctx.fillRect(x, y, colW, segH);
      // Bright lit cap on the held segment.
      ctx.fillStyle = 'rgba(255,255,255,0.6)';
      ctx.fillRect(x, y, colW, Math.max(1, segH * 0.34));
    }
  }
}
