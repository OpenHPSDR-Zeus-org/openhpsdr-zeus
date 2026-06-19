// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Analog S-Meter tile — standard tile header (drag handle, title, gear, X),
// animated dial face, gear-flyout config, and footer readout strip. Live
// data comes from live meter stores: RxMetersV2 signal drives the S scale
// when present (legacy 0x14 rxDbm is only a fallback), while fwdWatts drives
// PO and swr drives SWR. The panel auto-flips RX↔TX from moxOn/tunOn — the
// operator never tells the meter which side to read.
//
// The needle is driven by a requestAnimationFrame loop that:
//   1. samples raw RxMetersV2/fwdWatts/swr each frame,
//   2. normalises against the active scale,
//   3. pushes through a moving-average prefilter (cfg.avg samples), then
//   4. through an attack/decay RC ballistic, then
//   5. updates a peak-hold ghost that decays slowly (~5%/s).
//
// We render at rAF rate but only re-render the React tree when the needle
// or peak-hold position changes by ≥ 0.001 of the dial — keeps idle CPU
// flat without making the animation chunky.

import { useEffect, useMemo, useRef, useState } from 'react';
import { useShallow } from 'zustand/react/shallow';
import { GripVertical, Settings, X } from 'lucide-react';
import { usePaStore } from '../../state/pa-store';
import { useRadioStore } from '../../state/radio-store';
import { useTxStore } from '../../state/tx-store';
import { preferredRxSignalDbm } from '../../dsp/rx-chain-health';
import { useRxMetersStore } from '../../state/rx-meters-store';
import { AnalogMeterFace } from './AnalogMeterFace';
import { AnalogMeterConfig } from './AnalogMeterConfig';
import { AnalogMeterZeusOverlay } from './AnalogMeterZeusOverlay';
import { TileLockButton } from '../../layout/TileChrome';
import {
  S_SCALE,
  PO_SCALE,
  SWR_SCALE,
  ballistics,
  dbmToS,
  makeAverager,
  sToDbm,
  type Averager,
  type ScaleId,
} from './analogMeterShared';
import { useAnalogMeterStore } from './analogMeterStore';

type Mode = 'rx' | 'tx';

