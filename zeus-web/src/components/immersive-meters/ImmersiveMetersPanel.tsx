// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Immersive TX Meters panel — three-section layout from the design
// prototype (Immersive Meters.html):
//   FINAL OUTPUT  : two BigArcs (forward watts: PEP + AVG, axis 0..ratedW)
//   SIGNAL CHAIN  : six VuColumns (MIC PK/AV, LVLR PK/AV, ALC PK/AV)
//   GAIN REDUCTION: two PullDownArcs (LVLR GR, ALC GR)
//
// "Final Output" deviates from the design prototype's dBFS axis. The
// design imagined a pure-audio dBFS meter, but for a ham radio operator
// the meaningful "output" is forward watts on the antenna, not the
// digital modulator amplitude. WDSP's TXA_OUT_PK pegs at full scale on
// every TUNE regardless of drive % (the radio's hardware attenuator
// scales the analog signal, not the digital chain) — so a 1 W TUNE
// looks identical to a 100 W TUNE on a dBFS meter, which is useless.
// Using fwdWatts with a 0..MaxPowerWatts axis makes the gauge show
// what the operator actually cares about.
//
// The tile chrome (drag handle, header, close-X) is supplied by the
// surrounding Zeus PanelTile, so this component renders only the body —
// the three section cards + the footer status strip. ~30 Hz refresh tick
// keeps the peak-hold animation continuous between the 10 Hz wire frames.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { BigArc } from './BigArc';
import { VuColumn } from './VuColumn';
import { PullDownArc } from './PullDownArc';
import { useTxStore } from '../../state/tx-store';
import { useConnectionStore } from '../../state/connection-store';
import { useRadioStore } from '../../state/radio-store';
import { usePaStore } from '../../state/pa-store';
import { subscribe } from '../meters/render/drawBus';

/**
 * Track the per-keydown peak watts (PEP) and a 1-second exponential moving
 * average. Resets when MOX/TUN drops so the next keydown starts clean —
 * matching the legacy TxStageMeters behaviour for PEP latching.
 *
 * AVG uses a fixed time-constant smoother rather than a windowed mean so
 * the needle reads steady on a continuous tone (TUNE) but still tracks
 * voice dynamics on SSB without lagging by a full second.
 */
function useFwdWattsStats(transmitting: boolean): { pep: number; avg: number } {
  const fwdWatts = useTxStore((s) => s.fwdWatts);
  const peakRef = useRef(0);
  const avgRef = useRef(0);
  const lastTsRef = useRef(0);

  // Reset both rails on key-up so the next keydown starts clean.
  if (!transmitting) {
    peakRef.current = 0;
    avgRef.current = 0;
    lastTsRef.current = 0;
  } else if (isFinite(fwdWatts)) {
    if (fwdWatts > peakRef.current) peakRef.current = fwdWatts;
    const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
    if (lastTsRef.current === 0) {
      avgRef.current = fwdWatts;
    } else {
      const dt = Math.max(0, (now - lastTsRef.current) / 1000);
      // 1-second time constant: every step lerps avg toward the live value
      // with weight = 1 - exp(-dt). At 10 Hz frame rate that's ~10 % of
      // the gap closed per tick, smoothly settling within ~1 s.
      const k = 1 - Math.exp(-dt);
      avgRef.current = avgRef.current + (fwdWatts - avgRef.current) * k;
    }
    lastTsRef.current = now;
  }

  return { pep: peakRef.current, avg: avgRef.current };
}

// Re-render tick that keeps the wall-clock peak-hold decay (usePeakHoldFrac,
// recomputed on render) advancing between the 10 Hz wire frames. Now driven
// by the SHARED draw-bus instead of a dedicated rAF — one module-level rAF for
// the whole meter cluster — throttled to ~30 Hz so the panel reconcile stays
// off the system frame rate while each widget's live fill/needle glides at up
// to 60 Hz via useGlidedFraction's imperative writes (no setState in the hot
// path). `hostRef` opts this subscriber into the bus's off-screen gate.
function useBusTick(hostRef: React.RefObject<Element | null>, throttleMs = 33) {
  const [, setTick] = useState(0);
  useEffect(() => {
    let acc = 0;
    const step = (dt: number) => {
      acc += dt * 1000;
      if (acc >= throttleMs) {
        acc = 0;
        setTick((n) => (n + 1) & 0xff);
      }
    };
    return subscribe(step, hostRef.current ?? null);
  }, [hostRef, throttleMs]);
}

