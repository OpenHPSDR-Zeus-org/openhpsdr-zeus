// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Mobile single-column shell. Renders the same widgets and stores the
// desktop layout uses, in a vertical stack tuned for a touch viewport.
// The layout shape comes from the Zeus Mobile design hand-off; colours,
// type, and controls are the existing Zeus surface (tokens.css + the
// component library) per the maintainer's brief.

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import {
  setMode,
  type RxMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxAudioProfileStore } from '../state/tx-audio-profile-store';
import { useQrzStore } from '../state/qrz-store';
import { useTxStore } from '../state/tx-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { VfoDisplay } from '../components/VfoDisplay';
import { SMeterLive } from '../components/SMeterLive';
import { Panadapter } from '../components/Panadapter';
import { WaterfallSurface } from '../components/WaterfallSurface';
import { MobilePttButton } from '../components/MobilePttButton';
import { MobileFrequencyTrackpad } from './MobileFrequencyTrackpad';
import { MobileInstallPrompt } from './MobileInstallPrompt';
import { TunButton } from '../components/TunButton';
import { PsToggleButton } from '../components/PsToggleButton';
import { AudioToggle } from '../components/AudioToggle';
import { BandButtons } from '../components/BandButtons';
import { TuningStepWidget } from '../components/TuningStepWidget';
import { AgcSlider } from '../components/AgcSlider';
import { DriveSlider } from '../components/DriveSlider';
import { MicGainSlider } from '../components/MicGainSlider';
import { TunePowerSlider } from '../components/TunePowerSlider';
import { useVfoLockStore } from '../state/vfo-lock-store';
import { saveReceiverBandModeMemory } from '../util/band-memory';
import { ConnectPanel } from '../components/ConnectPanel';
import { LeafletWorldMap } from '../components/design/LeafletWorldMap';
import { LeafletMapErrorBoundary } from '../components/design/LeafletMapErrorBoundary';
import { bandOf } from '../components/design/data';
import { RotatorDialPanel } from '../layout/panels/RotatorDialPanel';
import { QrzPanel } from '../layout/panels/QrzPanel';
import { DspFlexPanel } from '../layout/panels/DspFlexPanel';
import { useRotatorStore } from '../state/rotator-store';
import {
  clampSplit,
  readInitialSplit,
  writeLegacySplit,
} from '../layout/spectrum-split';
import './mobile.css';

const MODES: readonly RxMode[] = ['LSB', 'USB', 'CWL', 'CWU', 'AM', 'FM', 'DIGU', 'FREEDV'];

const MOBILE_VIEWPORT_LOCK =
  'width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover';

function useMobileZoomLock(): void {
  useEffect(() => {
    if (typeof document === 'undefined') return;
    const viewport = document.querySelector<HTMLMetaElement>('meta[name="viewport"]');
    const previousViewport = viewport?.getAttribute('content') ?? null;
    viewport?.setAttribute('content', MOBILE_VIEWPORT_LOCK);

    const preventZoomGesture = (event: Event) => event.preventDefault();
    const listenerOptions: AddEventListenerOptions = { passive: false };
    document.addEventListener('gesturestart', preventZoomGesture, listenerOptions);
    document.addEventListener('gesturechange', preventZoomGesture, listenerOptions);
    document.addEventListener('gestureend', preventZoomGesture, listenerOptions);

    return () => {
      if (viewport && previousViewport != null) {
        viewport.setAttribute('content', previousViewport);
      }
      document.removeEventListener('gesturestart', preventZoomGesture, listenerOptions);
      document.removeEventListener('gesturechange', preventZoomGesture, listenerOptions);
      document.removeEventListener('gestureend', preventZoomGesture, listenerOptions);
    };
  }, []);
}

type MobileTab = 'radio' | 'tools';
type MobileToolId = 'tx' | 'rotator' | 'qrz' | 'dsp';

function normalizeAzimuth(deg: number | null | undefined): number | null {
  if (deg == null || !Number.isFinite(deg)) return null;
  return ((deg % 360) + 360) % 360;
}