interface TileHeaderProps {
  configOpen: boolean;
  onGearClick: () => void;
  onClose?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

function TileHeader({
  configOpen,
  onGearClick,
  onClose,
  tileLocked = false,
  workspaceLocked = false,
  onToggleLock,
}: TileHeaderProps) {
  const stopDrag = (e: React.MouseEvent) => e.stopPropagation();
  return (
    <div className="am-header workspace-tile-header">
      <span
        className="workspace-tile-drag-handle"
        aria-hidden="true"
        title={
          tileLocked || workspaceLocked
            ? 'Panel position is locked'
            : 'Drag to reposition'
        }
      >
        <GripVertical size={12} />
      </span>
      <span className="am-status-dot" />
      <span className="workspace-tile-title am-h-title">S-METER</span>

      <button
        type="button"
        className={`am-h-gear ${configOpen ? 'on' : ''}`}
        onClick={onGearClick}
        onMouseDown={stopDrag}
        aria-label="Configure meter"
        aria-pressed={configOpen}
        title="Configure meter"
      >
        <Settings size={14} />
      </button>

      {onToggleLock ? (
        <TileLockButton
          locked={tileLocked}
          workspaceLocked={workspaceLocked}
          onToggleLock={onToggleLock}
        />
      ) : null}

      {onClose && (
        <button
          type="button"
          className="workspace-tile-close"
          onClick={(e) => {
            e.stopPropagation();
            onClose();
          }}
          onPointerDown={(e) => e.stopPropagation()}
          onMouseDown={(e) => e.stopPropagation()}
          aria-label="Remove S-Meter panel"
          title="Remove panel"
        >
          <X size={12} />
        </button>
      )}
    </div>
  );
}

interface ReadoutStripProps {
  enabled: { s: boolean; po: boolean; swr: boolean };
  values: { s: number; po: number; swr: number };
  showDbm: boolean;
  dbm: number;
  swrAlarm: number;
  activeScaleId: ScaleId;
}

function ReadoutStrip({ enabled, values, showDbm, dbm, swrAlarm, activeScaleId }: ReadoutStripProps) {
  const items: { key: ScaleId; label: string; value: string; active: boolean; danger?: boolean }[] = [];
  if (enabled.s) {
    items.push({
      key: 's',
      label: showDbm ? 'S / dBm' : 'S',
      value: showDbm
        ? `${S_SCALE.fmt(values.s)}  ·  ${Math.round(dbm)} dBm`
        : S_SCALE.fmt(values.s),
      active: activeScaleId === 's',
    });
  }
  if (enabled.po) {
    items.push({
      key: 'po',
      label: 'PO',
      value: PO_SCALE.fmt(values.po),
      active: activeScaleId === 'po',
    });
  }
  if (enabled.swr) {
    items.push({
      key: 'swr',
      label: 'SWR',
      value: SWR_SCALE.fmt(values.swr),
      active: activeScaleId === 'swr',
      danger: values.swr >= swrAlarm,
    });
  }

  if (items.length === 0) {
    return (
      <div className="am-readout-strip">
        <div className="am-ro empty">No scales enabled — open settings ⚙</div>
      </div>
    );
  }

  return (
    <div className="am-readout-strip">
      {items.map((it) => (
        <div
          key={it.key}
          className={`am-ro ${it.active ? 'active' : ''} ${it.danger ? 'danger' : ''}`}
          data-scale={it.key}
        >
          <div className="am-ro-label">{it.label}</div>
          <div className="am-ro-value">{it.value}</div>
        </div>
      ))}
    </div>
  );
}

function sampleRxDbm(): number {
  const rx = useRxMetersStore.getState();
  const tx = useTxStore.getState();
  return preferredRxSignalDbm({ ...rx, fallbackDbm: tx.rxDbm }).dbm ?? tx.rxDbm;
}

export interface AnalogMeterPanelProps {
  /** When provided, a close button is rendered in the tile header. The
   *  layout system injects this for headerless panels. */
  onClose?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

export function AnalogMeterPanel({
  onClose,
  tileLocked,
  workspaceLocked,
  onToggleLock,
}: AnalogMeterPanelProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const cfg = useAnalogMeterStore();
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;

  // The radio knows whether it's transmitting; the operator never has to tell
  // the meter which side to read. RX while idle, TX during MOX/TUN.
  const mode: Mode = transmitting ? 'tx' : 'rx';

  const [configOpen, setConfigOpen] = useState(false);

  // Resolve which scale the single physical needle reads — RX prefers S,
  // TX prefers PO if enabled, else SWR. If the operator turned everything
  // off, we still need an `active` to drive the needle math; default to S.
  const enabled = { s: cfg.scaleS, po: cfg.scalePo, swr: cfg.scaleSwr };
  const activeScaleId: ScaleId = useMemo(() => {
    if (mode === 'rx') {
      if (enabled.s) return 's';
      if (enabled.po) return 'po';
      if (enabled.swr) return 'swr';
      return 's';
    }
    if (enabled.po) return 'po';
    if (enabled.swr) return 'swr';
    if (enabled.s) return 's';
    return 'po';
  }, [mode, enabled.s, enabled.po, enabled.swr]);

  // PO arc full-scale tracks the connected radio. Resolution order matches
  // TxStageMeters / ImmersiveMeters so the analog dial reads the same axis as
  // the digital bar: operator PA override → board's published MaxPowerWatts
  // (e.g. HL2 = 10 W) → 100 W last-ditch fallback.
  const paMaxPowerWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const boardMaxWatts = useRadioStore((s) => s.capabilities.maxPowerWatts);
  const poMax = useMemo(() => {
    const ratedW =
      paMaxPowerWatts > 0 ? paMaxPowerWatts : boardMaxWatts > 0 ? boardMaxWatts : 100;
    return Math.max(1, ratedW);
  }, [paMaxPowerWatts, boardMaxWatts]);
  const dynamicPoScale = useMemo(() => {
    // Six evenly-spaced major ticks at 0, max/5, …, max — same shape as the
    // digital PWR axis. HL2 (10 W) → 0/2/4/6/8/10; 100 W rig → 0/20/40/60/80/100.
    const step = poMax / 5;
    const decimals = step >= 1 ? 0 : 1;
    const ticks = Array.from({ length: 6 }, (_, i) => {
      const v = i * step;
      return {
        v,
        label: v.toFixed(decimals),
        major: true,
      };
    });
    return {
      ...PO_SCALE,
      ticks,
      n: (w: number) => Math.min(1, Math.max(0, w) / poMax),
      fmt: (w: number) => `${w < 10 ? w.toFixed(1) : Math.round(w)} W`,
      fromN: (n: number) => Math.max(0, Math.min(1, n)) * poMax,
    };
  }, [poMax]);

  const activeScale = useMemo(() => {
    if (activeScaleId === 's') return S_SCALE;
    if (activeScaleId === 'po') return dynamicPoScale;
    return SWR_SCALE;
  }, [activeScaleId, dynamicPoScale]);

  // Ballistics state. Stored in a ref so the rAF loop can mutate without
  // triggering renders; `tick` is the only state we touch from the loop.
  const stateRef = useRef({
    needleN: 0,
    peakN: 0,
    last: typeof performance !== 'undefined' ? performance.now() : Date.now(),
    rxDbm: -160,
    fwdW: 0,
    swr: 1,
  });
  const avgRef = useRef<Averager>(makeAverager(cfg.avg));
  useEffect(() => {
    avgRef.current.resize(cfg.avg);
  }, [cfg.avg]);

  // Reset peak hold when toggled off.
  useEffect(() => {
    if (!cfg.peakHold) stateRef.current.peakN = 0;
  }, [cfg.peakHold]);

  // [needleN, peakN] published to the face. We update at rAF rate but only
  // call setRender when either crosses a 0.001-of-dial threshold.
  const [render, setRender] = useState({ needleN: 0, peakN: 0 });
  const lastPublishedRef = useRef({ needleN: -1, peakN: -1 });

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    let raf = 0;
    // Visibility gate: stop the rAF loop when the tile is scrolled offscreen
    // or the tab is hidden. Same pattern Panadapter / Waterfall use.
    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;
    // Throttle React publishes to ~30 Hz max even on high-refresh displays
    // (macOS ProMotion runs rAF at 120 Hz). The needle face is the only
    // consumer of `render`; 30 Hz gives a smooth analog feel and stops the
    // panel reconcile from being driven off the system frame rate, which
    // used to drag the always-mounted child subtree along with it.
    const PUBLISH_INTERVAL_MS = 33;
    let lastPublishMs = 0;

    const loop = (now: number) => {
      raf = 0;
      const s = stateRef.current;
      const dt = Math.min(0.1, (now - s.last) / 1000);
      s.last = now;

      // Pull the latest live readings without subscribing — getState() avoids
      // re-rendering the panel on every store change.
      const tx = useTxStore.getState();
      s.rxDbm = sampleRxDbm();
      s.fwdW = tx.fwdWatts;
      s.swr = tx.swr;

      let raw: number;
      if (activeScaleId === 's') {
        raw = dbmToS(s.rxDbm);
      } else if (activeScaleId === 'po') {
        raw = s.fwdW;
      } else {
        raw = s.swr;
      }
      const targetN = Math.max(0, Math.min(1, activeScale.n(raw)));
      const avgedN = avgRef.current.push(targetN);
      s.needleN = ballistics(s.needleN, avgedN, dt, cfg.attack, cfg.decay);

      if (cfg.peakHold) {
        if (s.needleN > s.peakN) s.peakN = s.needleN;
        else s.peakN = Math.max(s.needleN, s.peakN - dt * 0.05);
      } else {
        s.peakN = 0;
      }

      const last = lastPublishedRef.current;
      if (
        now - lastPublishMs >= PUBLISH_INTERVAL_MS &&
        (Math.abs(s.needleN - last.needleN) > 0.001 ||
          Math.abs(s.peakN - last.peakN) > 0.001)
      ) {
        lastPublishMs = now;
        lastPublishedRef.current = { needleN: s.needleN, peakN: s.peakN };
        setRender({ needleN: s.needleN, peakN: s.peakN });
      }

      if (isActive()) raf = requestAnimationFrame(loop);
    };

    const start = () => {
      if (raf === 0 && isActive()) {
        // Reset the dt anchor so the first tick after a wake doesn't account
        // for the gap as elapsed time and yank the ballistic.
        stateRef.current.last = performance.now();
        raf = requestAnimationFrame(loop);
      }
    };

    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) inViewport = e.isIntersecting;
        if (isActive()) start();
      },
      { threshold: 0 },
    );
    io.observe(container);
    const onVisibilityChange = () => {
      pageVisible = !document.hidden;
      if (isActive()) start();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    start();

    return () => {
      if (raf !== 0) cancelAnimationFrame(raf);
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [activeScaleId, activeScale, cfg.attack, cfg.decay, cfg.peakHold]);

  // PO arc tracks rated PA power dynamically; S/SWR are static.
  const scalesForFace = useMemo(
    () => ({ s: S_SCALE, po: dynamicPoScale, swr: SWR_SCALE }),
    [dynamicPoScale],
  );

  // Convert needle position back to scale value for the readout.
  const needleVal = useMemo(
    () => activeScale.fromN(render.needleN),
    [render.needleN, activeScale],
  );

  // Readout values: active scale shows the ballistic-filtered needle reading,
  // others show the raw live values so the footer mirrors the radio's state.
  // Single subscription with a shallow comparator keeps this to one re-render
  // per data tick instead of three (the prior pattern fired three separate
  // subscribers per store update).
  const { rxDbm: fallbackRxDbm, fwdWatts: rawFwdW, swr: rawSwr } = useTxStore(
    useShallow((s) => ({ rxDbm: s.rxDbm, fwdWatts: s.fwdWatts, swr: s.swr })),
  );
  const rxMeters = useRxMetersStore(
    useShallow((s) => ({
      signalPk: s.signalPk,
      signalAv: s.signalAv,
      adcPk: s.adcPk,
      adcAv: s.adcAv,
      agcGain: s.agcGain,
      agcEnvPk: s.agcEnvPk,
      agcEnvAv: s.agcEnvAv,
    })),
  );
  const rawRxDbm =
    preferredRxSignalDbm({ ...rxMeters, fallbackDbm: fallbackRxDbm }).dbm ??
    fallbackRxDbm;
  const readoutValues = {
    s: activeScaleId === 's' ? needleVal : dbmToS(rawRxDbm),
    po: activeScaleId === 'po' ? needleVal : rawFwdW,
    swr: activeScaleId === 'swr' ? needleVal : rawSwr,
  };
  const dbm = sToDbm(readoutValues.s);

  return (
    <div ref={containerRef} className="am-tile" data-mode={mode}>
      <TileHeader
        configOpen={configOpen}
        onGearClick={() => setConfigOpen((o) => !o)}
        onClose={onClose}
        tileLocked={tileLocked}
        workspaceLocked={workspaceLocked}
        onToggleLock={onToggleLock}
      />

      <AnalogMeterConfig open={configOpen} onClose={() => setConfigOpen(false)} />

      <div className="am-face-stack">
        <AnalogMeterFace
          enabledScales={enabled}
          activeScaleId={activeScaleId}
          needleN={render.needleN}
          peakN={cfg.peakHold ? render.peakN : null}
          scales={scalesForFace}
        />
        <AnalogMeterZeusOverlay
          sValue={readoutValues.s}
          active={cfg.zeusMode && enabled.s && activeScaleId === 's'}
        />
      </div>

      <ReadoutStrip
        enabled={enabled}
        values={readoutValues}
        showDbm={cfg.showDbm}
        dbm={dbm}
        swrAlarm={cfg.swrAlarm}
        activeScaleId={activeScaleId}
      />
    </div>
  );
}