interface SectionProps {
  /** Section header text (e.g. "Final Output"). */
  title: string;
  /** Status label class — mirrors the prototype's `.lbl on/.lbl warm` mods.
   *  'on' = blue dot, 'warm' = warn-amber dot, undefined = neutral grey. */
  led?: 'on' | 'warm';
  /** Right-side meta strip — small mono labels separated by '·'. */
  meta: string[];
  children: React.ReactNode;
}

function Section({ title, led, meta, children }: SectionProps) {
  const sectionStyle: CSSProperties = {
    background:
      'linear-gradient(180deg, var(--immersive-panel-2) 0%, var(--immersive-well) 100%)',
    border: '1px solid var(--immersive-line)',
    borderRadius: 8,
    padding: '14px 14px 12px',
    boxShadow:
      'inset 0 1px 0 var(--immersive-rim), inset 0 0 30px rgba(0,0,0,0.25)',
    position: 'relative',
  };
  const headerStyle: CSSProperties = {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: 12,
  };
  const lblStyle: CSSProperties = {
    fontSize: 9.5,
    letterSpacing: '0.20em',
    textTransform: 'uppercase',
    color: 'var(--fg-2)',
    fontWeight: 700,
    display: 'flex',
    alignItems: 'center',
    gap: 9,
  };
  const dotStyle: CSSProperties = {
    width: 5,
    height: 5,
    borderRadius: '50%',
    background:
      led === 'on'
        ? 'var(--immersive-accent)'
        : led === 'warm'
          ? 'var(--immersive-warn)'
          : 'var(--fg-3)',
    boxShadow:
      led === 'on'
        ? '0 0 6px var(--immersive-accent-glow)'
        : led === 'warm'
          ? '0 0 6px var(--immersive-warn-glow)'
          : undefined,
  };
  const metaStyle: CSSProperties = {
    fontFamily: 'var(--font-mono)',
    fontSize: 9.5,
    color: 'var(--fg-3)',
    letterSpacing: '0.06em',
    display: 'flex',
    gap: 10,
    alignItems: 'center',
  };
  const metaKeyStyle: CSSProperties = { color: 'var(--fg-2)' };

  return (
    <section style={sectionStyle}>
      <div style={headerStyle}>
        <div style={lblStyle}>
          <span style={dotStyle} />
          {title}
        </div>
        <div style={metaStyle}>
          {meta.map((m, i) => (
            <span key={i} style={i === 0 ? metaKeyStyle : undefined}>
              {i === 0 ? m : `· ${m}`}
            </span>
          ))}
        </div>
      </div>
      {children}
    </section>
  );
}

