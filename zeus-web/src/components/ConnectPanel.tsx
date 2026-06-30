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
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  connect as apiConnect,
  connectP2 as apiConnectP2,
  disconnect as apiDisconnect,
  disconnectP2 as apiDisconnectP2,
  reclaimRadio,
  fetchRadios,
  fetchProtocol3Presence,
  fetchState,
  listPrefsDatabases,
  setActivePrefsDatabase,
  createPrefsDatabase,
  uploadPrefsDatabase,
  exportPrefsDatabase,
  restartApp,
  quitApp,
  ApiError,
  type PrefsDatabaseInfo,
  type RadioInfoDto,
} from '../api/client';
import {
  BOARD_LABELS,
  updateRadioSelection,
  type BoardKind,
} from '../api/radio';
import { getAudioClient } from '../audio/audio-client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import { useTxAudioProfileStore } from '../state/tx-audio-profile-store';
import { useTxStore } from '../state/tx-store';
import { TxAudioProfileSavePrompt } from './TxAudioProfileSavePrompt';
import { ConfirmDialog } from '../layout/ConfirmDialog';
import {
  useConnectStore,
  type ProtocolChoice,
  type SampleRate,
  type SavedEndpoint,
} from '../state/connect-store';

const DISCOVERY_INTERVAL_MS = 10_000;
const DEFAULT_DATA_PORT = 1024;
const DEFAULT_SAMPLE_RATE = 192_000;
const RETRY_THRESHOLD = 2;
const IPV4_RE = /^(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)$/;
// 768/1536 kHz need a wide DDC transport (P2/P3); the select below filters
// them out when Protocol 1 is selected.
const SAMPLE_RATES: SampleRate[] = [48_000, 96_000, 192_000, 384_000, 768_000, 1_536_000];
const P1_MAX_SAMPLE_RATE = 384_000;

function sampleRateCeiling(protocol: ProtocolChoice, boardMaxRateHz: number): number {
  return protocol === 'P1' ? P1_MAX_SAMPLE_RATE : boardMaxRateHz;
}

function highestAllowedSampleRate(maxHz: number): SampleRate {
  return [...SAMPLE_RATES].reverse().find((r) => r <= maxHz) ?? 48_000;
}

// Compact "12.3 KB" / "4.0 MB" for the prefs-database picker. 0 bytes (a
// never-written Default) renders as "empty".
function formatDbSize(bytes: number): string {
  if (!bytes || bytes <= 0) return 'empty';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// "never" for an unwritten Default, else a short local date.
function formatDbSaved(modifiedUtcMs: number): string {
  if (!modifiedUtcMs || modifiedUtcMs <= 0) return 'never';
  return new Date(modifiedUtcMs).toLocaleDateString();
}

function prefsDbOptionLabel(db: PrefsDatabaseInfo): string {
  const parts = [formatDbSize(db.sizeBytes), formatDbSaved(db.modifiedUtcMs)];
  if (db.active) parts.push('ACTIVE');
  return `${db.name} (${parts.join(' · ')})`;
}

// A single-line text prompt rendered as a proper in-app Zeus dialog (wraps
// ConfirmDialog with an input) — replaces window.prompt so it matches Zeus
// chrome. Enter submits; empty/whitespace input is a no-op. The input grabs
// focus after ConfirmDialog's focus trap settles on Cancel.
function PromptDialog({
  title,
  label,
  placeholder,
  confirmLabel,
  onSubmit,
  onCancel,
}: {
  title: string;
  label: string;
  placeholder?: string;
  confirmLabel: string;
  onSubmit: (value: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState('');
  const inputRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    const id = window.setTimeout(() => {
      inputRef.current?.focus();
      inputRef.current?.select();
    }, 0);
    return () => window.clearTimeout(id);
  }, []);
  const submit = () => {
    const v = value.trim();
    if (v) onSubmit(v);
  };
  return (
    <ConfirmDialog
      title={title}
      confirmLabel={confirmLabel}
      intent="primary"
      onConfirm={submit}
      onCancel={onCancel}
    >
      <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: 12.5, color: 'var(--fg-1)' }}>
        <span>{label}</span>
        <input
          ref={inputRef}
          className="mono"
          value={value}
          placeholder={placeholder}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              submit();
            }
          }}
          style={{
            padding: '6px 8px',
            borderRadius: 'var(--r-sm)',
            border: '1px solid var(--line-strong)',
            background: '#0c0c10',
            color: '#d8d8dc',
            fontSize: 13,
            outline: 'none',
          }}
        />
      </label>
    </ConfirmDialog>
  );
}

