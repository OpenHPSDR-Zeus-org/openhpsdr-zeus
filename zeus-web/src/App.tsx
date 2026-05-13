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

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { WorkspaceContext } from './layout/WorkspaceContext';
import { FlexWorkspace } from './layout/FlexWorkspace';
import { AfGainSlider } from './components/AfGainSlider';
import { AgcSlider } from './components/AgcSlider';
import { AlertBanner } from './components/AlertBanner';
import { AttenuatorSlider } from './components/AttenuatorSlider';
import { AudioToggle } from './components/AudioToggle';
import { BandFavorites } from './components/toolbar/BandFavorites';
import { ConnectPanel } from './components/ConnectPanel';
import { FilterPanel } from './components/filter/FilterPanel';
import { LeftLayoutBar } from './components/LeftLayoutBar';
import { MicMeter } from './components/MicMeter';
import { ModeFavorites } from './components/toolbar/ModeFavorites';
import { MoxButton } from './components/MoxButton';
import { PreampButton } from './components/PreampButton';
import { PsToggleButton } from './components/PsToggleButton';
import { PaTempChip } from './components/PaTempChip';
import { QrzStatusPill } from './components/QrzStatusPill';
import { RotatorStatusPill } from './components/RotatorStatusPill';
import { SettingsView, type SettingsTabId } from './components/SettingsMenu';
import { StepFavorites } from './components/toolbar/StepFavorites';
import { TunButton } from './components/TunButton';
import { BOARD_LABELS } from './api/radio';
import { useFilterRibbonOpenSync } from './components/filter/FilterRibbon';
import { useSwUpdatePrompt } from './pwa/useSwUpdatePrompt';
import { CONTACTS, bandOf } from './components/design/data';
import { bearingDeg, distanceKm } from './components/design/geo';
import { startRealtime } from './realtime/ws-client';
import { getServerBaseUrl, isCapacitorRuntime } from './serverUrl';
import { getAudioClient } from './audio/audio-client';
import { useMicUplink } from './audio/use-mic-uplink';
import { fetchState } from './api/client';
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
import { MobileApp, useIsMobileViewport } from './mobile/MobileApp';
import type L from 'leaflet';
import type { QrzStation } from './api/qrz';
import type { Contact } from './components/design/data';

// See ../state/connection-store.ts — StateDto is REST-poll only; WS is binary
// frames. 1 s poll keeps slow state (atten offset, adc overload) fresh — the
// previous 333 ms cadence accounted for ~3 of the ~5 idle-RX fetches/sec and
// drove repeated applyState/hydrateFromState fan-out into the React tree.
const STATE_POLL_MS = 1000;