/* ─── Footer status strip ──────────────────────────────────────────── */
function StatusFooter({ pepWatts, ratedWatts }: { pepWatts: number; ratedWatts: number }) {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;
  // Ceiling = at-or-past rated PA wattage. The amber LED warns the
  // operator they're sitting at the rail; turning red would imply we
  // tripped a backend interlock (we don't here).
  const ceilingHit = isFinite(pepWatts) && ratedWatts > 0 && pepWatts >= ratedWatts;

  const footerStyle: CSSProperties = {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '10px 4px 2px',
    fontSize: 9.5,
    color: 'var(--fg-3)',
    letterSpacing: '0.14em',
    textTransform: 'uppercase',
    // Push the footer to the bottom of the column-flex body. Any
    // remaining vertical space inside the panel collapses into this
    // top margin, so the sections stay packed at the top while the
    // status strip pins to the panel's bottom edge.
    marginTop: 'auto',
  };
  const grpStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 14 };
  const statStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 6 };
  const ledBaseStyle: CSSProperties = {
    width: 6,
    height: 6,
    borderRadius: '50%',
  };
  const liveLed: CSSProperties = {
    ...ledBaseStyle,
    background: transmitting ? 'var(--immersive-good)' : 'var(--fg-4)',
    boxShadow: transmitting ? '0 0 6px var(--immersive-good-glow)' : undefined,
  };
  const ceilingLed: CSSProperties = {
    ...ledBaseStyle,
    background: ceilingHit ? 'var(--immersive-tx)' : 'var(--immersive-warn)',
    boxShadow: ceilingHit
      ? '0 0 6px var(--immersive-tx-glow)'
      : '0 0 6px var(--immersive-warn-glow)',
  };
  const valueStyle: CSSProperties = {
    fontFamily: 'var(--font-mono)',
    color: 'var(--fg-1)',
    letterSpacing: '0.04em',
    textTransform: 'none',
    fontWeight: 600,
  };

  return (
    <div style={footerStyle}>
      <div style={grpStyle}>
        <div style={statStyle}>
          <span style={liveLed} />
          <span>{transmitting ? 'Live' : 'Idle'}</span>
        </div>
        <div style={statStyle}>
          <span style={valueStyle}>48 kHz</span>
        </div>
      </div>
      <div style={grpStyle}>
        <div style={statStyle}>
          <span style={ceilingLed} />
          <span>{ratedWatts > 0 ? `${Math.round(ratedWatts)} W RATED` : 'PA RATED'}</span>
        </div>
        <div style={statStyle}>
          <span>Hold</span>
          <span style={valueStyle}>1.2 s</span>
        </div>
        <div style={statStyle}>
          <span>Peak</span>
          <span style={valueStyle}>
            {transmitting && isFinite(pepWatts) && pepWatts > 0
              ? `${pepWatts.toFixed(pepWatts < 10 ? 1 : 0)} W`
              : '—'}
          </span>
        </div>
      </div>
    </div>
  );
}