function compassPoint(deg: number): string {
  const points = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;
  return points[Math.round((deg % 360) / 45) % 8] ?? 'N';
}

export function MobileApp() {
  useMobileZoomLock();
  const status = useConnectionStore((s) => s.status);
  const endpoint = useConnectionStore((s) => s.endpoint);
  const lastEndpoint = useConnectionStore((s) => s.lastConnectedEndpoint);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const qrzHome = useQrzStore((s) => s.home);
  const qrzHasXml = useQrzStore((s) => s.hasXmlSubscription);
  const connected = status === 'Connected';

  const radioLabel = endpoint || lastEndpoint || '—';
  const bandLabel = bandOf(vfoHz);
  const freqMHz = (vfoHz / 1_000_000).toFixed(3);

  const [activeTab, setActiveTab] = useState<MobileTab>('radio');
  // When activeTab === 'tools', toolOpen=null shows the tile grid;
  // toolOpen='tx' (etc) shows that tool's detail page with a back button.
  const [toolOpen, setToolOpen] = useState<MobileToolId | null>(null);

  // Force the waterfall into AUTO dB-range whenever the mobile shell mounts.
  // FIXED mode (the desktop default) reads grainy on a phone where there's
  // no easy way to drag-shift the range; mobile has the AUTO/FIXED toggle
  // hidden in mobile.css. We pin AUTO every mount rather than at first-run
  // only so a value flipped to FIXED on desktop and persisted to
  // localStorage doesn't leak into a phone session.
  useEffect(() => {
    if (!useDisplaySettingsStore.getState().autoRange) {
      useDisplaySettingsStore.getState().setAutoRange(true);
    }
  }, []);

  // Radio selector overlay. Open from the topbar; auto-close once a connect
  // *transition* completes (status flips Disconnected → Connected) so the
  // operator lands back on the radio screen without an extra dismiss tap.
  // Watching the edge — not the steady state — lets the operator open the
  // selector while already connected (to disconnect or switch radios)
  // without it slamming shut on first render.
  const [selectorOpen, setSelectorOpen] = useState(false);
  const prevConnectedRef = useRef(connected);
  useEffect(() => {
    if (selectorOpen && !prevConnectedRef.current && connected) {
      setSelectorOpen(false);
    }
    prevConnectedRef.current = connected;
  }, [selectorOpen, connected]);

  // QRZ map background — mirrors desktop's behaviour. The map renders behind
  // the spectrum stack when the operator has a QTH home pinned and an XML
  // subscription (same gate as App.tsx's qrzActive). LeafletWorldMap takes a
  // MapStation shape (call, not callsign), so we adapt the QrzStation here
  // the same way App.tsx:446-454 does.
  const effectiveHome =
    qrzHome && qrzHome.lat != null && qrzHome.lon != null && qrzHasXml
      ? {
          call: qrzHome.callsign,
          lat: qrzHome.lat,
          lon: qrzHome.lon,
          grid: qrzHome.grid ?? '',
          imageUrl: qrzHome.imageUrl ?? null,
        }
      : null;

  // Panadapter background mode — same store the desktop reads from, so a
  // setting picked on desktop (basic / beam-map / image) follows the operator
  // to mobile via localStorage. Mobile has no Display settings entry point of
  // its own yet; this is inheritance-only by design.
  const panBackground = useDisplaySettingsStore((s) => s.panBackground);
  const backgroundImage = useDisplaySettingsStore((s) => s.backgroundImage);
  const backgroundImageFit = useDisplaySettingsStore((s) => s.backgroundImageFit);
  const terminatorActive = panBackground === 'beam-map';
  const imageMode = panBackground === 'image' && !!backgroundImage;

  return (
    <div className="m-app">
      <header className="m-topbar">
        <div className="m-brand">
          <span className="brand-mark m-brand-mark">
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
          </span>
          <span className="m-brand-text">
            <span className="m-brand-pre">OpenHpsdr</span>
            <span className="m-brand-name">Zeus</span>
          </span>
        </div>
        <nav className="m-tabs" role="tablist" aria-label="Mobile sections">
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'radio'}
            className={`m-tab ${activeTab === 'radio' ? 'on' : ''}`}
            onClick={() => setActiveTab('radio')}
          >
            Radio
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={activeTab === 'tools'}
            className={`m-tab ${activeTab === 'tools' ? 'on' : ''}`}
            onClick={() => {
              setActiveTab('tools');
              // Tapping the tab while inside a tool returns to the grid.
              setToolOpen(null);
            }}
          >
            Tools
          </button>
        </nav>
        <button
          type="button"
          className="m-conn-btn"
          data-connected={connected}
          onClick={() => setSelectorOpen(true)}
          title={connected ? `Connected to ${radioLabel} — tap to manage` : 'Tap to discover radios'}
          aria-label={connected ? `Connected to ${radioLabel} — tap to manage` : 'Tap to discover radios'}
        >
          <span
            className={`m-conn-led${connected ? '' : ' m-conn-led--off'}`}
            aria-hidden
          />
          <span className={`m-conn-action${connected ? '' : ' m-conn-action--primary'}`}>
            {connected ? 'Disconnect' : 'Connect'}
          </span>
        </button>
      </header>

      {selectorOpen && (
        <div
          className="m-selector-backdrop"
          role="dialog"
          aria-modal="true"
          aria-label="Radio selector"
          onClick={(e) => {
            // Backdrop click dismisses; clicks inside the sheet bubble through
            // to here too, so guard on currentTarget.
            if (e.target === e.currentTarget) setSelectorOpen(false);
          }}
        >
          <div className="m-selector-sheet">
            <header className="m-selector-head">
              <span className="m-selector-title">Radio</span>
              <button
                type="button"
                className="m-selector-close"
                onClick={() => setSelectorOpen(false)}
                aria-label="Close radio selector"
              >
                ✕
              </button>
            </header>
            <div className="m-selector-body">
              <ConnectPanel />
            </div>
          </div>
        </div>
      )}

      <main className="m-stack">
        <MobileInstallPrompt />
        {activeTab === 'radio' ? (
          <>
            <Section label="Frequency" meta="VFO A">
              <div className="m-vfo-grid">
                <div className="m-vfo-wrap">
                  <VfoDisplay />
                </div>
                <div className="m-vfo-side">
                  <div className="m-vfo-side-cell"><AudioToggle /></div>
                  <div className="m-vfo-side-cell"><VfoLockButton /></div>
                </div>
              </div>
            </Section>

            <SMeterSection />


            <Section label="Panadapter" meta={`${freqMHz} MHz · ${bandLabel}`}>
              <MobileSpectrumStack
                imageMode={imageMode}
                terminatorActive={terminatorActive}
                effectiveHome={effectiveHome}
                backgroundImage={backgroundImage}
                backgroundImageFit={backgroundImageFit}
              />
            </Section>

            <div className="m-mox-block">
              <MicGate />
              <MobileTxProfileSelect />
              <div className="m-ptt-row">
                <MobilePttButton />
                <div className="m-ptt-tun"><TunButton /></div>
              </div>
            </div>

            <Section label="Mode · Band">
              <div className="m-controls">
                <div className="m-control-row m-control-row--2">
                  <label className="m-control">
                    <span className="m-control-lbl">Mode</span>
                    <select
                      className="m-select"
                      value={mode}
                      disabled={!connected}
                      onChange={(e) => {
                        const nextMode = e.target.value as RxMode;
                        useConnectionStore.setState({ mode: nextMode });
                        saveReceiverBandModeMemory('A', nextMode);
                        setMode(nextMode)
                          .then((state) => {
                            applyState(state);
                            saveReceiverBandModeMemory('A', undefined, state);
                          })
                          .catch(() => undefined);
                      }}
                    >
                      {MODES.map((m) => (
                        <option key={m} value={m}>{m}</option>
                      ))}
                    </select>
                  </label>
                  <label className="m-control">
                    <span className="m-control-lbl">Band</span>
                    <BandButtons />
                  </label>
                </div>
              </div>
            </Section>
          </>
        ) : toolOpen == null ? (
          <ToolsGrid onPick={setToolOpen} />
        ) : (
          <ToolDetail tool={toolOpen} onBack={() => setToolOpen(null)} />
        )}
      </main>
      <MobileFrequencyTrackpad />
    </div>
  );
}

