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

import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { WorkspaceContext } from './layout/WorkspaceContext';
import { FlexWorkspace } from './layout/FlexWorkspace';
import { WorkspaceErrorBoundary } from './layout/WorkspaceErrorBoundary';
import { currentDetachedWorkspaceLayoutId } from './layout/workspace-windows';
import { ConfirmDialog } from './layout/ConfirmDialog';
import { AfGainSlider } from './components/AfGainSlider';
import { AgcSlider } from './components/AgcSlider';
import { SquelchSlider } from './components/SquelchSlider';
import { AlertBanner } from './components/AlertBanner';
import { AudioSuiteWindow } from './components/AudioSuiteWindow';
import { AttenuatorSlider } from './components/AttenuatorSlider';
import { AudioToggle } from './components/AudioToggle';
import { BandFavorites } from './components/toolbar/BandFavorites';
import { ConnectPanel } from './components/ConnectPanel';
import { FilterPanel } from './components/filter/FilterPanel';
import { LeftLayoutBar } from './components/LeftLayoutBar';
import { MicMeter } from './components/MicMeter';
import { ModeFavorites } from './components/toolbar/ModeFavorites';
import { CtunButton } from './components/CtunButton';
import { MoxButton } from './components/MoxButton';
import { PreampButton } from './components/PreampButton';
import { PsToggleButton } from './components/PsToggleButton';
import { PaTempChip } from './components/PaTempChip';
import { QrzStatusPill } from './components/QrzStatusPill';
import { RotatorStatusPill } from './components/RotatorStatusPill';
import type { SettingsTabId } from './components/SettingsMenu';
import { SignalIntelligenceController } from './components/SignalIntelligenceController';
import { SmartNrController } from './components/SmartNrController';
import { DspSceneDiagnosticsPublisher } from './components/DspSceneDiagnosticsPublisher';
import { AudioPlaybackDiagnosticsPublisher } from './components/AudioPlaybackDiagnosticsPublisher';
import { useTxAudioProfileDirtyTracker } from './state/tx-audio-profile-tracker';
import { ThemeApplier } from './components/ThemeApplier';
import { StartupUpdatePrompt } from './components/StartupUpdatePrompt';
import { StepFavorites } from './components/toolbar/StepFavorites';
import { TunButton } from './components/TunButton';
import { BOARD_LABELS } from './api/radio';
import { useFilterRibbonOpenSync } from './components/filter/filterRibbonShared';
import { CONTACTS, bandOf } from './components/design/data';
import { bearingDeg, distanceKm } from './components/design/geo';
import { startRealtime } from './realtime/ws-client';
import { getServerBaseUrl, isCapacitorRuntime } from './serverUrl';
import { getAudioClient } from './audio/audio-client';
import { setAudioHostMode } from './audio/host-mode';
import { useMicUplink } from './audio/use-mic-uplink';
import { fetchState, fetchUpdateStatus, type RepoUpdateStatus } from './api/client';
import { useConnectionStore } from './state/connection-store';
import { useRadioStore } from './state/radio-store';
import { useQrzStore } from './state/qrz-store';
import { useRotatorStore } from './state/rotator-store';
import { useLoggerStore } from './state/logger-store';
import { useTxStore } from './state/tx-store';
import { useLayoutStore } from './state/layout-store';
import { useDisplaySettingsStore } from './state/display-settings-store';
import { useCapabilitiesStore } from './state/capabilities-store';
import { useKeyboardShortcuts } from './util/use-keyboard-shortcuts';
import { SpectrumWheelActionsContext, type SpectrumWheelActions } from './util/use-pan-tune-gesture';
import { BandPlanProvider } from './context/BandPlanContext';
import { registerServiceWorker } from './service-worker/registerSW';
import { UpdatePrompt } from './service-worker/UpdatePrompt';
import { useDesktopViewportLock, useIsMobileViewport } from './mobile/use-mobile-viewport';
import type L from 'leaflet';
import type { Contact } from './components/design/data';
import { qrzStationToContact } from './components/design/qrz-contact';

const SettingsView = lazy(async () => {
  const module = await import('./components/SettingsMenu');
  return { default: module.SettingsView };
});
const MobileApp = lazy(async () => {
  const module = await import('./mobile/MobileApp');
  return { default: module.MobileApp };
});

// See ../state/connection-store.ts — StateDto is REST-poll only; WS is binary
// frames. 1 s poll keeps slow state (atten offset, adc overload) fresh — the
// previous 333 ms cadence accounted for ~3 of the ~5 idle-RX fetches/sec and
// drove repeated applyState/hydrateFromState fan-out into the React tree.
const STATE_POLL_MS = 1000;