export default function App() {
  useSwUpdatePrompt();
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);
  const settingsInitialTab = useLayoutStore((s) => s.settingsInitialTab);
  const setSettingsView = useLayoutStore((s) => s.setSettingsView);
  const [updateAvailable, setUpdateAvailable] = useState(false);
  const [installUpdate, setInstallUpdate] = useState<(() => Promise<void>) | null>(null);
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
  const brandSub = radioConnected !== 'Unknown'
    ? BOARD_LABELS[radioConnected]
    : 'Not Connected';

  // Per-radio layouts (issue #241): the layout-store is keyed on the active
  // BoardKind. "default" is the sentinel for "no radio yet" — discovery
  // landing flips this to e.g. "HermesLite2" / "AnanG2" and the store
  // re-fetches that radio's named-layout collection from the server.
  const loadLayoutsForRadio = useLayoutStore((s) => s.loadForRadio);
  useEffect(() => {
    const key = radioConnected !== 'Unknown' ? radioConnected : 'default';
    void loadLayoutsForRadio(key);
  }, [loadLayoutsForRadio, radioConnected]);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);

  useKeyboardShortcuts();
  useMicUplink();
  useFilterRibbonOpenSync();

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
    const stop = startRealtime();
    return () => {
      stop();
    };
  }, []);

  // Fetch host capabilities once on mount. The backend snapshot is built
  // at startup and doesn't change at runtime, so a single fetch is enough;
  // failures fall back to "no features available" which hides feature-gated
  // UI rather than rendering broken controls.
  useEffect(() => {
    void useCapabilitiesStore.getState().refresh();
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
          useConnectionStore.getState().applyState(next);
          // Hydrate persistable PS / TwoTone fields from the server's StateDto
          // so server-persisted edits (e.g. operator changed MOX delay on
          // another tab) reach this tab even after the initial connect-time
          // hydrate. Master-arm fields are session-only and skipped.
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
      if (state.mode !== prev.mode) getAudioClient().reset();
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

  const logbookActions = (
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
  );

  // Live rotator heading — drives the map's beam lines when rotctld is up so
  // the beam shows the actual antenna direction, not the great-circle bearing
  // to the current QRZ lookup.
  const rotStatus = useRotatorStore((s) => s.status);
  const rotLiveAz = rotStatus?.connected ? rotStatus.currentAz : null;
  const contact: Contact | null = qrzActive
    ? qrzStationToContact(qrzLookup, qrzHome)
    : (CONTACTS[callsign.toUpperCase()] ?? null);

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

  const [wpm, setWpm] = useState(22);
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

  const onCallsignSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    runQrzLookup();
  };

  const bandLabel = bandOf(vfoHz);

  // Effective home for the map + bearing math. Null until QRZ supplies a real
  // station — the map just omits the home marker and great-circle until then.
  const effectiveHome = qrzHome && qrzHome.lat != null && qrzHome.lon != null
    ? {
        call: qrzHome.callsign,
        lat: qrzHome.lat,
        lon: qrzHome.lon,
        grid: qrzHome.grid ?? '',
        imageUrl: qrzHome.imageUrl ?? null,
      }
    : null;

  const sp = contact && effectiveHome ? bearingDeg(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;
  const lp = (sp + 180) % 360;
  const dist = contact && effectiveHome ? distanceKm(effectiveHome.lat, effectiveHome.lon, contact.lat, contact.lon) : 0;

  function rotateToBearing(brg: number) {
    const normalized = ((brg % 360) + 360) % 360;
    setBeamOverrideDeg(normalized);
    setBeamInputStr(normalized.toFixed(0));
    const rot = useRotatorStore.getState();
    if (rot.config.enabled && rot.status?.connected) {
      void rot.setAzimuth(normalized);
    }
  }

  function submitBeam(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const trimmed = beamInputStr.trim();
    if (!trimmed) {
      setBeamOverrideDeg(null);
      return;
    }
    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed)) return;
    rotateToBearing(parsed);
  }

  // --- Hero title
  const heroTitle = terminatorActive && contact ? (() => {
    return (
      <>
        Panadapter · World Map ·{' '}
        <span style={{ color: 'var(--accent)' }}>{contact.callsign}</span> ·{' '}
        {Math.round(dist).toLocaleString()} km · brg {sp.toFixed(0)}°
      </>
    );
  })() : (
    <>Panadapter · {(vfoHz / 1e6).toFixed(3)} MHz · {bandLabel}</>
  );

  // When no radio is connected, dim the workspace and centre the full
  // ConnectPanel on top so the eye lands on it. The backdrop is
  // pointer-events:none so the topbar stays interactive (QRZ sign-in,
  // Tweaks, etc.); the ConnectPanel itself re-enables pointer events so
  // Discover / Connect buttons still click through.
  const disconnectedOverlay = useMemo(() => {
    if (connected) return null;
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
  }, [connected]);

  // Mobile viewport (≤900px) reactively tracked. Also honours `?mobile=1` so
  // the mobile shell can be previewed on a desktop browser without resizing.
  // The matchMedia listener is mounted in a layout effect so we don't paint
  // a stale variant after a window resize / device-rotate.
  const isMobile = useIsMobileViewport();

  // Bundle workspace state into a context so panel components can consume it
  // without prop-drilling through the FlexWorkspace factory.
  // eslint-disable-next-line react-hooks/exhaustive-deps
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
    dspActive,
    wpm,
    setWpm,
    logbookTitle,
    logbookActions,
  }), [
    connected, moxOn, tunOn, mode, vfoHz,
    callsign, terminatorActive, imageMode, bgActive, panBackground,
    backgroundImage, backgroundImageFit,
    enriching, lookupKey, contact,
    qrzLookupError, qrzActive, mapAvailable, mapInteractive, effectiveHome,
    beamOverrideDeg, beamInputStr, rotLiveAz, sp, lp, dist,
    // heroTitle and logbookActions are ReactNodes (new objects each render);
    // their underlying primitive deps are already above, so omit them here.
    dspActive, wpm, logbookTitle,
    handleLogQso, runQrzLookup,
  ]);

  // Mobile viewport short-circuit. All initialization hooks above (realtime,
  // state poll, keyboard, mic uplink, service worker) have already run, so
  // the mobile shell inherits the same live data feeds and the same store
  // state — it just renders a different UI tree. SpectrumWheelActions is
  // still required because Panadapter depends on the gesture context.
  if (isMobile) {
    return (
      <SpectrumWheelActionsContext.Provider value={spectrumWheelActions}>
        <MobileApp />
      </SpectrumWheelActionsContext.Provider>
    );
  }

  return (
    <BandPlanProvider>
    <WorkspaceContext.Provider value={workspaceCtx}>
    <SpectrumWheelActionsContext.Provider value={spectrumWheelActions}>
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
            <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
              <circle cx="12" cy="12" r="3" fill="var(--accent)" />
              <circle cx="12" cy="12" r="7" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.5" />
              <circle cx="12" cy="12" r="11" fill="none" stroke="var(--accent)" strokeWidth="1" opacity="0.25" />
            </svg>
          </div>
          <div className="brand-text">
            <div className="brand-name mono">OpenHpsdr Zeus</div>
            <div className="brand-sub label-xs hide-mobile">{brandSub}</div>
          </div>
        </div>

        <span className="topbar-divider hide-mobile" aria-hidden />

        <div className="topbar-controls hide-mobile">
          <ModeFavorites />
          <span className="strip-divider" aria-hidden />
          <FilterPanel />
          <span className="strip-divider" aria-hidden />
          <BandFavorites />
          <span className="strip-divider" aria-hidden />
          <StepFavorites />
          <span className="strip-divider" aria-hidden />
          <div className="ctrl-group" style={{ minWidth: 200 }}>
            <div className="label-xs ctrl-lbl">FRONT-END</div>
            <div className="btn-row" style={{ gap: 6, alignItems: 'center' }}>
              <PreampButton />
              <AttenuatorSlider />
            </div>
          </div>
          <div className="ctrl-group" style={{ minWidth: 160 }}>
            <div className="label-xs ctrl-lbl">AGC</div>
            <AgcSlider />
          </div>
          <div className="ctrl-group" style={{ minWidth: 160 }}>
            <div className="label-xs ctrl-lbl">AF</div>
            <AfGainSlider />
          </div>
        </div>

        <div className="spacer" style={{ flex: 1 }} />

        {/* Settings is reached from the LeftLayoutBar (bottom slot). The
            top bar is now reserved for Disconnect when connected; while
            disconnected the centre overlay owns Discover so we mount only
            one ConnectPanel at a time. */}
        {connected && <ConnectPanel compact />}
      </header>

      {/* Workspace area — alert banner + active layout (or settings view).
          Wrapped together so the grid only needs one row for both, which
          keeps the gap between the topbar and the first panel a single
          6px unit instead of stacking two grid gaps around an empty
          alert row. */}
      <div className="workspace-area">
        <AlertBanner />
        {settingsViewOpen ? (
          <SettingsView
            initialTab={settingsInitialTab as SettingsTabId | undefined}
            onClose={() => setSettingsView(false)}
          />
        ) : (
          <FlexWorkspace key={activeLayoutId} />
        )}
      </div>

      {/* Transport — MOX/TUN + audio + mic + macro buttons on the left,
          PA/PRE chips, then the per-radio status (radio IP, rotator, QRZ)
          and "+ Add Panel" trigger on the right. This is the single
          bottom-pinned bar; the previous separate BottomStatusBar was
          merged in here so the chrome doesn't duplicate. */}
      <div className="transport">
        <MoxButton />
        <TunButton />
        <PsToggleButton />
        <div className="transport-sep" />
        <AudioToggle />
        <MicMeter />
        <div className="transport-sep hide-mobile" />
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
        {/* Add Panel + Reset live together at the bottom-right: both act on
            the active layout's tile arrangement, so keeping them adjacent
            makes the relationship obvious. Disabled while the Settings
            view is showing (no active workspace to mutate). */}
        <button
          type="button"
          className="btn"
          onClick={() => useLayoutStore.getState().setAddPanelOpen(true)}
          disabled={settingsViewOpen}
          title="Add a panel to the active layout"
        >
          + Add Panel
        </button>
        <button
          type="button"
          className="btn ghost"
          onClick={() => {
            const s = useLayoutStore.getState();
            const active = s.layouts.find((l) => l.id === s.activeLayoutId);
            if (!active) return;
            if (!window.confirm(`Reset “${active.name}” to the default panel arrangement?`)) return;
            s.resetActiveLayout();
          }}
          disabled={settingsViewOpen}
          title="Reset active layout to default"
          aria-label="Reset active layout to default"
        >
          ⟳ Default
        </button>
      </div>

      {disconnectedOverlay}
      <UpdatePrompt show={updateAvailable} onUpdate={installUpdate} />
    </div>
    </SpectrumWheelActionsContext.Provider>
    </WorkspaceContext.Provider>
    </BandPlanProvider>
  );
}