type MobileSpectrumHome = {
  call: string;
  lat: number;
  lon: number;
  grid: string;
  imageUrl: string | null;
};

// Panadapter + waterfall stack with a draggable divider, mirroring the desktop
// HeroPanel splitter. Lifted into its own component so the pointermove storm
// during a drag re-renders only this subtree, not the whole MobileApp (which
// subscribes to many stores). The split fraction shares the desktop's
// localStorage mirror via spectrum-split, so a balance chosen on either
// surface follows the operator to the other.
function MobileSpectrumStack({
  imageMode,
  terminatorActive,
  effectiveHome,
  backgroundImage,
  backgroundImageFit,
}: {
  imageMode: boolean;
  terminatorActive: boolean;
  effectiveHome: MobileSpectrumHome | null;
  backgroundImage: string | null;
  backgroundImageFit: string;
}) {
  const gridRef = useRef<HTMLDivElement | null>(null);
  const [split, setSplit] = useState(() => readInitialSplit(null));
  const [dragging, setDragging] = useState(false);

  // Drag the divider to rebalance panadapter vs. waterfall. Window-level
  // pointer listeners keep tracking when the finger outruns the slim hit area;
  // touch-action:none on the handle (mobile.css) stops the page from scrolling
  // under the gesture. preventDefault suppresses the synthetic mouse / text
  // selection that would otherwise fire on some touch stacks.
  const onSplitterPointerDown = useCallback(
    (e: ReactPointerEvent) => {
      e.preventDefault();
      e.stopPropagation();
      const grid = gridRef.current;
      if (!grid) return;
      const rect = grid.getBoundingClientRect();
      if (rect.height <= 0) return;
      setDragging(true);
      let latest = split;
      const onMove = (ev: PointerEvent) => {
        latest = clampSplit((ev.clientY - rect.top) / rect.height);
        setSplit(latest);
      };
      const onEnd = () => {
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onEnd);
        window.removeEventListener('pointercancel', onEnd);
        setDragging(false);
        writeLegacySplit(latest);
      };
      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onEnd);
      window.addEventListener('pointercancel', onEnd);
    },
    [split],
  );

  return (
    <div className={`m-pan-stack${imageMode ? ' image-mode' : ''}`}>
      {imageMode && (
        <div
          className={`image-layer ${backgroundImageFit}`}
          style={{ backgroundImage: `url(${backgroundImage})` }}
        />
      )}
      {terminatorActive && effectiveHome && (
        // The "map-layer visible" pair pulls in the desktop sizing
        // chain in layout.css:451-470 — without it the inner Leaflet
        // .leaflet-container collapses to 0×0 and tiles never paint.
        // .m-map-layer still applies for any mobile-only tweaks.
        <div className="m-map-layer map-layer visible">
          <LeafletMapErrorBoundary fallback={null} onError={() => undefined}>
            <LeafletWorldMap home={effectiveHome} target={null} active interactive={false} />
          </LeafletMapErrorBoundary>
        </div>
      )}
      <div
        ref={gridRef}
        className="m-pan-spectrum"
        style={{ gridTemplateRows: `${split}fr 10px ${1 - split}fr` }}
      >
        <div className="m-pan">
          <Panadapter touchMode="pinch-only" />
        </div>
        <div
          className={`m-spectrum-splitter${dragging ? ' dragging' : ''}`}
          role="separator"
          aria-orientation="horizontal"
          aria-label="Resize panadapter and waterfall"
          title="Drag to resize panadapter / waterfall"
          onPointerDown={onSplitterPointerDown}
        />
        <div className="m-wf">
          {/* Opaque under beam-map: dark Esri tiles + near-black
              noise floor blended and the waterfall read as solid
              black. Transparent under imageMode so the user's
              picture shows through both halves (matches desktop). */}
          <WaterfallSurface transparent={imageMode} touchMode="pinch-only" />
        </div>
      </div>
    </div>
  );
}