export default function App() {
  const detachedLayoutId = useMemo(() => currentDetachedWorkspaceLayoutId(), []);
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const settingsInitialTab = useLayoutStore((s) => s.settingsInitialTab);
  const setSettingsView = useLayoutStore((s) => s.setSettingsView);
  const detachedLayoutName = useLayoutStore((s) =>
    detachedLayoutId ? s.layouts.find((l) => l.id === detachedLayoutId)?.name : undefined,
  );
  const [updateAvailable, setUpdateAvailable] = useState(false);
  const [installUpdate, setInstallUpdate] = useState<(() => Promise<void>) | null>(null);
  const [startupUpdate, setStartupUpdate] = useState<RepoUpdateStatus | null>(null);
  const [startupUpdateDismissed, setStartupUpdateDismissed] = useState(false);
  const [confirmResetLayout, setConfirmResetLayout] = useState<{
    id: string;
    name: string;
  } | null>(null);
  const status = useConnectionStore((s) => s.status);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const preampOn = useConnectionStore((s) => s.preampOn);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const endpoint = useConnectionStore((s) => s.endpoint);
  const connected = status === 'Connected';
  // Brand sub label reflects what discovery actually saw on the wire
  // (selection.connected), not the operator's preferred override — showing
  // "ANAN G2" when an HL2 is plugged in would just confuse anyone reading
  // the bottom status bar to confirm what they're talking to. The bar
  // itself reads radio-store; we still trigger a reload here whenever the
  // connection flips so the label is fresh after Connect.
  const radioConnected = useRadioStore((s) => s.selection.connected);
  const radioLoad = useRadioStore((s) => s.load);
  // Reload on mount AND every time the wire connection flips to Connected.
  // Clicking Connect on a discovered radio doesn't refresh radio-store on
  // its own (only the manual-connect path does).
  useEffect(() => { radioLoad(); }, [radioLoad, connected]);
  // Track unsaved TX Audio Profile edits (dirty flag) for the disconnect/close
  // save prompt. Mounted once here so it spans the whole session.
  useTxAudioProfileDirtyTracker();
  const brandSub = radioConnected !== 'Unknown'
    ? BOARD_LABELS[radioConnected]
    : 'Not Connected';

  // Per-radio layouts (issue #241): the layout-store is keyed on the active
  // BoardKind. "default" is the sentinel for "no radio yet" — discovery
  // landing flips this to e.g. "HermesLite2" / "AnanG2" and the store
  // re-fetches that radio's named-layout collection from the server.
  const loadLayoutsForRadio = useLayoutStore((s) => s.loadForRadio);
  // Wait for the radio-store's initial fetch before loading any layout. Until
  // `radioLoaded` is true, `connected === 'Unknown'` is ambiguous: it could mean
  // "no radio" OR "not resolved yet". Loading 'default' eagerly here renders the
  // default-key layout, then the connection resolves to the real board and we
  // re-load the per-radio layout — that swap is the on-load flash. Gating on
  // `radioLoaded` means we load the correct layout exactly once.
  const radioLoaded = useRadioStore((s) => s.loaded);
  useEffect(() => {
    if (!radioLoaded) return;
    const key = radioConnected !== 'Unknown' ? radioConnected : 'default';
    void loadLayoutsForRadio(key);
  }, [loadLayoutsForRadio, radioConnected, radioLoaded]);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);

  // Recover action for the workspace error boundary: reset the active layout to
  // its default panel arrangement, which clears whatever layout/panel state
  // drove a render to throw and blank the workspace.
  const resetActiveWorkspaceLayout = useCallback(() => {
    useLayoutStore.getState().resetActiveLayout();
  }, []);

  useKeyboardShortcuts();
  useMicUplink();
  useFilterRibbonOpenSync();

  const topbarControlsRef = useRef<HTMLDivElement | null>(null);
  const [topbarScroll, setTopbarScroll] = useState({ canLeft: false, canRight: false });
  const syncTopbarScroll = useCallback(() => {
    const el = topbarControlsRef.current;
    if (!el) return;
    const maxScroll = Math.max(0, el.scrollWidth - el.clientWidth);
    const next = {
      canLeft: el.scrollLeft > 1,
      canRight: maxScroll > 1 && el.scrollLeft < maxScroll - 1,
    };
    setTopbarScroll((prev) =>
      prev.canLeft === next.canLeft && prev.canRight === next.canRight ? prev : next,
    );
  }, []);
  const scrollTopbarControls = useCallback((direction: -1 | 1) => {
    const el = topbarControlsRef.current;
    if (!el) return;
    const amount = Math.max(220, Math.floor(el.clientWidth * 0.75));
    el.scrollBy({ left: direction * amount, behavior: 'smooth' });
    window.setTimeout(syncTopbarScroll, 180);
  }, [syncTopbarScroll]);
  useEffect(() => {
    syncTopbarScroll();
  });
  useEffect(() => {
    const el = topbarControlsRef.current;
    if (!el) return;
    window.addEventListener('resize', syncTopbarScroll);
    const resizeObserver = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(syncTopbarScroll)
      : null;
    resizeObserver?.observe(el);
    return () => {
      window.removeEventListener('resize', syncTopbarScroll);
      resizeObserver?.disconnect();
    };
  }, [syncTopbarScroll]);

  // Register service worker and handle updates
  useEffect(() => {
    const handleUpdateAvailable = () => {
      console.log('Service worker update available');
      setUpdateAvailable(true);
    };

    const install = registerServiceWorker(handleUpdateAvailable);
    if (install) {
      setInstallUpdate(() => install);
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    fetchUpdateStatus(true, ctrl.signal)
      .then((next) => {
        const actionableRelease =
          !next.isGitRepo
          && next.updateAvailable
          && (next.updateAction === 'download' || next.updateAction === 'openRelease');
        if (actionableRelease) setStartupUpdate(next);
      })
      .catch(() => {
        /* Settings -> Updates exposes manual retry and detailed errors. */
      });
    return () => ctrl.abort();
  }, []);

  useEffect(() => {
    const stop = startRealtime();
    return () => {
      stop();
    };
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    fetchState(ctrl.signal)
      .then((next) => {
        useConnectionStore.getState().applyState(next);
        useTxStore.getState().hydrateFromState(next);
      })
      .catch(() => {
        /* ConnectPanel reports startup/API failures in the visible UI. */
      });
    return () => ctrl.abort();
  }, []);

  // Fetch host capabilities once on mount. The backend snapshot is built
  // at startup and doesn't change at runtime, so a single fetch is enough;
  // failures fall back to "no features available" which hides feature-gated
  // UI rather than rendering broken controls.
  useEffect(() => {
    void useCapabilitiesStore.getState().refresh();
    // Mirror the resolved host mode into the audio-host-mode flag so the
    // non-React consumers (audio-client, ws-client, mic-uplink) can opt
    // out of browser audio paths in desktop mode without each needing its
    // own Zustand subscription on the hot path.
    return useCapabilitiesStore.subscribe((state) => {
      const host = state.capabilities?.host;
      if (host) setAudioHostMode(host === 'desktop' ? 'native' : 'browser');
    });
  }, []);

  useEffect(() => {
    if (!connected) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let ctrl: AbortController | null = null;
    const tick = async () => {
      ctrl = new AbortController();
      try {
        const next = await fetchState(ctrl.signal);
        if (!cancelled) {
          // trustVfo:false — a poll response generated before the operator's
          // latest tune must not rewind the dial mid-gesture (issue #597).
          useConnectionStore.getState().applyState(next, { trustVfo: false });
          // Hydrate server-persisted TX/PS fields from StateDto so edits made
          // in another tab or before a desktop relaunch reach this store after
          // the startup/connect-time hydrate.
          useTxStore.getState().hydrateFromState(next);
        }
      } catch {
        /* transient errors reconcile on the next tick */
      }
      if (!cancelled) timer = setTimeout(tick, STATE_POLL_MS);
    };
    tick();
    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
      ctrl?.abort();
    };
  }, [connected]);

  useEffect(() => {
    return useConnectionStore.subscribe((state, prev) => {
      if (state.mode !== prev.mode || state.modeB !== prev.modeB) getAudioClient().reset();
    });
  }, []);

  useEffect(() => {
    return useTxStore.subscribe((state, prev) => {
      if (state.moxOn !== prev.moxOn) {
        // PERF_PASS_3_DEBUG: arm one-shot capture in audio-client. Uncommitted.
        (window as unknown as { __zeusFirstAudioAfterMox?: boolean }).__zeusFirstAudioAfterMox = !state.moxOn;
        getAudioClient().reset();
      }
    });
  }, []);

  // Apply saved theme attributes to <html> on first render. The Tweaks panel
  // used to toggle these at runtime; now the defaults are fixed.
  useEffect(() => {
    const variant = localStorage.getItem('zeus.variant') || 'console';
    const fonts = localStorage.getItem('zeus.fonts') || 'geist';
    document.documentElement.setAttribute('data-variant', variant);
    document.documentElement.setAttribute('data-fonts', fonts);
  }, []);

  // Handle deeplink via URL hash (#qrz, #rotator, #pa, #server, #about).
  // Opens the settings view and navigates to the specified tab.
  useEffect(() => {
    const handleHash = () => {
      const hash = window.location.hash.slice(1); // Remove '#'
      if (
        hash === 'qrz' ||
        hash === 'rotator' ||
        hash === 'pa' ||
        hash === 'server' ||
        hash === 'updates' ||
        hash === 'about'
      ) {
        setSettingsView(true, hash as SettingsTabId);
        // Clear the hash after handling it
        window.history.replaceState(null, '', window.location.pathname + window.location.search);
      }
    };

    // Check on mount
    handleHash();

    // Listen for hash changes
    window.addEventListener('hashchange', handleHash);
    return () => window.removeEventListener('hashchange', handleHash);
  }, [setSettingsView]);

  // First-run UX for native shells (Capacitor): if there is no server URL
  // configured, the app would spin trying to reach the WebView's own host.
  // Pop the Settings → Server tab open so the operator can paste their LAN
  // address.
  useEffect(() => {
    if (!isCapacitorRuntime()) return;
    if (getServerBaseUrl()) return;
    setSettingsView(true, 'server');
  }, [setSettingsView]);

  // --- Design-mock state (QRZ, DSP grid toggles, CW WPM, memories) ---
  const [callsign, setCallsign] = useState('');
  // Panadapter background is now driven by the Display settings panel
  // (display-settings-store): 'basic' | 'beam-map' | 'image'. terminatorActive
  // (= map + terminator chrome visible) is derived from 'beam-map'; imageMode
  // is derived from 'image' + a loaded image.
  const panBackground = useDisplaySettingsStore((s) => s.panBackground);
  const backgroundImage = useDisplaySettingsStore((s) => s.backgroundImage);
  const backgroundImageFit = useDisplaySettingsStore((s) => s.backgroundImageFit);
  const terminatorActive = panBackground === 'beam-map';
  const imageMode = panBackground === 'image' && !!backgroundImage;
  const bgActive = terminatorActive || imageMode;
  // While 'M' is held and the map is showing, the spectrum canvas stack goes
  // pointer-events:none and the Leaflet map underneath takes drag/zoom input.
  // Click-to-tune is suspended for the duration of the modifier.
  const [mapModifier, setMapModifier] = useState(false);
  const [enriching, setEnriching] = useState(false);
  const [lookupKey, setLookupKey] = useState(0);
  const [beamOverrideDeg, setBeamOverrideDeg] = useState<number | null>(null);
  const [beamInputStr, setBeamInputStr] = useState('');
  // Track whether the Leaflet map is available. Set to false when the map
  // error boundary catches a load failure (missing tiles, Leaflet init fail).
  // When false, QRZ info still renders but map-dependent UI (beam chips, etc.)
  // is hidden.
  const [mapAvailable, setMapAvailable] = useState(true);

  const qrzHome = useQrzStore((s) => s.home);
  const qrzLookup = useQrzStore((s) => s.lastLookup);
  const qrzHasXml = useQrzStore((s) => s.hasXmlSubscription);
  const qrzLookupError = useQrzStore((s) => s.lookupError);
  const qrzActive = !!qrzHome && qrzHasXml;

  const addLogEntry = useLoggerStore((s) => s.addLogEntry);
  const logPublishInFlight = useLoggerStore((s) => s.publishInFlight);
  const logPublishResult = useLoggerStore((s) => s.lastPublishResult);
  const logPublishError = useLoggerStore((s) => s.publishError);
  const logSelectedIds = useLoggerStore((s) => s.selectedIds);
  const logPublishSelected = useLoggerStore((s) => s.publishSelectedToQrz);
  const logExportAdif = useLoggerStore((s) => s.exportAdif);
  const workedSummary = useLoggerStore((s) => s.workedSummary);
  const workedSummaryLoading = useLoggerStore((s) => s.workedSummaryLoading);
  const loadWorkedSummary = useLoggerStore((s) => s.loadWorkedSummary);
  const clearWorkedSummary = useLoggerStore((s) => s.clearWorkedSummary);
  const qrzHasApiKey = useQrzStore((s) => s.hasApiKey);

  const logbookTitle = logPublishInFlight
    ? 'Logbook · Uploading…'
    : logPublishError
      ? `Logbook · ${logPublishError.length > 28 ? 'Publish failed' : logPublishError}`
      : logPublishResult
        ? logPublishResult.failedCount > 0
          ? `Logbook · ${logPublishResult.successCount} ok, ${logPublishResult.failedCount} failed`
          : `Logbook · Published ${logPublishResult.successCount}`
        : 'Logbook';

  const logSelectedCount = logSelectedIds.size;
  const publishDisabled = logSelectedCount === 0 || logPublishInFlight || !qrzHasApiKey;
  const publishTitle = !qrzHasApiKey
    ? 'Set a QRZ API key in the QRZ panel to enable publishing'
    : logSelectedCount === 0
      ? 'Select one or more rows to publish'
      : 'Publish selected QSOs to QRZ logbook';

  const logbookActions = useMemo(() => (
    <>
      <button
        type="button"
        className="btn ghost sm"
        onClick={() => void logPublishSelected(Array.from(logSelectedIds))}
        disabled={publishDisabled}
        title={publishTitle}
      >
        {logPublishInFlight ? 'Publishing…' : `Publish (${logSelectedCount})`}
      </button>
      <button
        type="button"
        className="btn ghost sm"
        onClick={() => void logExportAdif()}
        title="Export all log entries to ADIF file"
      >
        Export
      </button>
    </>
  ), [
    logExportAdif,
    logPublishInFlight,
    logPublishSelected,
    logSelectedCount,
    logSelectedIds,
    publishDisabled,
    publishTitle,
  ]);

  // Live rotator heading — drives the map's beam lines when rotctld is up so
  // the beam shows the actual antenna direction, not the great-circle bearing
  // to the current QRZ lookup.
  const rotStatus = useRotatorStore((s) => s.status);
  const rotLiveAz = rotStatus?.connected ? rotStatus.currentAz : null;
  const contact: Contact | null = qrzActive
    ? qrzStationToContact(qrzLookup, qrzHome)
    : (CONTACTS[callsign.toUpperCase()] ?? null);

  const workedSummaryCallsign = contact?.callsign ?? null;

  useEffect(() => {
    if (!workedSummaryCallsign) {
      clearWorkedSummary();
      return;
    }

    const ac = new AbortController();
    void loadWorkedSummary(workedSummaryCallsign, ac.signal);
    return () => ac.abort();
  }, [workedSummaryCallsign, loadWorkedSummary, clearWorkedSummary]);

  // Log QSO handler - creates a lazy log entry with RST based on mode
  const handleLogQso = useCallback(() => {
    if (!contact || !qrzLookup) return;

    // Determine RST based on mode: 599 for CW, 59 for phone modes
    const isCwMode = mode === 'CWU' || mode === 'CWL';
    const rstSent = isCwMode ? '599' : '59';
    const rstRcvd = isCwMode ? '599' : '59';

    const band = bandOf(vfoHz);
    const frequencyMhz = vfoHz / 1e6;

    void addLogEntry({
      callsign: contact.callsign,
      name: qrzLookup.name ?? undefined,
      frequencyMhz,
      band,
      mode,
      rstSent,
      rstRcvd,
      grid: qrzLookup.grid ?? undefined,
      country: qrzLookup.country ?? undefined,
      dxcc: qrzLookup.dxcc ?? undefined,
      cqZone: qrzLookup.cqZone ?? undefined,
      ituZone: qrzLookup.ituZone ?? undefined,
      state: qrzLookup.state ?? undefined,
    });
  }, [contact, qrzLookup, mode, vfoHz, addLogEntry]);

  const handleClearQrz = useCallback(() => {
    useQrzStore.getState().clearLookup();
    setCallsign('');
  }, [setCallsign]);

  // CW WPM is now persisted server-side via /api/cw/settings and read
  // through useCwStore — no longer threaded through the workspace context.
  const nrState = useConnectionStore((s) => s.nr);
  const dspActive =
    nrState.nrMode !== 'Off' ||
    nrState.nbMode !== 'Off' ||
    nrState.anfEnabled ||
    nrState.snbEnabled ||
    nrState.nbpNotchesEnabled;

  const csInputRef = useRef<HTMLInputElement | null>(null);
  // Handle on the Leaflet map so spectrum wheel bindings (alt / alt+shift +
  // wheel) can drive pan/zoom imperatively. Null until LeafletWorldMap mounts.
  const mapApiRef = useRef<L.Map | null>(null);
  const spectrumWheelActions = useMemo<SpectrumWheelActions>(() => ({
    onMapPan: (dx, dy) => {
      mapApiRef.current?.panBy([dx, dy], { animate: false });
    },
    onMapZoom: (delta) => {
      const m = mapApiRef.current;
      if (!m || delta === 0) return;
      m.setZoom(m.getZoom() + delta, { animate: false });
    },
  }), []);

  // QRZ is now passively engaged whenever the user has it configured (see
  // qrzActive below) — there is no separate engage/disengage step. Submitting
  // a callsign just runs a lookup; the contact card / chips render off the
  // resulting `contact` regardless of what's drawn behind the panadapter.
  const runQrzLookup = useCallback((cs?: string) => {
    const target = (cs ?? callsign).toUpperCase();
    setCallsign(target);
    setEnriching(true);
    setLookupKey((k) => k + 1);
    setBeamOverrideDeg(null);
    setBeamInputStr('');
    const qrz = useQrzStore.getState();
    if (qrz.connected && qrz.hasXmlSubscription) {
      qrz.lookup(target).finally(() => setEnriching(false));
    } else {
      // Design-mock fallback: CONTACTS lookup is synchronous; just run the scan
      // briefly so the card doesn't pop in without the visual beat.
      setTimeout(() => setEnriching(false), 700);
    }
  }, [callsign]);

  // `/` focuses the callsign input so the operator can type a call and hit Enter.
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (e.key === '/' && !(t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement)) {
        e.preventDefault();
        csInputRef.current?.focus();
        csInputRef.current?.select();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, []);

  // Hold-to-steer: while Alt/Option is down (outside a text field), the
  // Leaflet map becomes interactive and the spectrum canvas stops intercepting
  // events. Pairs with the alt+wheel zoom and alt+drag pan in
  // use-pan-tune-gesture. Keyup — and a defensive blur/visibilitychange —
  // release the modifier so you don't get stuck if focus leaves the window
  // mid-press.
  useEffect(() => {
    const inField = (t: EventTarget | null) =>
      t instanceof HTMLInputElement ||
      t instanceof HTMLTextAreaElement ||
      (t instanceof HTMLElement && t.isContentEditable);
    const onDown = (e: KeyboardEvent) => {
      if (e.repeat) return;
      if (e.key === 'Alt' && !inField(e.target)) {
        setMapModifier(true);
      }
    };
    const onUp = (e: KeyboardEvent) => {
      if (e.key === 'Alt') setMapModifier(false);
    };
    const release = () => setMapModifier(false);
    window.addEventListener('keydown', onDown);
    window.addEventListener('keyup', onUp);
    window.addEventListener('blur', release);
    document.addEventListener('visibilitychange', release);
    return () => {
      window.removeEventListener('keydown', onDown);
      window.removeEventListener('keyup', onUp);
      window.removeEventListener('blur', release);
      document.removeEventListener('visibilitychange', release);
    };
  }, []);

  const mapInteractive = terminatorActive && mapModifier && mapAvailable;

  const onCallsignSubmit = useCallback((e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    runQrzLookup();
  }, [runQrzLookup]);

  const bandLabel = bandOf(vfoHz);

  // Effective home for the map + bearing math. Null until QRZ supplies a real
  // station — the map just omits the home marker and great-circle until then.
  const effectiveHome = useMemo(() => (
    qrzHome && qrzHome.lat != null && qrzHome.lon != null
      ? {
          call: qrzHome.callsign,
          lat: qrzHome.lat,
          lon: qrzHome.lon,
          grid: qrzHome.grid ?? '',
          imageUrl: qrzHome.imageUrl ?? null,
        }
      : null
  ), [qrzHome]);

  const sp = contact && effectiveHome ? bearingDeg(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;
  const lp = (sp + 180) % 360;
  const dist = contact && effectiveHome ? distanceKm(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;

  const rotateToBearing = useCallback((brg: number) => {
    const normalized = ((brg % 360) + 360) % 360;
    setBeamOverrideDeg(normalized);
    setBeamInputStr(normalized.toFixed(0));
    const rot = useRotatorStore.getState();
    if (rot.config.enabled && rot.status?.connected) {
      void rot.setAzimuth(normalized);
    }
  }, []);

  const submitBeam = useCallback((e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const trimmed = beamInputStr.trim();
    if (!trimmed) {
      setBeamOverrideDeg(null);
      return;
    }
    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed)) return;
    rotateToBearing(parsed);
  }, [beamInputStr, rotateToBearing]);

  // --- Hero title
  const heroTitle = useMemo(() => (
    terminatorActive && contact ? (
      <>
        Panadapter · World Map ·{' '}
        <span style={{ color: 'var(--accent)' }}>{contact.callsign}</span> ·{' '}
        {Math.round(dist).toLocaleString()} km · brg {sp.toFixed(0)}°
      </>
    ) : (
      <>Panadapter · {(vfoHz / 1e6).toFixed(3)} MHz · {bandLabel}</>
    )
  ), [bandLabel, contact, dist, sp, terminatorActive, vfoHz]);

  // When no radio is connected, dim the workspace and centre the full
  // ConnectPanel on top so the eye lands on it. The backdrop is
  // pointer-events:none so the topbar stays interactive (QRZ sign-in,
  // Tweaks, etc.); the ConnectPanel itself re-enables pointer events so
  // Discover / Connect buttons still click through.
  const disconnectedOverlay = useMemo(() => {
    if (connected || settingsViewOpen) return null;
    return (
      <div
        style={{
          position: 'absolute',
          inset: 0,
          background: 'rgba(0,0,0,0.55)',
          backdropFilter: 'blur(4px)',
          pointerEvents: 'none',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          zIndex: 200,
        }}
      >
        <div style={{ pointerEvents: 'auto' }}>
          <ConnectPanel />
        </div>
      </div>
    );
  }, [connected, settingsViewOpen]);

  const desktopHost = useCapabilitiesStore((s) => s.capabilities?.host === 'desktop');
  useDesktopViewportLock(desktopHost);

  // Mobile viewport (≤900px) reactively tracked. Also honours `?mobile=1` so
  // the mobile shell can be previewed on a desktop browser without resizing.
  // Desktop-host loopback views keep the desktop shell so the browser view and
  // Photino app do not diverge. LAN clients still follow the breakpoint.
  const isMobile = useIsMobileViewport({ forceDesktop: desktopHost });

  // Bundle workspace state into a context so panel components can consume it
  // without prop-drilling through the FlexWorkspace factory.

  const workspaceCtx = useMemo(() => ({
    connected,
    moxOn,
    tunOn,
    mode,
    vfoHz,
    callsign,
    setCallsign,
    terminatorActive,
    imageMode,
    bgActive,
    panBackground,
    backgroundImage,
    backgroundImageFit,
    enriching,
    lookupKey,
    contact,
    workedSummary,
    workedSummaryLoading,
    qrzLookupError,
    qrzActive,
    mapAvailable,
    setMapAvailable,
    mapInteractive,
    effectiveHome,
    beamOverrideDeg,
    setBeamOverrideDeg,
    beamInputStr,
    setBeamInputStr,
    rotLiveAz,
    sp,
    lp,
    dist,
    heroTitle,
    csInputRef,
    runQrzLookup,
    onCallsignSubmit,
    submitBeam,
    handleLogQso,
    handleClearQrz,
    dspActive,
    logbookTitle,
    logbookActions,
  }), [
    connected, moxOn, tunOn, mode, vfoHz,
    callsign, terminatorActive, imageMode, bgActive, panBackground,
    backgroundImage, backgroundImageFit,
    enriching, lookupKey, contact, workedSummary, workedSummaryLoading,
    qrzLookupError, qrzActive, mapAvailable, mapInteractive, effectiveHome,
    beamOverrideDeg, beamInputStr, rotLiveAz, sp, lp, dist,
    heroTitle, dspActive, logbookTitle, logbookActions,
    handleLogQso, handleClearQrz, onCallsignSubmit, runQrzLookup, submitBeam,
  ]);

  if (detachedLayoutId) {
    return (
      <BandPlanProvider>
      <WorkspaceContext.Provider value={workspaceCtx}>
      <SpectrumWheelActionsContext.Provider value={spectrumWheelActions}>
      <ThemeApplier />
      <SignalIntelligenceController />
      <SmartNrController />
      <DspSceneDiagnosticsPublisher />
      <div
        className="detached-workspace-app"
        data-screen-label={`Detached Workspace · ${detachedLayoutName ?? detachedLayoutId}`}
      >
        <div className="workspace-area detached-workspace-area">
          <AlertBanner />
          <WorkspaceErrorBoundary
            key={detachedLayoutId}
            onReset={resetActiveWorkspaceLayout}
          >
            <FlexWorkspace
              key={detachedLayoutId}
              layoutId={detachedLayoutId}
            />
          </WorkspaceErrorBoundary>
        </div>
        {disconnectedOverlay}
      </div>
      </SpectrumWheelActionsContext.Provider>
      </WorkspaceContext.Provider>
      </BandPlanProvider>
    );
  }

  // Mobile viewport short-circuit. All initialization hooks above (realtime,
  // state poll, keyboard, mic uplink, service worker) have already run, so
  // the mobile shell inherits the same live data feeds and the same store
  // state — it just renders a different UI tree. SpectrumWheelActions is
  // still required because Panadapter depends on the gesture context.
  if (isMobile) {
    return (
      <WorkspaceContext.Provider value={workspaceCtx}>
        <SpectrumWheelActionsContext.Provider value={spectrumWheelActions}>
          <ThemeApplier />
          <SignalIntelligenceController />
          <SmartNrController />
          <DspSceneDiagnosticsPublisher />
              <AudioPlaybackDiagnosticsPublisher />
          <Suspense fallback={null}>
            <MobileApp />
          </Suspense>
        </SpectrumWheelActionsContext.Provider>
      </WorkspaceContext.Provider>
    );
  }

  return (
    <BandPlanProvider>
    <WorkspaceContext.Provider value={workspaceCtx}>
    <SpectrumWheelActionsContext.Provider value={spectrumWheelActions}>
    <ThemeApplier />
    <SignalIntelligenceController />
    <SmartNrController />
    <DspSceneDiagnosticsPublisher />
    <AudioPlaybackDiagnosticsPublisher />
    <div className="app" data-screen-label="01 Main Console" style={{ position: 'relative' }}>
      {/* Left layout bar — issue #241. Spans the full app height; lists named
          layouts for the active radio with switch/add/delete/reset actions. */}
      <LeftLayoutBar />

      {/* Top bar — brand on the left, transport-level inline controls
          (mode/filter/band/step/front-end/AGC/AF) in the middle, status
          pills + settings on the right. These controls stay always-visible
          across default layouts so they're reachable mid-QSO without hunting
          through the workspace (see feedback memory: top bar keeps inline
          controls). The bar sits above the disconnected overlay so QRZ
          sign-in stays usable before a radio is connected. */}
      <header className="topbar" style={{ position: 'relative', zIndex: 300 }}>
        <div className="brand">
          <div className="brand-mark">
            <svg className="brand-mark-logo" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path
                className="brand-mark-wave brand-mark-wave--top"
                d="M3.2 8.2c1.55-1.2 3.1-1.2 4.65 0l.75.58c1.55 1.2 3.1 1.2 4.65 0l.75-.58c1.55-1.2 3.1-1.2 4.65 0l1.15.88"
              />
              <path
                className="brand-mark-wave brand-mark-wave--bottom"
                d="M3.2 15.8c1.55 1.2 3.1 1.2 4.65 0l.75-.58c1.55-1.2 3.1-1.2 4.65 0l.75.58c1.55 1.2 3.1 1.2 4.65 0l1.15-.88"
              />
              <path className="brand-mark-bolt" d="M13.4 2.5 7 12.1h4.15l-1.2 9.4L17 10.55h-4.25l.65-8.05Z" />
            </svg>
          </div>
          <div className="brand-text">
            <div className="brand-name mono">OpenHpsdr Zeus</div>
            <div className="brand-sub label-xs hide-mobile">{brandSub}</div>
          </div>
        </div>

        <span className="topbar-divider hide-mobile" aria-hidden />

        <div className="topbar-controls-shell hide-mobile">
          <button
            type="button"
            className="btn sm topbar-scroll-btn"
            onClick={() => scrollTopbarControls(-1)}
            disabled={!topbarScroll.canLeft}
            title="Previous topbar controls"
            aria-label="Previous topbar controls"
          >
            <ChevronLeft size={14} aria-hidden />
          </button>
          <div
            ref={topbarControlsRef}
            className="topbar-controls"
            role="group"
            aria-label="Primary radio controls"
            tabIndex={0}
            onScroll={syncTopbarScroll}
          >
            <ModeFavorites />
            <span className="strip-divider" aria-hidden />
            <FilterPanel />
            <span className="strip-divider" aria-hidden />
            <BandFavorites />
            <span className="strip-divider" aria-hidden />
            <StepFavorites />
            <span className="strip-divider" aria-hidden />
            <div className="ctrl-group topbar-control topbar-control--front-end">
              <div className="label-xs ctrl-lbl">FRONT-END</div>
              <div className="btn-row" style={{ gap: 6, alignItems: 'center' }}>
                <PreampButton />
                <AttenuatorSlider />
              </div>
            </div>
            <div className="ctrl-group topbar-control topbar-control--agc">
              <div className="label-xs ctrl-lbl">AGC</div>
              <AgcSlider />
            </div>
            <div className="ctrl-group topbar-control topbar-control--sql">
              <div className="label-xs ctrl-lbl">SQL</div>
              <SquelchSlider />
            </div>
            <div className="ctrl-group topbar-control topbar-control--af">
              <div className="label-xs ctrl-lbl">AF</div>
              <AfGainSlider />
            </div>
          </div>
          <button
            type="button"
            className="btn sm topbar-scroll-btn"
            onClick={() => scrollTopbarControls(1)}
            disabled={!topbarScroll.canRight}
            title="Next topbar controls"
            aria-label="Next topbar controls"
          >
            <ChevronRight size={14} aria-hidden />
          </button>
        </div>

        <div className="spacer topbar-spacer" style={{ flex: 1 }} />

        {/* Settings is reached from the LeftLayoutBar (bottom slot). The
            top bar is now reserved for Disconnect when connected; while
            disconnected the centre overlay owns Discover so we mount only
            one ConnectPanel at a time. */}
        {connected && (
          <div className="topbar-connect">
            <ConnectPanel compact />
          </div>
        )}
      </header>

      {/* Workspace area — alert banner + active layout (or settings view).
          Wrapped together so the grid only needs one row for both, which
          keeps the gap between the topbar and the first panel a single
          6px unit instead of stacking two grid gaps around an empty
          alert row. */}
      <div className="workspace-area">
        <AlertBanner />
        {settingsViewOpen ? (
          <Suspense fallback={null}>
            <SettingsView
              initialTab={settingsInitialTab as SettingsTabId | undefined}
              onClose={() => setSettingsView(false)}
            />
          </Suspense>
        ) : (
          <WorkspaceErrorBoundary
            key={activeLayoutId}
            onReset={resetActiveWorkspaceLayout}
          >
            <FlexWorkspace key={activeLayoutId} />
          </WorkspaceErrorBoundary>
        )}
      </div>

      {/* Audio Suite floating window — position:fixed overlay rendered
          outside the workspace grid so it can drift to wherever the
          operator drags it without getting clipped by a parent. Mounted
          unconditionally (returns null when closed) so the open/close
          state in the store is the single source of truth. */}
      <AudioSuiteWindow route="tx" />
      <AudioSuiteWindow route="rx" />

      {/* Transport — MOX/TUN + audio + mic + macro buttons on the left,
          PA/PRE chips, then the per-radio status (radio IP, rotator, QRZ)
          and layout reset on the right. This is the single bottom-pinned
          bar; the previous separate BottomStatusBar was merged in here so
          the chrome doesn't duplicate. */}
      <div className="transport">
        <MoxButton />
        <TunButton />
        <PsToggleButton />
        <div className="transport-sep" />
        <AudioToggle />
        <MicMeter />
        <div className="transport-sep hide-mobile" />
        <CtunButton />
        <button type="button" className="btn ghost hide-mobile">SPLIT</button>
        <button type="button" className="btn ghost hide-mobile">RIT</button>
        <button type="button" className="btn ghost hide-mobile">SAVE MEM</button>
        <div className="spacer" style={{ flex: 1 }} />
        <PaTempChip />
        <div className="chip hide-mobile">
          <span className="k">PRE</span>
          <span className="v">{preampOn ? 'ON' : 'OFF'}</span>
        </div>
        <span className={`chip ${connected ? 'accent' : ''}`}>
          <span className="k">RADIO</span>
          <span className="v mono">{connected ? (endpoint ?? '—') : '—'}</span>
        </span>
        <RotatorStatusPill />
        <QrzStatusPill />
        {/* Reset acts on the active layout's tile arrangement. Disabled
            while the Settings view is showing (no active workspace to
            mutate). Add Panel now lives inside the workspace surface. */}
        <button
          type="button"
          className="btn ghost"
          onClick={() => {
            const s = useLayoutStore.getState();
            const active = s.layouts.find((l) => l.id === s.activeLayoutId);
            if (!active) return;
            setConfirmResetLayout({ id: active.id, name: active.name });
          }}
          disabled={settingsViewOpen}
          title="Reset active layout to default"
          aria-label="Reset active layout to default"
        >
          ⟳ Default
        </button>
      </div>

      {disconnectedOverlay}
      {confirmResetLayout && (
        <ConfirmDialog
          title="Reset layout"
          confirmLabel="Reset Layout"
          onCancel={() => setConfirmResetLayout(null)}
          onConfirm={() => {
            const s = useLayoutStore.getState();
            if (s.activeLayoutId === confirmResetLayout.id) {
              s.resetActiveLayout();
            }
            setConfirmResetLayout(null);
          }}
        >
          <p>Reset {confirmResetLayout.name} to the default panel arrangement?</p>
          <p>This keeps the layout tab but replaces its current panel positions.</p>
        </ConfirmDialog>
      )}
      <StartupUpdatePrompt
        status={!startupUpdateDismissed ? startupUpdate : null}
        onDismiss={() => setStartupUpdateDismissed(true)}
        onOpenSettings={() => setSettingsView(true, 'updates')}
      />
      <UpdatePrompt show={updateAvailable} onUpdate={installUpdate} />
    </div>
    </SpectrumWheelActionsContext.Provider>
    </WorkspaceContext.Provider>
    </BandPlanProvider>
  );
}