// Compact prefs-database (profile) selector for the connect screen. Switching
// applies on restart, so the change confirms, flips the server pointer, then
// relaunches. New prompts for the name via an in-app Zeus dialog; Import opens
// a native file dialog and uploads the chosen .db.
function PrefsDatabaseRow() {
  const [databases, setDatabases] = useState<PrefsDatabaseInfo[] | null>(null);
  const [activeRel, setActiveRel] = useState<string>('');
  const [busy, setBusy] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [newDialogOpen, setNewDialogOpen] = useState(false);
  const [pendingSwitch, setPendingSwitch] = useState<string | null>(null);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const dto = await listPrefsDatabases(signal);
      setDatabases(dto.databases);
      setActiveRel(dto.activeRelativePath);
    } catch (e) {
      if ((e as { name?: string })?.name === 'AbortError') return;
      setError(e instanceof Error ? e.message : 'Failed to load databases');
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    void refresh(ctrl.signal);
    return () => ctrl.abort();
  }, [refresh]);

  const onSwitch = useCallback(
    async (relativePath: string) => {
      setPendingSwitch(null);
      if (relativePath === activeRel) return;
      setError(null);
      setBusy(true);
      try {
        await setActivePrefsDatabase(relativePath);
        setRestarting(true);
        await restartApp();
      } catch (e) {
        setRestarting(false);
        setError(e instanceof Error ? e.message : 'Failed to switch database');
      } finally {
        setBusy(false);
      }
    },
    [activeRel],
  );

  const onCreate = useCallback(async (name: string) => {
    setNewDialogOpen(false);
    setError(null);
    setBusy(true);
    try {
      const dto = await createPrefsDatabase(name.trim());
      setDatabases(dto.databases);
      setActiveRel(dto.activeRelativePath);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create database');
    } finally {
      setBusy(false);
    }
  }, []);

  // Export the active database so the operator can back it up or move it to
  // another machine. The dropdown's value tracks the active profile, so this
  // downloads whichever DB is currently selected.
  const onExport = useCallback(async () => {
    if (!activeRel) return;
    setError(null);
    setBusy(true);
    try {
      await exportPrefsDatabase(activeRel);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to export database');
    } finally {
      setBusy(false);
    }
  }, [activeRel]);

  const fileInputRef = useRef<HTMLInputElement>(null);

  // Open the native OS file dialog; the upload runs in onFileChosen once the
  // operator picks a file.
  const onImport = useCallback(() => {
    fileInputRef.current?.click();
  }, []);

  const onFileChosen = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      // Reset so re-picking the same file still fires onChange next time.
      e.target.value = '';
      if (!file) return;
      setError(null);
      setBusy(true);
      try {
        const dto = await uploadPrefsDatabase(file);
        setDatabases(dto.databases);
        setActiveRel(dto.activeRelativePath);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to import database');
      } finally {
        setBusy(false);
      }
    },
    [],
  );

  const disabled = busy || restarting || databases === null;

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        padding: '8px 14px',
        borderBottom: '1px solid var(--panel-border)',
        background: 'var(--bg-1)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span className="label-xs" style={{ color: 'var(--fg-2)', minWidth: 58 }}>
          Database
        </span>
        <select
          aria-label="Active prefs database"
          value={activeRel}
          disabled={disabled}
          onChange={(e) => {
            const next = e.target.value;
            if (next && next !== activeRel) setPendingSwitch(next);
          }}
          style={{ ...prefsDbSelectStyle, flex: 1 }}
        >
          {databases === null && <option value="">Loading…</option>}
          {(databases ?? []).map((db) => (
            <option key={db.relativePath} value={db.relativePath}>
              {prefsDbOptionLabel(db)}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="label-xs"
          disabled={disabled}
          onClick={() => setNewDialogOpen(true)}
          style={prefsDbButtonStyle}
          title="Create a new database"
        >
          ＋ New
        </button>
        <button
          type="button"
          className="label-xs"
          disabled={disabled}
          onClick={() => void onImport()}
          style={prefsDbButtonStyle}
          title="Import an existing .db file"
        >
          Import
        </button>
        <button
          type="button"
          className="label-xs"
          disabled={disabled || !activeRel}
          onClick={() => void onExport()}
          style={prefsDbButtonStyle}
          title="Export the active database to a .db file"
        >
          Export
        </button>
        <input
          ref={fileInputRef}
          type="file"
          accept=".db"
          onChange={(e) => void onFileChosen(e)}
          style={{ display: 'none' }}
          aria-hidden
          tabIndex={-1}
        />
      </div>
      {restarting && (
        <span className="label-xs" style={{ color: 'var(--fg-3)' }}>
          Restarting…
        </span>
      )}
      {error && (
        <span className="label-xs" style={{ color: 'var(--tx)' }}>
          {error}
        </span>
      )}
      {newDialogOpen && (
        <PromptDialog
          title="New Database"
          label="New database name:"
          placeholder="e.g. Field Day"
          confirmLabel="Create"
          onSubmit={(name) => void onCreate(name)}
          onCancel={() => setNewDialogOpen(false)}
        />
      )}
      {pendingSwitch !== null && (
        <ConfirmDialog
          title="Switch Database"
          confirmLabel="Switch & Restart"
          intent="primary"
          onConfirm={() => void onSwitch(pendingSwitch)}
          onCancel={() => setPendingSwitch(null)}
        >
          <p style={{ margin: 0, fontSize: 12.5, color: 'var(--fg-1)' }}>
            Switch to{' '}
            <strong className="mono">
              {(databases ?? []).find((db) => db.relativePath === pendingSwitch)?.name ?? pendingSwitch}
            </strong>{' '}
            and restart Zeus?
          </p>
        </ConfirmDialog>
      )}
    </div>
  );
}

const prefsDbSelectStyle: React.CSSProperties = {
  background: 'var(--bg-0)',
  color: 'var(--fg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  padding: '4px 6px',
  fontFamily: 'var(--font-mono)',
  fontSize: 11,
  outline: 'none',
  minWidth: 0,
};

const prefsDbButtonStyle: React.CSSProperties = {
  background: 'var(--bg-0)',
  color: 'var(--fg-2)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  padding: '4px 8px',
  cursor: 'pointer',
  whiteSpace: 'nowrap',
};

// Same set as the Settings RadioSelector, in the same order. Auto first so
// the default Manual-mode connect behaviour is "let discovery decide".
// Post-#218 Phase 4: Griffin renamed → HermesII, HermesC10 (G2E) added.
const MANUAL_BOARD_OPTIONS: ReadonlyArray<BoardKind> = [
  'Auto',
  'HermesLite2',
  'OrionMkII',
  'HermesC10',
  'Orion',
  'Angelia',
  'Hermes',
  'HermesII',
  'Metis',
];

const PROTOCOL3_UNAVAILABLE_TITLE = 'Protocol 3 requires p3app running on the selected radio.';
const PROTOCOL3_PREVIEW_TITLE =
  'Protocol 3 p3app detected on this G2.';

function hasProtocol3App(r: RadioInfoDto): boolean {
  return r.details?.protocol3Available === 'true';
}

function protocolDisplayName(protocol: string): string {
  if (protocol === 'P3') return 'Protocol 3';
  if (protocol === 'P2') return 'Protocol 2';
  return 'Protocol 1';
}

function discoveredP3AvailableForIp(radios: RadioInfoDto[] | null, ip: string): boolean {
  const needle = ip.trim();
  if (!needle || radios === null) return false;
  return radios.some((r) => r.ipAddress === needle && hasProtocol3App(r));
}

function endpointFor(r: RadioInfoDto): string {
  if (!r.ipAddress) return '';
  const protocol = r.details?.protocol ?? 'P1';
  const port = protocol === 'P3'
    ? (r.details?.protocol3Port ?? `${DEFAULT_DATA_PORT}`)
    : `${DEFAULT_DATA_PORT}`;
  return r.ipAddress.includes(':')
    ? r.ipAddress
    : `${r.ipAddress}:${port}`;
}

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message;
  return String(err);
}

// Discovery surfaces rawBoardId as a hex string like "0x01" (Hermes) /
// "0x0A" (OrionMkII). Parse it back to a byte so we can hand it to the
// connect endpoint — issue #171.
function parseRawBoardId(raw: string | undefined): number | null {
  if (!raw) return null;
  const n = parseInt(raw, 16);
  if (!Number.isFinite(n) || n < 0 || n > 0xff) return null;
  return n;
}

// Post-connect side-effects shared by discover-click and manual-connect.
//
// Drive / TUN drive, mic gain, and Leveler max-gain are all authoritative on
// the server (StateDto.DrivePct / TunePct / MicGainDb / LevelerMaxGainDb,
// persisted via RadioStateStore). The connect-time response carries the
// hydrated values and tx-store.hydrateFromState picks them up; pushing the
// localStorage mirror back here would clobber the server's just-hydrated
// values, which is the bug reported for relaunches that didn't restore drive
// (and the same bug that left mic gain + Leveler reverting on every desktop
// launch when Photino wiped its storage).
function applyPostConnectEffects() {
  void getAudioClient().start();
}