// Mobile TX audio profile picker. Uses the unified TX Audio Profile store
// (shared with the desktop TxAudioProfileBar): loads the list + last-loaded
// pointer for selection, applies on pick. Apply records last-loaded server-side.
function MobileTxProfileSelect() {
  const profiles = useTxAudioProfileStore((s) => s.profiles);
  const loaded = useTxAudioProfileStore((s) => s.loaded);
  const lastLoadedId = useTxAudioProfileStore((s) => s.lastLoadedId);
  const busy = useTxAudioProfileStore((s) => s.busy);
  const load = useTxAudioProfileStore((s) => s.load);
  const apply = useTxAudioProfileStore((s) => s.apply);
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const profileMenuRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!loaded) void load();
  }, [loaded, load]);

  useEffect(() => {
    if (busy) setProfileMenuOpen(false);
  }, [busy]);

  useEffect(() => {
    if (!profileMenuOpen) return;
    const onPointerDown = (event: PointerEvent) => {
      const root = profileMenuRef.current;
      const target = event.target;
      if (root && target instanceof Node && !root.contains(target)) {
        setProfileMenuOpen(false);
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setProfileMenuOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [profileMenuOpen]);

  const selectedId = lastLoadedId && profiles.some((p) => p.id === lastLoadedId) ? lastLoadedId : '';
  const selectedProfile = profiles.find((p) => p.id === selectedId);

  const selectProfile = useCallback(
    (id: string) => {
      setProfileMenuOpen(false);
      setError(null);
      void apply(id).then((result) => {
        if (!result.ok) setError(result.error ?? 'TX profile apply failed');
      });
    },
    [apply],
  );

  const displayedMessage = error ?? (selectedProfile ? `${selectedProfile.name} active` : 'No profile');

  return (
    <div className="m-tx-profile" aria-label="TX audio profile selector">
      <div className="m-control m-profile-picker" ref={profileMenuRef}>
        <span className="m-control-lbl">TX Profile</span>
        <button
          type="button"
          className="m-profile-trigger"
          aria-label="TX audio profile"
          aria-haspopup="listbox"
          aria-expanded={profileMenuOpen}
          disabled={busy}
          onClick={() => setProfileMenuOpen((open) => !open)}
        >
          <span>{selectedProfile?.name ?? 'Select profile'}</span>
          <span className="m-profile-caret" aria-hidden>
            {profileMenuOpen ? '^' : 'v'}
          </span>
        </button>
        {profileMenuOpen && (
          <div
            className="m-profile-menu"
            role="listbox"
            aria-label="TX audio profile options"
          >
            {profiles.length === 0 && (
              <div className="m-profile-option" aria-disabled>
                No profiles saved
              </div>
            )}
            {profiles.map((profile) => {
              const active = profile.id === selectedId;
              return (
                <button
                  key={profile.id}
                  type="button"
                  role="option"
                  aria-selected={active}
                  className={`m-profile-option${active ? ' is-active' : ''}`}
                  onClick={() => selectProfile(profile.id)}
                >
                  {profile.name}
                </button>
              );
            })}
          </div>
        )}
      </div>
      <div
        className={`m-tx-profile-status${error ? ' is-error' : ''}`}
        title={displayedMessage}
      >
        {displayedMessage}
      </div>
    </div>
  );
}

// Mic permission gate. useMicUplink() in App.tsx kicks getUserMedia at mount
// — that call falls outside any user gesture, and on iOS Safari over plain
// HTTP-on-LAN-IP the origin isn't a secure context either, so the call is
// silently rejected. Surfaces the error and offers an "Allow microphone"
// button that re-requests permission FROM the user gesture, where Safari
// will actually present the prompt. Reloads on success so the existing
// uplink hook picks up the granted permission cleanly.
function MicGate() {
  const micError = useTxStore((s) => s.micError);
  const [granting, setGranting] = useState(false);

  if (!micError) return null;

  // Non-secure-context detection. Localhost is treated as secure even over
  // HTTP, but a LAN IP like 192.168.x.y over plain HTTP fails this and is
  // unrecoverable without an HTTPS scheme.
  const insecure =
    typeof window !== 'undefined' && window.isSecureContext === false;

  const onAllow = async () => {
    setGranting(true);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      // Tear the temp stream down — useMicUplink will open its own once we
      // reload. Holding it would tie up the mic.
      for (const t of stream.getTracks()) t.stop();
      useTxStore.getState().setMicError(null);
      // Reload so useMicUplink's mount-effect re-runs with permission
      // already granted; in-place retry would need plumbing the hook to
      // accept a reset token.
      window.location.reload();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      useTxStore.getState().setMicError(msg);
    } finally {
      setGranting(false);
    }
  };

  return (
    <div className="m-mic-gate" role="alert">
      <div className="m-mic-gate__msg">
        <strong>Microphone unavailable.</strong>{' '}
        {insecure
          ? 'Mobile browsers require HTTPS for microphone access. The Zeus server prints an https:// LAN URL at startup — open that one on your phone instead. The first visit will warn that the certificate is self-signed; tap through to proceed.'
          : micError}
      </div>
      {!insecure && (
        <button
          type="button"
          className="m-mic-gate__btn"
          onClick={onAllow}
          disabled={granting}
        >
          {granting ? 'Requesting…' : 'Allow microphone'}
        </button>
      )}
    </div>
  );
}

// S-Meter card. Wrapped in its own component so subscribing to TX state
// for the in-header SWR + MIC chips doesn't re-render the whole MobileApp
// at the meter's update rate. During TX, the chips render in the section
// header (right-aligned) instead of below the meter — keeping the body
// height fixed so keying MOX doesn't push the PTT button down.
function SMeterSection() {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const swr = useTxStore((s) => s.swr);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const transmitting = moxOn || tunOn;
  const swrColor = swr >= 3 ? 'var(--tx)' : swr >= 2 ? 'var(--power)' : 'var(--fg-0)';

  const chips = transmitting ? (
    <>
      <span className="chip mono">
        <span className="k">SWR</span>
        <span className="v" style={{ color: swrColor }}>{swr.toFixed(2)}</span>
      </span>
      <span className="chip mono">
        <span className="k">MIC</span>
        <span className="v">{micDbfs.toFixed(0)} dBfs</span>
      </span>
    </>
  ) : null;

  return (
    <Section label="S-Meter" meta={transmitting ? 'TX' : 'RX'} extra={chips} tight>
      <SMeterLive hideChips />
    </Section>
  );
}

// Padlock toggle that pins the VFO. The lock state lives in vfo-lock-store
// and is consulted by `api/client.setVfo` so the trackpad, band-button taps,
// scrolls, and digit edits all no-op while engaged. Glyphs
// are inline SVG so the button looks the same across iOS / Android / desktop
// without depending on emoji font availability.
function VfoLockButton() {
  const locked = useVfoLockStore((s) => s.locked);
  const toggle = useVfoLockStore((s) => s.toggle);
  return (
    <button
      type="button"
      onClick={toggle}
      aria-pressed={locked}
      aria-label={locked ? 'VFO locked — tap to unlock' : 'VFO unlocked — tap to lock'}
      title={locked ? 'VFO locked — tap to unlock' : 'Lock VFO'}
      className={`btn m-lock-btn ${locked ? 'on' : ''}`}
    >
      {locked ? (
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
          <rect x="5" y="11" width="14" height="9" rx="1.5" fill="currentColor" />
          <path
            d="M8 11V8a4 4 0 0 1 8 0v3"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
          />
        </svg>
      ) : (
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden>
          <rect x="5" y="11" width="14" height="9" rx="1.5" fill="currentColor" />
          <path
            d="M8 11V8a4 4 0 0 1 7.5-2"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
          />
        </svg>
      )}
      <span className="m-lock-lbl">{locked ? 'LOCKED' : 'LOCK'}</span>
    </button>
  );
}

function Section({
  label,
  meta,
  extra,
  tight,
  children,
}: {
  label: string;
  meta?: string;
  /** Right-aligned slot in the section header. Used by the S-Meter card to
   *  surface SWR + MIC dBfs chips during TX without growing the body and
   *  shifting the PTT button below it. */
  extra?: ReactNode;
  /** Strip the body padding — used by the SMeter section so the meter
   *  fills the chrome edge to edge. */
  tight?: boolean;
  children: ReactNode;
}) {
  return (
    <section className={`m-section${tight ? ' m-section--tight' : ''}`}>
      <header className="m-section-head">
        <span className="m-section-led" />
        <span className="m-section-label">{label}</span>
        {meta && <span className="m-section-meta">· {meta}</span>}
        {extra && <span className="m-section-extra">{extra}</span>}
      </header>
      <div className="m-section-body">{children}</div>
    </section>
  );
}

// ============================================================================
// Tools tab — drawer (2-col tile grid) and per-tool detail pages.
//
// Per the maintainer brief: no new widgets. Every tool detail page embeds
// the existing desktop panel inside a positioned wrapper so it scales
// down to the 480-wide mobile column. The tile grid + back-button chrome
// is the only mobile-specific UI here.
// ============================================================================

const TOOL_META: Record<MobileToolId, { name: string; desc: string; sub: string; icon: ReactNode }> = {
  tx: {
    name: 'TX Controls',
    desc: 'Step, AGC-T, drive, tune, mic gain',
    sub: 'VFO A',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden>
        <circle cx="12" cy="12" r="3" />
        <path d="M5.6 5.6a9 9 0 0 0 0 12.8M18.4 5.6a9 9 0 0 1 0 12.8" />
        <path d="M2.8 2.8a13 13 0 0 0 0 18.4M21.2 2.8a13 13 0 0 1 0 18.4" />
      </svg>
    ),
  },
  rotator: {
    name: 'Rotator',
    desc: 'Compass heading & antenna jog',
    sub: 'Heading',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden>
        <circle cx="12" cy="12" r="9" />
        <path d="M12 4v2M12 18v2M4 12h2M18 12h2" />
        <path d="M14.5 8 12 14l-2.5-6 5 0z" fill="currentColor" stroke="none" opacity="0.85" />
      </svg>
    ),
  },
  qrz: {
    name: 'QRZ Lookup',
    desc: 'Find callsigns, grids & bios',
    sub: 'Online',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden>
        <circle cx="11" cy="11" r="6" />
        <path d="m20 20-4.5-4.5" />
        <path d="M9 11h4M11 9v4" opacity="0.6" />
      </svg>
    ),
  },
  dsp: {
    name: 'DSP Control',
    desc: 'NB, NR2, ANF, notch & filters',
    sub: 'Filters',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden>
        <path d="M3 12h2M19 12h2" />
        <path d="M6 12c1 0 1-6 2-6s1 12 2 12 1-9 2-9 1 6 2 6 1-3 2-3" />
      </svg>
    ),
  },
};