/* ─── Main panel ───────────────────────────────────────────────────── */
export function ImmersiveMetersPanel() {
  const bodyRef = useRef<HTMLDivElement | null>(null);
  // ~30 Hz re-render tick (shared draw-bus) to advance peak-hold decay
  // between the 10 Hz wire frames; the live fills/needles inside each widget
  // glide at up to 60 Hz on the same bus via useGlidedFraction.
  useBusTick(bodyRef, 33);

  const micPk = useTxStore((s) => s.wdspMicPk);
  const micAv = useTxStore((s) => s.micAv);
  const lvlrPk = useTxStore((s) => s.lvlrPk);
  const lvlrAv = useTxStore((s) => s.lvlrAv);
  const lvlrGr = useTxStore((s) => s.lvlrGr);
  const alcPk = useTxStore((s) => s.alcPk);
  const alcAv = useTxStore((s) => s.alcAv);
  const alcGr = useTxStore((s) => s.alcGr);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;
  const mode = useConnectionStore((s) => s.mode);

  // Forward-watts axis top: operator override (PA panel) → board default
  // (caps.maxPowerWatts) → 100 W last-ditch fallback. Same resolution
  // order as the legacy TxStageMeters PWR row.
  const paMaxWatts = usePaStore((s) => s.settings.global.paMaxPowerWatts);
  const boardMaxWatts = useRadioStore((s) => s.capabilities.maxPowerWatts);
  const ratedW = paMaxWatts > 0 ? paMaxWatts : boardMaxWatts > 0 ? boardMaxWatts : 100;
  const { pep } = useFwdWattsStats(transmitting);
  const fwdNow = useTxStore((s) => s.fwdWatts);
  const swr = useTxStore((s) => s.swr);

  // Footer "Peak" reads the per-keydown PEP. Reads 0 (rendered "—") when
  // not transmitting since the per-keydown ref resets on key-up.
  const peakForFooter = transmitting && pep > 0 ? pep : Number.NEGATIVE_INFINITY;

  const bodyStyle: CSSProperties = {
    padding: 14,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
    background: 'var(--immersive-panel)',
    // The surrounding `.workspace-tile-body` is `display: block` with an
    // explicit pixel height (set by RGL via the tile chrome). Take the
    // full height of that container so the dark grey background extends
    // edge-to-edge regardless of how short the content is. `min-height:
    // 100%` on a block parent only works if the parent itself has a
    // resolved height — which `.workspace-tile-body` does (block height
    // from flex sizing of `.workspace-tile`), so this resolves correctly
    // without an intervening flex chain.
    boxSizing: 'border-box',
    minHeight: '100%',
    overflow: 'auto',
  };
  const arcsStyle: CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: 10,
  };
  // Three stage groups (MIC / LEV / ALC) side by side, each holding its own
  // PK + AVG pair under a shared brass-plate header. Grouping (instead of a
  // flat 6-up grid) is what de-busies the row — the eye reads three labelled
  // instruments showing peak + average, not six identical strips.
  const vuClusterStyle: CSSProperties = {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gap: 10,
  };
  const vuStageStyle: CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: 4,
  };
  const vuStageHeaderStyle: CSSProperties = {
    fontSize: 9,
    letterSpacing: '0.18em',
    textTransform: 'uppercase',
    color: 'var(--immersive-lamp-label)',
    fontWeight: 700,
    textAlign: 'center',
    marginBottom: 4,
    paddingBottom: 3,
    borderBottom: '1px solid var(--immersive-line)',
  };
  const grRowStyle: CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: 10,
  };

  return (
    <div ref={bodyRef} style={bodyStyle} aria-label="Immersive TX meters — final output, signal chain, gain reduction">
      <Section
        title="Final Output"
        led="on"
        meta={[`0..${Math.round(ratedW)} W`, mode ?? '—']}
      >
        <div style={arcsStyle}>
          <BigArc
            mode="watts"
            watts={transmitting ? fwdNow : 0}
            maxWatts={ratedW}
            label="Forward Power"
            units="Watts"
            defsId="immersive-arc-fwd"
          />
          <BigArc
            mode="swr"
            ratio={transmitting && isFinite(swr) ? swr : 1.0}
            label="SWR"
            units="Ratio · :1"
            defsId="immersive-arc-swr"
          />
        </div>
      </Section>

      <Section
        title="Signal Chain"
        meta={['PK / AVG', 'HOLD 1.2s', '−60 → +6 dBFS']}
      >
        <div style={vuClusterStyle}>
          <div>
            <div style={vuStageHeaderStyle}>Mic</div>
            <div style={vuStageStyle}>
              <VuColumn valueDb={micPk} name="" sub="PK" variant="pk" hostRef={bodyRef} defsId="immersive-vu-micpk" />
              <VuColumn valueDb={micAv} name="" sub="AVG" variant="avg" hostRef={bodyRef} defsId="immersive-vu-micav" />
            </div>
          </div>
          <div>
            <div style={vuStageHeaderStyle}>Leveler</div>
            <div style={vuStageStyle}>
              <VuColumn valueDb={lvlrPk} name="" sub="PK" variant="pk" hostRef={bodyRef} defsId="immersive-vu-lvlrpk" />
              <VuColumn valueDb={lvlrAv} name="" sub="AVG" variant="avg" hostRef={bodyRef} defsId="immersive-vu-lvlrav" />
            </div>
          </div>
          <div>
            <div style={vuStageHeaderStyle}>ALC</div>
            <div style={vuStageStyle}>
              <VuColumn valueDb={alcPk} name="" sub="PK" variant="pk" hostRef={bodyRef} defsId="immersive-vu-alcpk" />
              <VuColumn valueDb={alcAv} name="" sub="AVG" variant="avg" hostRef={bodyRef} defsId="immersive-vu-alcav" />
            </div>
          </div>
        </div>
      </Section>

      <Section
        title="Gain Reduction"
        led="warm"
        meta={['PULL-DOWN', '0 → −20 dB']}
      >
        <div style={grRowStyle}>
          <PullDownArc
            gainReductionDb={isFinite(lvlrGr) ? Math.max(0, lvlrGr) : 0}
            label="Leveler · GR"
            defsId="immersive-gr-lvlr"
          />
          <PullDownArc
            gainReductionDb={isFinite(alcGr) ? Math.max(0, alcGr) : 0}
            label="ALC · GR"
            defsId="immersive-gr-alc"
          />
        </div>
      </Section>

      <StatusFooter pepWatts={peakForFooter} ratedWatts={ratedW} />
    </div>
  );
}