// QRZ XML gives us a sparser record than the design-time Contact type; fill
// the design-only fields ("local", "rig", "ant", "age"…) with em-dashes so
// QrzCard still renders without needing a schema change.
function qrzStationToContact(s: QrzStation | null, home: QrzStation | null): Contact | null {
  if (!s || s.lat == null || s.lon == null) return null;
  const bearing = home?.lat != null && home?.lon != null
    ? bearingDeg(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const distance = home?.lat != null && home?.lon != null
    ? distanceKm(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const first = (s.firstName || s.name || '').trim().charAt(0).toUpperCase();
  const last = (s.name || '').trim().split(/\s+/).pop()?.charAt(0).toUpperCase() ?? '';
  const initials = (first + last) || s.callsign.slice(0, 2);
  const location = [s.city, s.state, s.country].filter(Boolean).join(', ') || (s.country ?? '—');
  const fullName = [s.firstName, s.name].filter(Boolean).join(' ') || '—';
  return {
    callsign: s.callsign,
    name: fullName,
    location,
    grid: s.grid ?? '—',
    cq: s.cqZone != null ? String(s.cqZone).padStart(2, '0') : '—',
    itu: s.ituZone != null ? String(s.ituZone).padStart(2, '0') : '—',
    latlon: `${Math.abs(s.lat).toFixed(2)}°${s.lat >= 0 ? 'N' : 'S'} / ${Math.abs(s.lon).toFixed(2)}°${s.lon >= 0 ? 'E' : 'W'}`,
    lat: s.lat,
    lon: s.lon,
    local: '—',
    qsl: '—',
    licensed: '—',
    initials,
    flag: '',
    bearing,
    distance,
    age: 0,
    class: '—',
    rig: '—',
    ant: '—',
    power: '—',
    qth: s.city ?? s.country ?? '—',
    email: '—',
    photoUrl: s.imageUrl ?? undefined,
    qrzUrl: `https://www.qrz.com/db/${s.callsign}`,
  };
}