function ToolsGrid({ onPick }: { onPick: (id: MobileToolId) => void }) {
  // Surface live-state badges on each tile so the operator sees what's
  // actually doing something at a glance — drives the design's "Active" /
  // "072° NE" / "NR2 On" chips without inventing new state.
  const rotConnected = useRotatorStore((s) => !!s.status?.connected);
  const rotAz = useRotatorStore((s) => normalizeAzimuth(s.status?.currentAz));
  const txBadge = useTxStore((s) => (s.moxOn ? 'TX' : s.tunOn ? 'TUN' : 'Ready'));

  const tools: ReadonlyArray<{
    id: MobileToolId;
    badge: { text: string; tone: 'green' | 'amber' | 'muted' };
  }> = [
    { id: 'tx', badge: { text: txBadge, tone: txBadge === 'Ready' ? 'green' : 'amber' } },
    {
      id: 'rotator',
      badge: rotConnected && rotAz != null
        ? { text: `${Math.round(rotAz).toString().padStart(3, '0')}° ${compassPoint(rotAz)}`, tone: 'muted' }
        : { text: 'Offline', tone: 'muted' },
    },
    { id: 'qrz', badge: { text: 'Online', tone: 'muted' } },
    { id: 'dsp', badge: { text: 'Ready', tone: 'green' } },
  ];

  return (
    <>
      <div className="m-tools-head">
        <span className="m-section-led" />
        <span className="m-tools-title">Tools</span>
        <span className="m-tools-sub">{tools.length} available</span>
      </div>
      <div className="m-tools-grid">
        {tools.map(({ id, badge }) => {
          const meta = TOOL_META[id];
          return (
            <button
              key={id}
              type="button"
              className="m-tool-tile"
              onClick={() => onPick(id)}
              aria-label={`Open ${meta.name}`}
            >
              <div className="m-tool-tile-icon">{meta.icon}</div>
              <div className="m-tool-tile-text">
                <div className="m-tool-tile-name">{meta.name}</div>
                <div className="m-tool-tile-desc">{meta.desc}</div>
              </div>
              <span className={`m-tool-tile-tag m-tool-tile-tag--${badge.tone}`}>{badge.text}</span>
            </button>
          );
        })}
      </div>
      <p className="m-tools-foot">
        Tools are configured on the desktop application.<br />
        Available widgets appear here.
      </p>
    </>
  );
}