export interface ConnectPanelProps {
  /** When true and connected, render only the Disconnect button (no
   *  endpoint chip). The bottom status bar shows the radio IP separately
   *  in this layout, so the chip would be redundant in the top bar. */
  compact?: boolean;
}

export function ConnectPanel({ compact = false }: ConnectPanelProps = {}) {
  const status = useConnectionStore((s) => s.status);
  const endpoint = useConnectionStore((s) => s.endpoint);
  const applyState = useConnectionStore((s) => s.applyState);
  // tx-store hydration runs alongside applyState on every fresh
  // RadioStateDto so PS / TwoTone tunings persisted server-side reach the
  // UI even when localStorage is empty (cleared cache, new browser, etc.).
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const inflight = useConnectionStore((s) => s.inflight);
  const setInflight = useConnectionStore((s) => s.setInflight);
  const setBoardId = useConnectionStore((s) => s.setBoardId);
  const setConnectedProtocol = useConnectionStore((s) => s.setConnectedProtocol);
  const lastConnectedEndpoint = useConnectionStore(
    (s) => s.lastConnectedEndpoint,
  );
  const setLastConnectedEndpoint = useConnectionStore(
    (s) => s.setLastConnectedEndpoint,
  );
  const wisdomPhase = useConnectionStore((s) => s.wisdomPhase);
  const wisdomStatus = useConnectionStore((s) => s.wisdomStatus);
  const dspPreparing = wisdomPhase === 'building';

  const mode = useConnectStore((s) => s.mode);
  const setMode = useConnectStore((s) => s.setMode);
  const savedEndpoints = useConnectStore((s) => s.savedEndpoints);
  const saveEndpoint = useConnectStore((s) => s.saveEndpoint);
  const removeEndpoint = useConnectStore((s) => s.removeEndpoint);
  const touchEndpoint = useConnectStore((s) => s.touchEndpoint);
  const manualFormDefaults = useConnectStore((s) => s.manualFormDefaults);
  const setManualFormDefaults = useConnectStore((s) => s.setManualFormDefaults);
  const lastConnectedId = useConnectStore((s) => s.lastConnectedId);
  const boardMaxRateHz = useRadioStore((s) => s.capabilities.maxRxSampleRateHz);

  const [radios, setRadios] = useState<RadioInfoDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [failureCount, setFailureCount] = useState(0);
  const inflightRef = useRef(false);
  const [retryNonce, setRetryNonce] = useState(0);
  // Pending "TX Audio Profile '{name}' changed — save?" prompt that gates the
  // Disconnect when the loaded profile has unsaved live edits.
  const [savePrompt, setSavePrompt] = useState<{ name: string } | null>(null);
  const [savePromptBusy, setSavePromptBusy] = useState(false);
  // Pending "take over a Busy radio" confirmation. Holds the discovered radio
  // the operator asked to force-connect to; the dialog kicks the current owner.
  const [pendingTakeover, setPendingTakeover] = useState<RadioInfoDto | null>(null);
  // Same takeover, Manual-connect path: a manual connect to a Busy radio comes
  // back as a 409 (reclaimable) rather than a discovered row, so we stash the
  // resolved form params here and offer the same Reclaim-then-connect flow.
  const [pendingManualTakeover, setPendingManualTakeover] =
    useState<Required<Pick<SavedEndpoint, 'ip' | 'port' | 'protocol' | 'sampleRate' | 'board'>> & { label?: string } | null>(null);

  // Pending "Exit Zeus?" confirmation for the login-dialog Exit button.
  const [pendingQuit, setPendingQuit] = useState(false);

  const [manualIp, setManualIp] = useState(manualFormDefaults.ip);
  const [manualPort, setManualPort] = useState(manualFormDefaults.port);
  const [manualProtocol, setManualProtocol] = useState<ProtocolChoice>(manualFormDefaults.protocol);
  const [manualSampleRate, setManualSampleRate] = useState<SampleRate>(manualFormDefaults.sampleRate);
  // Older persisted ManualFormDefaults predate the board field — coalesce to Auto.
  const [manualBoard, setManualBoard] = useState<BoardKind>(manualFormDefaults.board ?? 'Auto');
  const [manualSave, setManualSave] = useState(true);
  const [manualError, setManualError] = useState<string | null>(null);
  const [manualP3ProbeAvailable, setManualP3ProbeAvailable] = useState(false);
  const [manualP3Checking, setManualP3Checking] = useState(false);
  const manualMaxRateHz = sampleRateCeiling(manualProtocol, boardMaxRateHz);
  const manualP3Available =
    discoveredP3AvailableForIp(radios, manualIp) || manualP3ProbeAvailable;

  useEffect(() => {
    if (manualSampleRate <= manualMaxRateHz) return;
    setManualSampleRate(highestAllowedSampleRate(manualMaxRateHz));
  }, [manualMaxRateHz, manualSampleRate]);

  useEffect(() => {
    const ip = manualIp.trim();
    setManualP3ProbeAvailable(false);
    if (!IPV4_RE.test(ip)) {
      setManualP3Checking(false);
      return;
    }

    const ctrl = new AbortController();
    setManualP3Checking(true);
    const timer = window.setTimeout(() => {
      fetchProtocol3Presence(ip, ctrl.signal)
        .then((p3) => setManualP3ProbeAvailable(p3.available))
        .catch((err) => {
          if ((err as { name?: string })?.name !== 'AbortError') {
            setManualP3ProbeAvailable(false);
          }
        })
        .finally(() => {
          if (!ctrl.signal.aborted) setManualP3Checking(false);
        });
    }, 250);

    return () => {
      window.clearTimeout(timer);
      ctrl.abort();
    };
  }, [manualIp]);

  useEffect(() => {
    if (manualProtocol === 'P3' && !manualP3Available) {
      setManualProtocol('P2');
    }
  }, [manualP3Available, manualProtocol]);

  useEffect(() => {
    inflightRef.current = inflight;
  }, [inflight]);

  useEffect(() => {
    const ctrl = new AbortController();
    fetchState(ctrl.signal)
      .then((s) => {
        applyState(s);
        hydrateTxFromState(s);
      })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        setError(errorMessage(err));
        setFailureCount((n) => n + 1);
      });
    return () => ctrl.abort();
  }, [applyState, hydrateTxFromState]);

  // Discovery polling keeps running while Manual mode is active (PRD §6.2
  // proposal): toggling back to Discover shows fresh results immediately.
  useEffect(() => {
    if (status === 'Connected') return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const tick = async () => {
      if (!inflightRef.current) {
        if (!cancelled) setScanning(true);
        try {
          const list = await fetchRadios();
          if (!cancelled) {
            setRadios(list);
            setError(null);
            setFailureCount(0);
          }
        } catch (err) {
          if (!cancelled) {
            setError(errorMessage(err));
            setFailureCount((n) => n + 1);
          }
        } finally {
          if (!cancelled) setScanning(false);
        }
      }
      if (!cancelled) timer = setTimeout(tick, DISCOVERY_INTERVAL_MS);
    };
    tick();

    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
    };
  }, [status, retryNonce]);

  // Core connect sequence shared by the normal Connect button and the
  // Busy-radio takeover flow. Caller owns the inflight/error state so both
  // entry points can wrap it (the takeover sends a reclaim first).
  const performConnect = useCallback(
    async (r: RadioInfoDto, force = false) => {
      const ep = endpointFor(r);
      const protocol = r.details?.protocol ?? 'P1';
      if (protocol === 'P3') {
        throw new Error(PROTOCOL3_PREVIEW_TITLE);
      }
      const isP2 = protocol === 'P2';
      if (isP2) {
        // Pass the discovered board byte (e.g. 0x01 = Hermes for Brick2).
        // Without this the server falls back to OrionMkII for every P2
        // connection — issue #171.
        const rawBoardId = parseRawBoardId(r.details?.rawBoardId);
        await apiConnectP2({
          endpoint: ep,
          sampleRate: DEFAULT_SAMPLE_RATE,
          boardId: rawBoardId ?? undefined,
          // Takeover path only: the server refuses a second-master connect to a
          // Busy radio (relay-chatter guard). After reclaim has stopped the
          // other owner, force past the guard so the still-settling radio's
          // lingering Busy status doesn't re-block the re-connect.
          force,
        });
        const fresh = await fetchState();
        applyState(fresh);
        hydrateTxFromState(fresh);
      } else {
        // Pass the discovered board byte so the server sets the correct
        // board kind on the Protocol1Client (default is HermesLite2 —
        // without this an ANAN-10E appears as HL2 post-connect, issue #294).
        const rawBoardId = parseRawBoardId(r.details?.rawBoardId);
        const next = await apiConnect({
          endpoint: ep,
          sampleRate: DEFAULT_SAMPLE_RATE,
          boardId: rawBoardId ?? undefined,
        });
        applyState(next);
        hydrateTxFromState(next);
      }
      setBoardId(r.boardId || null);
      setConnectedProtocol(isP2 ? 'P2' : 'P1');
      setLastConnectedEndpoint(ep || null);
      applyPostConnectEffects();
    },
    [applyState, hydrateTxFromState, setBoardId, setConnectedProtocol, setLastConnectedEndpoint],
  );

  const handleConnect = useCallback(
    async (r: RadioInfoDto) => {
      if (inflightRef.current) return;
      setInflight(true);
      setError(null);
      try {
        await performConnect(r);
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setInflight(false);
      }
    },
    [performConnect, setInflight],
  );

  // Take over a Busy radio: ask the server to send a protocol stop (kicking
  // the current owner) and then connect. Invoked from the takeover confirm
  // dialog, never directly from the row button.
  const handleForceConnect = useCallback(
    async (r: RadioInfoDto) => {
      if (inflightRef.current) return;
      const ep = endpointFor(r);
      const protocol = r.details?.protocol ?? 'P1';
      if (protocol === 'P3') {
        setError(PROTOCOL3_PREVIEW_TITLE);
        return;
      }
      const isP2 = protocol === 'P2';
      setInflight(true);
      setError(null);
      try {
        await reclaimRadio(ep, isP2 ? 'P2' : 'P1');
        await performConnect(r, /* force */ true);
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setInflight(false);
      }
    },
    [performConnect, setInflight],
  );

  const handleManualConnect = useCallback(
    async (override?: Partial<SavedEndpoint>, force = false) => {
      if (inflightRef.current) return;
      const ip = (override?.ip ?? manualIp).trim();
      const port = override?.port ?? manualPort;
      const protocol: ProtocolChoice = override?.protocol ?? manualProtocol;
      const sampleRate: SampleRate = (override?.sampleRate as SampleRate | undefined)
        ?? manualSampleRate;
      const board: BoardKind = override?.board ?? manualBoard;
      const label = override?.label;

      if (!IPV4_RE.test(ip)) {
        setManualError('Enter a valid IPv4 address, e.g. 192.168.1.20');
        return;
      }
      if (!Number.isInteger(port) || port < 1 || port > 65535) {
        setManualError('Port must be between 1 and 65535');
        return;
      }
      if (protocol === 'P3') {
        setManualError(PROTOCOL3_PREVIEW_TITLE);
        return;
      }

      const ep = `${ip}:${port}`;
      setInflight(true);
      setManualError(null);
      try {
        // Apply the operator's board choice BEFORE opening the socket so
        // RadioService.ConnectedBoardKind resolves correctly on the very
        // first packet — otherwise PA defaults / drive-byte / ATT would
        // briefly use the auto-detected board until Settings was used to
        // flip the override. 'Auto' clears any prior override.
        const overrideOn = board !== 'Auto';
        try {
          await updateRadioSelection(board, overrideOn);
          await useRadioStore.getState().load();
        } catch (selErr) {
          // Don't block the connect on selection-PUT failure; surface it
          // alongside any subsequent connect error.
          setManualError(`Board override: ${errorMessage(selErr)}`);
        }

        if (protocol === 'P2') {
          await apiConnectP2({ endpoint: ep, sampleRate, force });
          const fresh = await fetchState();
          applyState(fresh);
          hydrateTxFromState(fresh);
        } else {
          const next = await apiConnect({ endpoint: ep, sampleRate, force });
          applyState(next);
          hydrateTxFromState(next);
        }
        setBoardId(null);
        setConnectedProtocol(protocol);
        setLastConnectedEndpoint(ep);
        applyPostConnectEffects();
        if (manualSave || override) {
          saveEndpoint({ label, ip, port, protocol, sampleRate, board });
        }
        setManualFormDefaults({ ip, port, protocol, sampleRate, board, label: '' });
      } catch (err) {
        // Busy radio (relay-chatter guard, P2 path): the server refuses a
        // second-master connect with a 409 { reclaimable: true }. Offer the
        // same Reclaim-then-connect takeover the Discover path exposes instead
        // of dead-ending on the error text. `force` is already set on a retry
        // after reclaim, so a still-Busy radio can't loop the prompt.
        if (!force && err instanceof ApiError && err.reclaimable) {
          setManualError(null);
          setPendingManualTakeover({ ip, port, protocol, sampleRate, board, label });
        } else {
          setManualError(errorMessage(err));
        }
      } finally {
        setInflight(false);
      }
    },
    [
      manualIp, manualPort, manualProtocol, manualSampleRate, manualBoard,
      manualSave, applyState, hydrateTxFromState, setBoardId, setConnectedProtocol, setInflight,
      setLastConnectedEndpoint, saveEndpoint, setManualFormDefaults,
    ],
  );

  // Take over a Busy radio from the Manual path: send the protocol stop to kick
  // the current owner, then re-run the manual connect with force=true so the
  // still-settling Busy status doesn't re-block it. Mirrors handleForceConnect
  // for the Discover path.
  const handleManualForceConnect = useCallback(
    async (snap: NonNullable<typeof pendingManualTakeover>) => {
      if (inflightRef.current) return;
      if (snap.protocol === 'P3') {
        setManualError(PROTOCOL3_PREVIEW_TITLE);
        return;
      }
      const ep = `${snap.ip}:${snap.port}`;
      // Latch the ref directly (the effect that mirrors `inflight` only runs on
      // the next render) so the reclaim shows as inflight and a stray click
      // can't double-submit during the radio's settle window.
      inflightRef.current = true;
      setInflight(true);
      setManualError(null);
      try {
        await reclaimRadio(ep, snap.protocol === 'P2' ? 'P2' : 'P1');
      } catch (err) {
        setManualError(errorMessage(err));
        inflightRef.current = false;
        setInflight(false);
        return;
      }
      // Release the latch so handleManualConnect's own guard passes; it owns the
      // inflight state for the forced connect from here.
      inflightRef.current = false;
      await handleManualConnect(snap, /* force */ true);
    },
    [handleManualConnect, setInflight],
  );

  const doDisconnect = useCallback(async () => {
    if (inflightRef.current) return;
    setInflight(true);
    setError(null);
    try {
      try { await apiDisconnect(); } catch { /* may be P2 */ }
      try { await apiDisconnectP2(); } catch { /* may have been P1 */ }
      const fresh = await fetchState();
      applyState(fresh);
      hydrateTxFromState(fresh);
      setBoardId(null);
      setConnectedProtocol(null);
      setRadios(null);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setInflight(false);
    }
  }, [applyState, hydrateTxFromState, setBoardId, setConnectedProtocol, setInflight]);

  // Gate the Disconnect: if the loaded TX Audio Profile has unsaved live edits,
  // prompt to save first (an async save still needs the live server connection).
  const handleDisconnect = useCallback(() => {
    if (inflightRef.current) return;
    const ps = useTxAudioProfileStore.getState();
    const profile = ps.lastLoadedId
      ? ps.profiles.find((p) => p.id === ps.lastLoadedId)
      : undefined;
    if (ps.dirty && profile) {
      setSavePrompt({ name: profile.name });
      return;
    }
    void doDisconnect();
  }, [doDisconnect]);

  const onSavePromptSave = useCallback(async () => {
    const name = savePrompt?.name;
    if (!name) return;
    setSavePromptBusy(true);
    const result = await useTxAudioProfileStore.getState().save(name);
    setSavePromptBusy(false);
    setSavePrompt(null);
    if (result.ok) {
      void doDisconnect();
    } else {
      // Keep the connection so the operator can retry; surface the error.
      setError(result.error ?? 'Could not save TX audio profile.');
    }
  }, [savePrompt, doDisconnect]);

  const onSavePromptDiscard = useCallback(() => {
    setSavePrompt(null);
    void doDisconnect();
  }, [doDisconnect]);

  const onSavePromptCancel = useCallback(() => setSavePrompt(null), []);

  const handleQuit = useCallback(() => {
    setPendingQuit(false);
    // The process exits ~300 ms after acking, so the response may never land —
    // fire and forget, swallowing the inevitable network error on teardown.
    void quitApp().catch(() => {});
  }, []);

  const handleRetry = useCallback(() => {
    setError(null);
    setFailureCount(0);
    setRetryNonce((n) => n + 1);
  }, []);

  const sortedRadios = useMemo(() => {
    if (!radios || !lastConnectedEndpoint) return radios;
    const preferred: RadioInfoDto[] = [];
    const rest: RadioInfoDto[] = [];
    for (const r of radios) {
      if (endpointFor(r) === lastConnectedEndpoint) preferred.push(r);
      else rest.push(r);
    }
    return [...preferred, ...rest];
  }, [radios, lastConnectedEndpoint]);

  const sortedSaved = useMemo(() => {
    return [...savedEndpoints].sort((a, b) =>
      b.lastUsedUtc.localeCompare(a.lastUsedUtc),
    );
  }, [savedEndpoints]);

  const showError = error !== null && failureCount >= RETRY_THRESHOLD;

  if (status === 'Connected') {
    return (
      <>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          {!compact && (
            <span className="chip accent">
              <span className="k">RADIO</span>
              <span className="v mono">{endpoint ?? '—'}</span>
            </span>
          )}
          {error && (
            <span className="label-xs" style={{ color: 'var(--tx)' }}>
              {error}
            </span>
          )}
          <button type="button" onClick={handleDisconnect} disabled={inflight} className="btn">
            {inflight ? 'Disconnecting…' : 'Disconnect'}
          </button>
        </div>
        {savePrompt && (
          <TxAudioProfileSavePrompt
            profileName={savePrompt.name}
            busy={savePromptBusy}
            onSave={() => void onSavePromptSave()}
            onDiscard={onSavePromptDiscard}
            onCancel={onSavePromptCancel}
          />
        )}
      </>
    );
  }

  const statusRight = dspPreparing
    ? 'Building…'
    : status === 'Connecting'
      ? 'Connecting…'
      : inflight
        ? 'Working…'
        : scanning
          ? 'Scanning…'
          : 'Refreshes every 10 s';

  return (
    <div className="panel" style={{ padding: 0, minWidth: 460, maxWidth: 580, position: 'relative', overflow: 'hidden' }}>
      <div
        aria-hidden
        style={{
          position: 'absolute',
          top: 0,
          left: 0,
          right: 0,
          height: 360,
          backgroundImage: 'url(/zeus-clouds.jpg)',
          backgroundSize: 'cover',
          backgroundPosition: 'center 18%',
          zIndex: 0,
          pointerEvents: 'none',
        }}
      />
      <div
        aria-hidden
        style={{
          position: 'absolute',
          top: 0,
          left: 0,
          right: 0,
          height: 360,
          background:
            'linear-gradient(180deg, rgba(10,15,24,0.25) 0%, rgba(10,15,24,0.55) 55%, var(--bg-1) 92%)',
          zIndex: 1,
          pointerEvents: 'none',
        }}
      />
      <div style={{ position: 'relative', zIndex: 2 }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          height: 40,
          padding: '0 12px',
          background:
            'linear-gradient(180deg, rgba(58,59,63,0.55), rgba(35,36,39,0.75))',
          backdropFilter: 'blur(6px)',
          borderBottom: '1px solid rgba(0,0,0,0.5)',
          boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.06)',
        }}
      >
        <div style={{ width: 22, height: 22, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
            <circle cx="12" cy="12" r="3" fill="var(--accent)" />
            <circle cx="12" cy="12" r="7" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.5" />
            <circle cx="12" cy="12" r="11" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.25" />
          </svg>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', lineHeight: 1.05 }}>
          <span
            className="mono"
            style={{
              fontSize: 14,
              fontWeight: 700,
              letterSpacing: '0.02em',
              color: 'var(--fg-0)',
              textShadow: '0 1px 2px rgba(0,0,0,0.8)',
            }}
          >
            ZEUS
          </span>
          <span
            className="label-xs"
            style={{
              color: 'var(--fg-1)',
              fontSize: 9,
              textShadow: '0 1px 2px rgba(0,0,0,0.8)',
            }}
          >
            OpenHPSDR · Protocol 1 / 2 / 3
          </span>
        </div>
        <button
          type="button"
          onClick={() => setPendingQuit(true)}
          className="btn sm"
          style={{ marginLeft: 'auto' }}
          title="Close Zeus"
        >
          Exit
        </button>
      </div>
      <div style={{ height: 180 }} />

      {!compact && <PrefsDatabaseRow />}

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          padding: '8px 14px',
          borderBottom: '1px solid var(--panel-border)',
          background: 'var(--bg-1)',
        }}
      >
        <span className="label-xs" style={{ fontSize: 11, letterSpacing: '0.14em' }}>
          {dspPreparing ? 'First-run setup' : 'Discover Radio'}
        </span>
        <span className="label-xs" style={{ color: 'var(--fg-3)' }}>
          {scanning && <span aria-hidden>· </span>}
          {statusRight}
        </span>
      </div>

      <div style={{ padding: 14, display: 'flex', flexDirection: 'column', gap: 12, background: 'var(--bg-1)' }}>
        {dspPreparing ? (
          <WisdomBuildingBody status={wisdomStatus} />
        ) : (
          <>
        <div
          role="tablist"
          aria-label="Connect mode"
          style={{
            display: 'grid',
            gridTemplateColumns: '1fr 1fr',
            gap: 4,
            padding: 3,
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <button
            type="button"
            role="tab"
            aria-selected={mode === 'discover'}
            onClick={() => setMode('discover')}
            className={`btn sm ${mode === 'discover' ? 'active' : 'ghost'}`}
          >
            Discover
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={mode === 'manual'}
            onClick={() => setMode('manual')}
            className={`btn sm ${mode === 'manual' ? 'active' : 'ghost'}`}
          >
            Manual
          </button>
        </div>

        {mode === 'discover' ? (
          <>
            {showError && (
              <div
                className="mono"
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  padding: '6px 10px',
                  background: 'rgba(230,58,43,0.12)',
                  border: '1px solid rgba(230,58,43,0.35)',
                  borderRadius: 0,
                  color: 'var(--tx)',
                  fontSize: 11,
                }}
              >
                <span>{error}</span>
                <button type="button" onClick={handleRetry} className="btn sm">
                  Retry
                </button>
              </div>
            )}
            {sortedRadios === null ? (
              <div className="label-xs" style={{ color: 'var(--fg-3)' }}>
                Scanning LAN…
              </div>
            ) : sortedRadios.length === 0 ? (
              <div className="label-xs" style={{ color: 'var(--fg-3)' }}>
                No radios found. Check power, ethernet, and subnet — or switch to Manual.
              </div>
            ) : (
              <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
                {sortedRadios.map((r) => {
                  const ep = endpointFor(r);
                  const isLast = !!ep && ep === lastConnectedEndpoint;
                  const protocol = r.details?.protocol ?? 'P1';
                  const isP2 = protocol === 'P2';
                  const isP3 = protocol === 'P3';
                  const showP3Preview = hasProtocol3App(r);
                  const showP3Chip = showP3Preview && !isP3;
                  return (
                    <li
                      key={r.macAddress || r.ipAddress}
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        gap: 10,
                        padding: '6px 10px',
                        background: 'var(--bg-2)',
                        border: '1px solid var(--panel-border)',
                        borderRadius: 0,
                      }}
                    >
                      <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                        <span style={{ color: 'var(--fg-0)', fontSize: 12, fontWeight: 600 }}>
                          {r.boardId || 'radio'}{' '}
                          <span className="label-xs" style={{ color: 'var(--fg-3)' }}>
                            fw {r.firmwareVersion || '?'}
                          </span>
                          <span
                            className="chip"
                            style={{ marginLeft: 6 }}
                            title={`Discovered via ${protocolDisplayName(protocol)}`}
                          >
                            <span className="v">{protocol}</span>
                          </span>
                          {showP3Chip && (
                            <span
                              className="chip"
                              style={{ marginLeft: 6 }}
                              title={PROTOCOL3_PREVIEW_TITLE}
                            >
                              <span className="v">P3</span>
                            </span>
                          )}
                          {isLast && (
                            <span className="chip accent" style={{ marginLeft: 6 }} title="Last connected radio">
                              <span className="v">LAST</span>
                            </span>
                          )}
                        </span>
                        <span className="mono label-xs" style={{ color: 'var(--fg-3)' }}>
                          {ep || '—'} · {r.macAddress || '—'}
                        </span>
                      </div>
                      {!isP3 && (r.busy ? (
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                          <span
                            className="label-xs"
                            style={{ color: 'var(--tx)' }}
                            title="In use by another client"
                          >
                            Busy
                          </span>
                          <button
                            type="button"
                            onClick={() => setPendingTakeover(r)}
                            disabled={inflight}
                            title="Take over this radio — disconnects the client currently using it"
                            className="btn sm"
                          >
                            Take over
                          </button>
                        </div>
                      ) : (
                        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                          <button
                            type="button"
                            onClick={() => handleConnect(r)}
                            disabled={inflight}
                            title={isP2 ? 'Protocol 2 path — experimental, RX only' : undefined}
                            className="btn sm active"
                          >
                            {inflight ? 'Connecting…' : 'Connect'}
                          </button>
                        </div>
                      ))}
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        ) : (
          <ManualMode
            ip={manualIp}
            setIp={setManualIp}
            port={manualPort}
            setPort={setManualPort}
            protocol={manualProtocol}
            setProtocol={setManualProtocol}
            p3Available={manualP3Available}
            p3Checking={manualP3Checking}
            sampleRate={manualSampleRate}
            setSampleRate={setManualSampleRate}
            maxSampleRateHz={manualMaxRateHz}
            board={manualBoard}
            setBoard={setManualBoard}
            save={manualSave}
            setSave={setManualSave}
            error={manualError}
            onConnect={() => handleManualConnect()}
            inflight={inflight}
            savedEndpoints={sortedSaved}
            lastConnectedId={lastConnectedId}
            onReconnect={(e) => {
              setManualIp(e.ip);
              setManualPort(e.port);
              setManualProtocol(e.protocol);
              setManualSampleRate(e.sampleRate);
              setManualBoard(e.board ?? 'Auto');
              touchEndpoint(e.id);
              void handleManualConnect(e);
            }}
            onRemove={removeEndpoint}
          />
        )}
          </>
        )}
      </div>
      </div>
      {pendingTakeover && (
        <ConfirmDialog
          title="Take over this radio?"
          confirmLabel="Take over"
          cancelLabel="Cancel"
          intent="danger"
          onConfirm={() => {
            const target = pendingTakeover;
            setPendingTakeover(null);
            void handleForceConnect(target);
          }}
          onCancel={() => setPendingTakeover(null)}
        >
          <p style={{ margin: 0 }}>
            <strong>{pendingTakeover.boardId || 'This radio'}</strong>
            {' '}({endpointFor(pendingTakeover) || pendingTakeover.ipAddress}) is
            in use by another client. Taking it over sends a stop command that
            disconnects whoever is currently using it — including an active
            transmission — and then connects Zeus.
          </p>
        </ConfirmDialog>
      )}
      {pendingManualTakeover && (
        <ConfirmDialog
          title="Take over this radio?"
          confirmLabel="Take over"
          cancelLabel="Cancel"
          intent="danger"
          onConfirm={() => {
            const target = pendingManualTakeover;
            setPendingManualTakeover(null);
            void handleManualForceConnect(target);
          }}
          onCancel={() => setPendingManualTakeover(null)}
        >
          <p style={{ margin: 0 }}>
            <strong>{pendingManualTakeover.ip}:{pendingManualTakeover.port}</strong>{' '}
            is in use by another controller. Taking it over sends a stop command
            that disconnects whoever is currently using it — including an active
            transmission — and then connects Zeus.
          </p>
        </ConfirmDialog>
      )}
      {pendingQuit && (
        <ConfirmDialog
          title="Exit Zeus?"
          confirmLabel="Exit"
          cancelLabel="Cancel"
          intent="danger"
          onConfirm={handleQuit}
          onCancel={() => setPendingQuit(false)}
        >
          <p style={{ margin: 0 }}>
            This closes Zeus and stops the backend. Any active radio connection
            will be dropped.
          </p>
        </ConfirmDialog>
      )}
    </div>
  );
}

function WisdomBuildingBody({ status }: { status: string }) {
  const trimmed = status.trim();
  return (
    <div
      role="status"
      aria-live="polite"
      aria-label="First-run DSP setup in progress"
      style={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'stretch',
        gap: 12,
        padding: '6px 2px 4px',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span
          className="mono"
          style={{
            color: 'var(--fg-0)',
            fontSize: 16,
            fontWeight: 700,
            letterSpacing: '0.02em',
            flex: 1,
          }}
        >
          Preparing wisdom file…
        </span>
        <span
          aria-label="Why is this happening?"
          title="Wisdom precalculates FFTW transforms (forward and inverse, across every FFT size WDSP uses) so noise reduction, filters, and the panadapter respond instantly once you connect. Runs once per machine; subsequent startups skip this step."
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 18,
            height: 18,
            borderRadius: '50%',
            border: '1px solid var(--panel-border)',
            background: 'var(--bg-0)',
            color: 'var(--fg-2)',
            fontSize: 11,
            fontWeight: 700,
            cursor: 'help',
            flexShrink: 0,
          }}
        >
          i
        </span>
      </div>
      <div
        className="mono"
        style={{
          minHeight: 18,
          padding: '8px 10px',
          background: 'var(--bg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
          color: 'var(--accent)',
          fontSize: 11,
          letterSpacing: '0.02em',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        {trimmed || 'Starting FFTW planner…'}
      </div>
      <span
        style={{
          color: 'var(--fg-1)',
          fontSize: 11,
          lineHeight: 1.5,
        }}
      >
        Helps noise reduction and filters respond faster by precalculating
        DSP transforms. One-time, first-run only — please leave the app open
        and wait. Connect will become available automatically.
      </span>
    </div>
  );
}

interface ManualModeProps {
  ip: string; setIp: (v: string) => void;
  port: number; setPort: (v: number) => void;
  protocol: ProtocolChoice; setProtocol: (v: ProtocolChoice) => void;
  p3Available: boolean;
  p3Checking: boolean;
  sampleRate: SampleRate; setSampleRate: (v: SampleRate) => void;
  maxSampleRateHz: number;
  board: BoardKind; setBoard: (v: BoardKind) => void;
  save: boolean; setSave: (v: boolean) => void;
  error: string | null;
  onConnect: () => void;
  inflight: boolean;
  savedEndpoints: SavedEndpoint[];
  lastConnectedId: string | undefined;
  onReconnect: (e: SavedEndpoint) => void;
  onRemove: (id: string) => void;
}

const inputStyle: React.CSSProperties = {
  background: 'var(--bg-0)',
  color: 'var(--fg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  padding: '5px 8px',
  fontFamily: 'var(--font-mono)',
  fontSize: 12,
  outline: 'none',
  width: '100%',
};

const fieldLabelStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 3,
};

function ManualMode(p: ManualModeProps) {
  const p3Disabled = !p.p3Available || p.p3Checking || p.inflight;
  const canConnect = !p.inflight && p.protocol !== 'P3';
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 110px',
          gap: 8,
        }}
      >
        <label style={fieldLabelStyle}>
          <span className="label-xs" style={{ color: 'var(--fg-2)' }}>IP address</span>
          <input
            type="text"
            inputMode="decimal"
            spellCheck={false}
            value={p.ip}
            onChange={(e) => p.setIp(e.target.value)}
            placeholder="192.168.1.20"
            style={inputStyle}
          />
        </label>
        <label style={fieldLabelStyle}>
          <span className="label-xs" style={{ color: 'var(--fg-2)' }}>Port</span>
          <input
            type="number"
            min={1}
            max={65535}
            value={p.port}
            onChange={(e) => p.setPort(Number(e.target.value))}
            style={inputStyle}
          />
        </label>
      </div>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 1fr',
          gap: 8,
        }}
      >
        <div style={fieldLabelStyle}>
          <span className="label-xs" style={{ color: 'var(--fg-2)' }}>Protocol</span>
          <div style={{ display: 'flex', gap: 4 }}>
            <button
              type="button"
              onClick={() => p.setProtocol('P1')}
              className={`btn sm ${p.protocol === 'P1' ? 'active' : 'ghost'}`}
              style={{ flex: 1 }}
            >
              P1
            </button>
            <button
              type="button"
              onClick={() => p.setProtocol('P2')}
              className={`btn sm ${p.protocol === 'P2' ? 'active' : 'ghost'}`}
              style={{ flex: 1 }}
              title="Protocol 2 — experimental, RX only"
            >
              P2
            </button>
            <button
              type="button"
              onClick={() => {
                if (!p3Disabled) p.setProtocol('P3');
              }}
              disabled={p3Disabled}
              className={`btn sm ${p.protocol === 'P3' ? 'active' : 'ghost'}`}
              style={{
                flex: 1,
                opacity: p3Disabled ? 0.45 : 1,
                cursor: p3Disabled ? 'not-allowed' : 'pointer',
              }}
              title={
                p.p3Checking
                  ? 'Checking for p3app on this radio...'
                  : p.p3Available
                    ? PROTOCOL3_PREVIEW_TITLE
                    : PROTOCOL3_UNAVAILABLE_TITLE
              }
            >
              P3
            </button>
          </div>
        </div>
        <label style={fieldLabelStyle}>
          <span className="label-xs" style={{ color: 'var(--fg-2)' }}>
            Sample rate
          </span>
          <select
            value={p.sampleRate}
            onChange={(e) => p.setSampleRate(Number(e.target.value) as SampleRate)}
            style={inputStyle}
          >
            {SAMPLE_RATES.filter((r) => r <= p.maxSampleRateHz).map((r) => (
              <option key={r} value={r}>{r / 1000} kHz</option>
            ))}
          </select>
        </label>
      </div>

      <label style={fieldLabelStyle}>
        <span className="label-xs" style={{ color: 'var(--fg-2)' }}>
          Radio type
        </span>
        <select
          value={p.board}
          onChange={(e) => p.setBoard(e.target.value as BoardKind)}
          style={inputStyle}
          title={
            p.board === 'Auto'
              ? 'Auto-detect: discovery picks the board.'
              : 'Override active: Zeus will treat this radio as the selected board, ignoring auto-detection. Wrong choice can produce wrong drive levels — use only if you know your hardware combination is misreported (e.g. Anvelina + ANAN 200D PA).'
          }
        >
          {MANUAL_BOARD_OPTIONS.map((b) => (
            <option key={b} value={b}>
              {BOARD_LABELS[b]}
            </option>
          ))}
        </select>
        {p.board !== 'Auto' && (
          <span
            className="label-xs"
            style={{
              color: 'var(--tx)',
              fontSize: 10,
              letterSpacing: 0,
              textTransform: 'none',
              marginTop: 2,
            }}
          >
            Override active — discovery result will be ignored.
          </span>
        )}
      </label>

      <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, color: 'var(--fg-2)' }}>
        <input
          type="checkbox"
          checked={p.save}
          onChange={(e) => p.setSave(e.target.checked)}
        />
        Save for next time
      </label>

      {p.error && (
        <div
          className="mono"
          style={{
            padding: '6px 10px',
            background: 'rgba(230,58,43,0.12)',
            border: '1px solid rgba(230,58,43,0.35)',
            borderRadius: 0,
            color: 'var(--tx)',
            fontSize: 11,
          }}
        >
          {p.error}
        </div>
      )}

      <button
        type="button"
        onClick={p.onConnect}
        disabled={!canConnect}
        title={p.protocol === 'P3' ? PROTOCOL3_PREVIEW_TITLE : undefined}
        className={`btn lg ${canConnect ? 'active' : ''}`}
        style={{ alignSelf: 'stretch' }}
      >
        {p.inflight ? 'Connecting...' : 'Connect'}
      </button>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
        <span className="label-xs" style={{ color: 'var(--fg-3)' }}>Saved endpoints</span>
        {p.savedEndpoints.length === 0 ? (
          <div
            className="label-xs"
            style={{
              color: 'var(--fg-3)',
              textTransform: 'none',
              letterSpacing: 0,
              fontSize: 11,
              padding: '10px 12px',
              background: 'var(--bg-2)',
              border: '1px dashed var(--panel-border)',
              borderRadius: 0,
              textAlign: 'center',
            }}
          >
            Connect manually and leave "Save for next time" ticked to see saved endpoints here.
          </div>
        ) : (
          <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
            {p.savedEndpoints.map((e) => {
              const isLast = e.id === p.lastConnectedId;
              const p3Saved = e.protocol === 'P3';
              return (
                <li
                  key={e.id}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'space-between',
                    gap: 8,
                    padding: '6px 10px',
                    background: 'var(--bg-2)',
                    border: '1px solid var(--panel-border)',
                    borderRadius: 0,
                  }}
                >
                  <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                    <span style={{ color: 'var(--fg-0)', fontSize: 12, fontWeight: 600 }}>
                      {e.label || `${e.ip}:${e.port}`}
                      <span className="chip" style={{ marginLeft: 6 }}>
                        <span className="v">{e.protocol}</span>
                      </span>
                      {isLast && (
                        <span className="chip accent" style={{ marginLeft: 6 }}>
                          <span className="v">LAST</span>
                        </span>
                      )}
                      {e.board && e.board !== 'Auto' && (
                        <span
                          className="chip"
                          style={{
                            marginLeft: 6,
                            background: 'var(--tx-soft)',
                            borderColor: 'var(--tx)',
                          }}
                          title={`Board override: ${BOARD_LABELS[e.board]}`}
                        >
                          <span className="v">{BOARD_LABELS[e.board]}</span>
                        </span>
                      )}
                    </span>
                    <span className="mono label-xs" style={{ color: 'var(--fg-3)' }}>
                      {e.ip}:{e.port} · {e.sampleRate / 1000} kHz
                    </span>
                  </div>
                  <div style={{ display: 'flex', gap: 4 }}>
                    <button
                      type="button"
                      onClick={() => p.onReconnect(e)}
                      disabled={p.inflight || p3Saved}
                      className={`btn sm ${p.inflight || p3Saved ? '' : 'active'}`}
                      title={p3Saved ? PROTOCOL3_PREVIEW_TITLE : undefined}
                    >
                      Connect
                    </button>
                    <button
                      type="button"
                      onClick={() => p.onRemove(e.id)}
                      disabled={p.inflight}
                      className="btn sm ghost"
                      title="Remove from saved endpoints"
                      aria-label="Remove saved endpoint"
                    >
                      ✕
                    </button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </div>
  );
}