function ToolDetail({ tool, onBack }: { tool: MobileToolId; onBack: () => void }) {
  const meta = TOOL_META[tool];
  return (
    <>
      <header className="m-tool-page-head">
        <button
          type="button"
          className="m-back-btn"
          onClick={onBack}
          aria-label="Back to tools"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden>
            <path
              d="M15 18l-6-6 6-6"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </button>
        <span className="m-tool-page-title">{meta.name}</span>
        <span className="m-tool-page-sub">{meta.sub}</span>
      </header>
      {tool === 'tx' && <TxToolView />}
      {tool === 'rotator' && (
        <div className="m-tool-embed m-tool-embed--rotator">
          <RotatorDialPanel />
        </div>
      )}
      {tool === 'qrz' && (
        <div className="m-tool-embed m-tool-embed--qrz">
          <QrzPanel />
        </div>
      )}
      {tool === 'dsp' && (
        <div className="m-tool-embed m-tool-embed--dsp">
          <DspFlexPanel />
        </div>
      )}
    </>
  );
}

// TX tool view — the controls that previously lived under the Settings tab.
// Lifted verbatim into its own component so the Tools router can route to it
// without inflating MobileApp.
function TxToolView() {
  return (
    <Section label="Controls">
      <div className="m-controls">
        <div className="m-control-row m-control-row--1">
          <label className="m-control">
            <span className="m-control-lbl">Step</span>
            <TuningStepWidget />
          </label>
        </div>
        <div className="m-slider-row"><AgcSlider /></div>
        <div className="m-slider-row m-slider-row--lbl"><DriveSlider /></div>
        <div className="m-slider-row m-slider-row--lbl"><TunePowerSlider /></div>
        <div className="m-slider-row m-slider-row--lbl"><MicGainSlider /></div>
        <div className="m-ps-row"><PsToggleButton /></div>
      </div>
    </Section>
  );
}

