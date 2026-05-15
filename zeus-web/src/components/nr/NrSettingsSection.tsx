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
// Inline NR settings section (issue #79). Renders the per-mode tunables for
// NR2 (EMNR post2) and NR4 (SBNR) directly in the DSP layout — the floating
// right-click popover variant proved unreliable to surface on disabled
// buttons across browsers, so settings live as a normal inline panel
// matching Thetis's Setup-form pattern.

import { useCallback, useEffect, useRef, useState } from 'react';
import { create } from 'zustand';
import {
  Activity,
  BarChart3,
  ChevronDown,
  ChevronRight,
  Crosshair,
  Filter,
  Maximize2,
  RotateCcw,
  Target,
  Timer,
  TrendingDown,
  Waves,
  Wind,
  Zap,
} from 'lucide-react';
import {
  GAIN_METHOD_LABELS,
  NPE_METHOD_LABELS,
  NR2_CORE_DEFAULTS,
  NR2_POST2_DEFAULTS,
  NR4_ALGO_LABELS,
  NR4_DEFAULTS,
  setNr2Core,
  setNr2Post2,
  setNr4,
  type Nr2CorePatchBody,
  type Nr2Post2PatchBody,
  type Nr4PatchBody,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import {
  fetchNrUiPrefs,
  updateNrUiPrefs,
  type NrUiPrefsState,
} from '../../api/nrUiPrefs';

export type NrSettingsMode = 'Anr' | 'Emnr' | 'Sbnr';

export type NrSettingsSectionProps = {
  mode: NrSettingsMode;
};

// Per-mode disclosure state lives in a global store, not local component
// state, so the panel survives any FlexLayout re-render that unmounts the
// DSP tab content (drag, dock, tabset reflow). Keyed by mode so each NR
// algorithm's accordion remembers its own open/closed state.
//
// Persisted server-side via /api/nr-ui-prefs (LiteDB) so the choice
// follows the operator across browsers + devices, same pattern as
// bottom-pin / display-settings. Hydration runs once at module load
// (single global store, single fetch); toggles fire a debounced PUT.
type NrSettingsUiState = {
  expanded: Record<NrSettingsMode, boolean>;
  hydrated: boolean;
  /** Set the persisted state from a server fetch (no PUT). */
  hydrate: (next: NrUiPrefsState) => void;
  /** Toggle one mode and schedule a debounced PUT to the backend. */
  toggle: (mode: NrSettingsMode) => void;
};

// Maps the wire DTO's three booleans to / from the per-mode keyed shape
// the component already consumes. NR4 lives under the `Sbnr` key — the
// reading-mode enum the surrounding panel uses.
function fromWire(p: NrUiPrefsState): Record<NrSettingsMode, boolean> {
  return { Anr: p.nr1Expanded, Emnr: p.nr2Expanded, Sbnr: p.nr4Expanded };
}
function toWire(e: Record<NrSettingsMode, boolean>): NrUiPrefsState {
  return { nr1Expanded: e.Anr, nr2Expanded: e.Emnr, nr4Expanded: e.Sbnr };
}

const PERSIST_DEBOUNCE_NR_UI_MS = 150;
let nrUiPersistTimer: ReturnType<typeof setTimeout> | null = null;

function schedulePersist(state: Record<NrSettingsMode, boolean>): void {
  if (nrUiPersistTimer) clearTimeout(nrUiPersistTimer);
  nrUiPersistTimer = setTimeout(() => {
    nrUiPersistTimer = null;
    void updateNrUiPrefs(toWire(state)).catch(() => {
      // Persistence is best-effort — a transient server hiccup leaves the
      // in-memory state intact; the next toggle retries. We don't surface
      // an error toast for a chevron-open preference.
    });
  }, PERSIST_DEBOUNCE_NR_UI_MS);
}

const useNrSettingsUi = create<NrSettingsUiState>((set) => ({
  expanded: { Anr: false, Emnr: false, Sbnr: false },
  hydrated: false,
  hydrate: (next) =>
    set({ expanded: fromWire(next), hydrated: true }),
  toggle: (mode) =>
    set((s) => {
      const expanded = { ...s.expanded, [mode]: !s.expanded[mode] };
      schedulePersist(expanded);
      return { expanded };
    }),
}));

// One-shot module-level hydration so every NrSettingsSection instance
// (the DSP panel renders one per mode) reads from the same already-fetched
// state. Errors are swallowed — a fresh install or an offline backend
// just leaves the defaults (everything collapsed) in place.
let nrUiHydrationStarted = false;
function ensureNrUiHydration(): void {
  if (nrUiHydrationStarted) return;
  nrUiHydrationStarted = true;
  void fetchNrUiPrefs()
    .then((prefs) => useNrSettingsUi.getState().hydrate(prefs))
    .catch(() => {
      // Mark hydrated anyway so the first toggle's PUT doesn't race a
      // late-arriving GET response (which would clobber the user's click).
      useNrSettingsUi.setState({ hydrated: true });
    });
}

export function NrSettingsSection({ mode }: NrSettingsSectionProps) {
  // Persisted disclosure: collapsed by default (the dense gauge panel is
  // too much for the casual operator), but stays open across remounts once
  // the user opens it. The chevron telegraphs the click target. State is
  // sourced from /api/nr-ui-prefs (LiteDB) so the choice follows the
  // operator across browsers + devices.
  useEffect(() => { ensureNrUiHydration(); }, []);
  const expanded = useNrSettingsUi((s) => s.expanded[mode]);
  const toggle = useNrSettingsUi((s) => s.toggle);

  const title =
    mode === 'Anr' ? 'NR1 — ANR' : mode === 'Emnr' ? 'NR2 — EMNR' : 'NR4 — SBNR';

  return (
    <div className="nr-settings" role="region" aria-label={`NR ${mode} settings`}>
      <button
        type="button"
        className="nr-settings__title-btn"
        aria-expanded={expanded}
        onClick={(e) => {
          // Belt-and-braces: stop bubbling so any pointer-down on the
          // ancestor flexlayout tabset can't fire its drag-detection on
          // an accordion-toggle click. (The factory wrapper in
          // FlexWorkspace also catches this, but covering it here keeps
          // the fix robust to changes in the wrapper.)
          e.stopPropagation();
          toggle(mode);
        }}
      >
        <span className="nr-settings__chevron" aria-hidden>
          {expanded ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
        </span>
        <span className="nr-settings__title-text">{title}</span>
      </button>
      {expanded && (
        <>
          {mode === 'Anr' && <AnrPanel />}
          {mode === 'Emnr' && <Nr2Panel />}
          {mode === 'Sbnr' && <Nr4Panel />}
        </>
      )}
    </div>
  );
}

// ---------- NR1 (ANR) — no exposed tunables in this iteration. ----------

function AnrPanel() {
  return (
    <p className="nr-settings__hint">
      NR1 (time-domain LMS) has no operator-tunable knobs in Zeus today.
      Defaults match Thetis: 64 taps, 16-sample delay, gain 1e-4, leakage 0.1.
    </p>
  );
}

// ---------- NR2 (EMNR) post2 comfort-noise tunables. ----------

const PERSIST_DEBOUNCE_MS = 120;

type RowAccent = 'red' | 'green' | 'orange' | 'purple';

function Nr2Panel() {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  // Core algorithm selectors.
  const [gainMethod, setGainMethod] = useState<number>(
    nr.emnrGainMethod ?? NR2_CORE_DEFAULTS.gainMethod,
  );
  const [npeMethod, setNpeMethod] = useState<number>(
    nr.emnrNpeMethod ?? NR2_CORE_DEFAULTS.npeMethod,
  );
  const [aeRun, setAeRun] = useState<boolean>(nr.emnrAeRun ?? NR2_CORE_DEFAULTS.aeRun);
  const [trainT1, setTrainT1] = useState<number>(nr.emnrTrainT1 ?? NR2_CORE_DEFAULTS.trainT1);
  const [trainT2, setTrainT2] = useState<number>(nr.emnrTrainT2 ?? NR2_CORE_DEFAULTS.trainT2);

  // Post-Process (post2 comfort-noise) tunables — pre-existing.
  const [run, setRun] = useState<boolean>(nr.emnrPost2Run ?? NR2_POST2_DEFAULTS.run);
  const [factor, setFactor] = useState<number>(nr.emnrPost2Factor ?? NR2_POST2_DEFAULTS.factor);
  const [nlevel, setNlevel] = useState<number>(nr.emnrPost2Nlevel ?? NR2_POST2_DEFAULTS.nlevel);
  const [rate, setRate] = useState<number>(nr.emnrPost2Rate ?? NR2_POST2_DEFAULTS.rate);
  const [taper, setTaper] = useState<number>(nr.emnrPost2Taper ?? NR2_POST2_DEFAULTS.taper);

  // Two parallel debounce/inflight pipelines — one per endpoint. Keeps the
  // server merges scoped: a Method change can't accidentally push a stale
  // post2 value, and vice versa. Either response carries the full merged
  // state, so applyState reconciles regardless of which lands second.
  const corePending = useRef<AbortController | null>(null);
  const coreDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);
  const post2Pending = useRef<AbortController | null>(null);
  const post2Debounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      corePending.current?.abort();
      post2Pending.current?.abort();
      if (coreDebounce.current != null) clearTimeout(coreDebounce.current);
      if (post2Debounce.current != null) clearTimeout(post2Debounce.current);
    },
    [],
  );

  const persistCore = useCallback(
    (body: Nr2CorePatchBody) => {
      if (coreDebounce.current != null) clearTimeout(coreDebounce.current);
      coreDebounce.current = setTimeout(() => {
        corePending.current?.abort();
        const ac = new AbortController();
        corePending.current = ac;
        setNr2Core(body, ac.signal)
          .then((s: RadioStateDto) => {
            if (!ac.signal.aborted) applyState(s);
          })
          .catch(() => {
            /* state poll will reconcile */
          });
      }, PERSIST_DEBOUNCE_MS);
    },
    [applyState],
  );

  const persistPost2 = useCallback(
    (body: Nr2Post2PatchBody) => {
      if (post2Debounce.current != null) clearTimeout(post2Debounce.current);
      post2Debounce.current = setTimeout(() => {
        post2Pending.current?.abort();
        const ac = new AbortController();
        post2Pending.current = ac;
        setNr2Post2(body, ac.signal)
          .then((s: RadioStateDto) => {
            if (!ac.signal.aborted) applyState(s);
          })
          .catch(() => {
            /* state poll will reconcile */
          });
      }, PERSIST_DEBOUNCE_MS);
    },
    [applyState],
  );

  // Method handlers.
  const onGainMethodChange = (v: number) => {
    setGainMethod(v);
    persistCore({ gainMethod: v });
  };
  const onNpeMethodChange = (v: number) => {
    setNpeMethod(v);
    persistCore({ npeMethod: v });
  };
  const onAeRunChange = (v: boolean) => {
    setAeRun(v);
    persistCore({ aeRun: v });
  };
  const onTrainT1Change = (v: number) => {
    setTrainT1(v);
    persistCore({ trainT1: v });
  };
  const onTrainT2Change = (v: number) => {
    setTrainT2(v);
    persistCore({ trainT2: v });
  };

  // Post-Process handlers.
  const onRunChange = (v: boolean) => {
    setRun(v);
    persistPost2({ post2Run: v });
  };
  const onFactorChange = (v: number) => {
    setFactor(v);
    persistPost2({ post2Factor: v });
  };
  const onNlevelChange = (v: number) => {
    setNlevel(v);
    persistPost2({ post2Nlevel: v });
  };
  const onRateChange = (v: number) => {
    setRate(v);
    persistPost2({ post2Rate: v });
  };
  const onTaperChange = (v: number) => {
    const r = Math.round(v);
    setTaper(r);
    persistPost2({ post2Taper: r });
  };

  // Resets BOTH groups to Thetis-parity factory state. Two endpoints fire;
  // each response reconciles independently via applyState (last write wins,
  // and both servers' merged states agree on the reset values).
  const resetDefaults = () => {
    setGainMethod(NR2_CORE_DEFAULTS.gainMethod);
    setNpeMethod(NR2_CORE_DEFAULTS.npeMethod);
    setAeRun(NR2_CORE_DEFAULTS.aeRun);
    setTrainT1(NR2_CORE_DEFAULTS.trainT1);
    setTrainT2(NR2_CORE_DEFAULTS.trainT2);
    setRun(NR2_POST2_DEFAULTS.run);
    setFactor(NR2_POST2_DEFAULTS.factor);
    setNlevel(NR2_POST2_DEFAULTS.nlevel);
    setRate(NR2_POST2_DEFAULTS.rate);
    setTaper(NR2_POST2_DEFAULTS.taper);
    persistCore({
      gainMethod: NR2_CORE_DEFAULTS.gainMethod,
      npeMethod: NR2_CORE_DEFAULTS.npeMethod,
      aeRun: NR2_CORE_DEFAULTS.aeRun,
      trainT1: NR2_CORE_DEFAULTS.trainT1,
      trainT2: NR2_CORE_DEFAULTS.trainT2,
    });
    persistPost2({
      post2Run: NR2_POST2_DEFAULTS.run,
      post2Factor: NR2_POST2_DEFAULTS.factor,
      post2Nlevel: NR2_POST2_DEFAULTS.nlevel,
      post2Rate: NR2_POST2_DEFAULTS.rate,
      post2Taper: NR2_POST2_DEFAULTS.taper,
    });
  };

  return (
    <div>
      {/* ---- METHOD ----------------------------------------------------- */}
      <h4 className="nr-settings__subhdr">Method</h4>

      <div
        className="nr-settings__row"
        title="Gain calculation method. Trained uses the zetaHat lookup table baked into libwdsp (zetaHat.bin / calculus). Defaults to Gamma."
      >
        <span className="nr-settings__label">Gain</span>
        <div className="btn-row" role="radiogroup" aria-label="Gain Method">
          {GAIN_METHOD_LABELS.map((lbl, i) => (
            <button
              key={lbl}
              type="button"
              role="radio"
              aria-checked={gainMethod === i}
              className={`btn sm ${gainMethod === i ? 'active' : ''}`}
              onClick={() => onGainMethodChange(i)}
            >
              {lbl}
            </button>
          ))}
        </div>
      </div>

      <div
        className="nr-settings__row"
        title="Noise Power Estimation method. OSMS is Warren Pratt's tuned default; MMSE/NSTAT are alternates."
      >
        <span className="nr-settings__label">NPE</span>
        <div className="btn-row" role="radiogroup" aria-label="NPE Method">
          {NPE_METHOD_LABELS.map((lbl, i) => (
            <button
              key={lbl}
              type="button"
              role="radio"
              aria-checked={npeMethod === i}
              className={`btn sm ${npeMethod === i ? 'active' : ''}`}
              onClick={() => onNpeMethodChange(i)}
            >
              {lbl}
            </button>
          ))}
        </div>
      </div>

      <div
        className="nr-settings__toggle-row"
        title="Artifact Eliminator — smooths the spectral mask to suppress the musical-noise warble typical of frequency-domain NR. Default ON (Thetis parity)."
      >
        <label className="nr-settings__label" htmlFor="nr2-ae">AE Filter</label>
        <Switch id="nr2-ae" checked={aeRun} onChange={onAeRunChange} />
      </div>

      {/* ---- TRAINED (only when Gain Method == Trained) ---------------- */}
      {gainMethod === 3 && (
        <>
          <h4 className="nr-settings__subhdr">Trained</h4>
          <GaugeRow
            accent="red"
            icon={<Target size={14} strokeWidth={2.25} />}
            label="T1"
            value={trainT1}
            min={-5}
            max={5}
            step={0.1}
            decimals={1}
            onChange={onTrainT1Change}
          />
          <GaugeRow
            accent="green"
            icon={<Filter size={14} strokeWidth={2.25} />}
            label="T2"
            value={trainT2}
            min={0.02}
            max={3.5}
            step={0.01}
            decimals={2}
            onChange={onTrainT2Change}
          />
        </>
      )}

      {/* ---- POST-PROCESS ---------------------------------------------- */}
      <h4 className="nr-settings__subhdr">Post-Process</h4>

      <div
        className="nr-settings__toggle-row"
        title="EMNR's post-stage comfort-noise injection (post2). Off = raw EMNR output. The NR cycle button is the master on/off; this is a sub-stage of NR2 only."
      >
        <label className="nr-settings__label" htmlFor="nr2-run">Enable</label>
        <Switch id="nr2-run" checked={run} onChange={onRunChange} />
      </div>

      <GaugeRow
        accent="red"
        icon={<Waves size={14} strokeWidth={2.25} />}
        label="Factor"
        value={factor}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onFactorChange}
      />
      <GaugeRow
        accent="green"
        icon={<Activity size={14} strokeWidth={2.25} />}
        label="Nlevel"
        value={nlevel}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onNlevelChange}
      />
      <GaugeRow
        accent="orange"
        icon={<Timer size={14} strokeWidth={2.25} />}
        label="Rate"
        value={rate}
        min={0}
        max={20}
        step={0.1}
        decimals={1}
        onChange={onRateChange}
      />
      <GaugeRow
        accent="purple"
        icon={<BarChart3 size={14} strokeWidth={2.25} />}
        label="Taper (bins)"
        value={taper}
        min={0}
        max={32}
        step={1}
        decimals={0}
        onChange={onTaperChange}
      />

      <p className="nr-settings__hint">
        Method defaults: Gamma / OSMS / AE on (Thetis parity). Trained T1/T2 only
        consulted when Gain Method = Trained. Post-Process defaults: factor 15,
        nlevel 15, rate 5.0, taper 12. See emnr.c:981–1056.
      </p>

      <div className="nr-settings__buttons">
        <button
          type="button"
          className="nr-settings__button nr-settings__button--primary"
          onClick={resetDefaults}
          title="Reset Method + Post-Process to factory defaults"
        >
          <RotateCcw size={12} strokeWidth={2.5} />
          <span>Defaults</span>
        </button>
      </div>
    </div>
  );
}

// ---------- NR4 (SBNR / libspecbleach) tunables. -----------------------

function Nr4Panel() {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  const [reduction, setReduction] = useState<number>(
    nr.nr4ReductionAmount ?? NR4_DEFAULTS.reductionAmount,
  );
  const [smoothing, setSmoothing] = useState<number>(
    nr.nr4SmoothingFactor ?? NR4_DEFAULTS.smoothingFactor,
  );
  const [whitening, setWhitening] = useState<number>(
    nr.nr4WhiteningFactor ?? NR4_DEFAULTS.whiteningFactor,
  );
  const [rescale, setRescale] = useState<number>(
    nr.nr4NoiseRescale ?? NR4_DEFAULTS.noiseRescale,
  );
  const [snrThr, setSnrThr] = useState<number>(
    nr.nr4PostFilterThreshold ?? NR4_DEFAULTS.postFilterThreshold,
  );
  const [algo, setAlgo] = useState<number>(
    nr.nr4NoiseScalingType ?? NR4_DEFAULTS.noiseScalingType,
  );

  const inflight = useRef<AbortController | null>(null);
  const debounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      inflight.current?.abort();
      if (debounce.current != null) clearTimeout(debounce.current);
    },
    [],
  );

  const persist = useCallback(
    (body: Nr4PatchBody) => {
      if (debounce.current != null) clearTimeout(debounce.current);
      debounce.current = setTimeout(() => {
        inflight.current?.abort();
        const ac = new AbortController();
        inflight.current = ac;
        setNr4(body, ac.signal)
          .then((s: RadioStateDto) => {
            if (!ac.signal.aborted) applyState(s);
          })
          .catch(() => {
            /* state poll will reconcile */
          });
      }, PERSIST_DEBOUNCE_MS);
    },
    [applyState],
  );

  const onAlgoChange = (v: number) => {
    setAlgo(v);
    persist({ noiseScalingType: v });
  };
  const onReductionChange = (v: number) => {
    setReduction(v);
    persist({ reductionAmount: v });
  };
  const onSmoothingChange = (v: number) => {
    setSmoothing(v);
    persist({ smoothingFactor: v });
  };
  const onWhiteningChange = (v: number) => {
    setWhitening(v);
    persist({ whiteningFactor: v });
  };
  const onRescaleChange = (v: number) => {
    setRescale(v);
    persist({ noiseRescale: v });
  };
  const onSnrThrChange = (v: number) => {
    setSnrThr(v);
    persist({ postFilterThreshold: v });
  };

  const resetDefaults = () => {
    setAlgo(NR4_DEFAULTS.noiseScalingType);
    setReduction(NR4_DEFAULTS.reductionAmount);
    setSmoothing(NR4_DEFAULTS.smoothingFactor);
    setWhitening(NR4_DEFAULTS.whiteningFactor);
    setRescale(NR4_DEFAULTS.noiseRescale);
    setSnrThr(NR4_DEFAULTS.postFilterThreshold);
    persist({
      noiseScalingType: NR4_DEFAULTS.noiseScalingType,
      reductionAmount: NR4_DEFAULTS.reductionAmount,
      smoothingFactor: NR4_DEFAULTS.smoothingFactor,
      whiteningFactor: NR4_DEFAULTS.whiteningFactor,
      noiseRescale: NR4_DEFAULTS.noiseRescale,
      postFilterThreshold: NR4_DEFAULTS.postFilterThreshold,
    });
  };

  return (
    <div>
      {/* ---- METHOD ----------------------------------------------------- */}
      <h4 className="nr-settings__subhdr">Method</h4>

      <div
        className="nr-settings__row"
        title="Noise scaling algorithm — 1: a-posteriori SNR scaling using the complete spectrum, 2: a-posteriori using critical bands, 3: masking thresholds."
      >
        <span className="nr-settings__label">Algo</span>
        <div className="btn-row" role="radiogroup" aria-label="Noise Scaling Algorithm">
          {NR4_ALGO_LABELS.map((lbl, i) => (
            <button
              key={lbl}
              type="button"
              role="radio"
              aria-checked={algo === i}
              className={`btn sm ${algo === i ? 'active' : ''}`}
              onClick={() => onAlgoChange(i)}
            >
              {lbl}
            </button>
          ))}
        </div>
      </div>

      {/* ---- TUNABLES --------------------------------------------------- */}
      <h4 className="nr-settings__subhdr">Tunables</h4>

      <GaugeRow
        accent="red"
        icon={<TrendingDown size={14} strokeWidth={2.25} />}
        label="Reduction"
        value={reduction}
        min={0}
        max={20}
        step={1}
        decimals={1}
        onChange={onReductionChange}
      />
      <GaugeRow
        accent="green"
        icon={<Wind size={14} strokeWidth={2.25} />}
        label="Smoothing"
        value={smoothing}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onSmoothingChange}
      />
      <GaugeRow
        accent="orange"
        icon={<Zap size={14} strokeWidth={2.25} />}
        label="Whitening"
        value={whitening}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onWhiteningChange}
      />
      <GaugeRow
        accent="purple"
        icon={<Maximize2 size={14} strokeWidth={2.25} />}
        label="Rescale"
        value={rescale}
        min={0}
        max={12}
        step={1}
        decimals={1}
        onChange={onRescaleChange}
      />
      <GaugeRow
        accent="red"
        icon={<Crosshair size={14} strokeWidth={2.25} />}
        label="SNRthresh"
        value={snrThr}
        min={-10}
        max={10}
        step={0.5}
        decimals={1}
        onChange={onSnrThrChange}
      />

      <p className="nr-settings__hint">
        libspecbleach spectral bleaching. Defaults: Algo 1, reduction 10, others
        0/2, SNRthresh -10. See native/wdsp/sbnr.c. Position is held at 1
        (post-AGC) — Thetis exposes it globally with ANF/ANR; not surfaced here.
      </p>

      <div className="nr-settings__buttons">
        <button
          type="button"
          className="nr-settings__button nr-settings__button--primary"
          onClick={resetDefaults}
          title="Reset Method + Tunables to factory defaults"
        >
          <RotateCcw size={12} strokeWidth={2.5} />
          <span>Defaults</span>
        </button>
      </div>
    </div>
  );
}

// ---------- Gauge row ---------------------------------------------------

type GaugeRowProps = {
  accent: RowAccent;
  icon: React.ReactNode;
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  decimals: number;
  onChange: (v: number) => void;
};

function GaugeRow({
  accent,
  icon,
  label,
  value,
  min,
  max,
  step,
  decimals,
  onChange,
}: GaugeRowProps) {
  const span = max - min || 1;
  const norm = Math.max(0, Math.min(1, (value - min) / span));
  const display = decimals === 0 ? String(Math.round(value)) : value.toFixed(decimals);

  const handleNorm = (n: number) => {
    const raw = min + n * span;
    const snapped = step > 0 ? Math.round(raw / step) * step : raw;
    const clamped = Math.max(min, Math.min(max, snapped));
    // Tame float noise (e.g. 0.150000000002).
    const out = decimals > 0 ? Number(clamped.toFixed(decimals)) : clamped;
    if (out !== value) onChange(out);
  };

  return (
    <div className={`nr-row nr-row--${accent}`}>
      <span className="nr-row__icon" aria-hidden>{icon}</span>
      <span className="nr-row__label">{label}</span>
      <Gauge norm={norm} accent={accent} onNormChange={handleNorm} />
      <span
        className="nr-row__value"
        role="status"
        aria-label={`${label} ${display}`}
      >
        {display}
      </span>
      <Bars norm={norm} accent={accent} />
    </div>
  );
}

// Mini circular gauge — 270° arc with a colored needle. Drag-to-set:
// pointer position relative to the gauge centre maps to the angle on the
// 135°→405° arc, then back to a normalised value. The dead-zone at the
// bottom (between 405° and 135° going through 90°) snaps to whichever
// end is nearer.
function Gauge({
  norm,
  accent,
  onNormChange,
}: {
  norm: number;
  accent: RowAccent;
  onNormChange: (n: number) => void;
}) {
  const startDeg = 135;
  const sweepDeg = 270;
  const angle = startDeg + sweepDeg * norm;
  const size = 34;
  const cx = size / 2;
  const cy = size / 2;
  const r = size / 2 - 4;
  const rad = (angle * Math.PI) / 180;
  const tipX = cx + Math.cos(rad) * (r - 1.5);
  const tipY = cy + Math.sin(rad) * (r - 1.5);

  const arc = describeArc(cx, cy, r, startDeg, startDeg + sweepDeg);
  const arcLive = describeArc(cx, cy, r, startDeg, angle);

  const setFromPoint = (clientX: number, clientY: number, rect: DOMRect) => {
    const dx = clientX - (rect.left + rect.width / 2);
    const dy = clientY - (rect.top + rect.height / 2);
    let theta = (Math.atan2(dy, dx) * 180) / Math.PI; // [-180, 180]
    if (theta < 0) theta += 360;                       // [0, 360]
    let shifted = (theta - startDeg + 360) % 360;      // [0, 360)
    if (shifted > sweepDeg) {
      // In the bottom dead-zone — snap to nearest end.
      shifted = shifted - sweepDeg < 360 - shifted ? sweepDeg : 0;
    }
    onNormChange(shifted / sweepDeg);
  };

  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    e.preventDefault();
    const target = e.currentTarget;
    const rect = target.getBoundingClientRect();
    target.setPointerCapture(e.pointerId);
    setFromPoint(e.clientX, e.clientY, rect);

    const onMove = (ev: PointerEvent) => {
      setFromPoint(ev.clientX, ev.clientY, rect);
    };
    const onUp = (ev: PointerEvent) => {
      try { target.releasePointerCapture(ev.pointerId); } catch { /* released already */ }
      target.removeEventListener('pointermove', onMove);
      target.removeEventListener('pointerup', onUp);
      target.removeEventListener('pointercancel', onUp);
    };
    target.addEventListener('pointermove', onMove);
    target.addEventListener('pointerup', onUp);
    target.addEventListener('pointercancel', onUp);
  };

  return (
    <svg
      className="nr-row__gauge"
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      role="slider"
      aria-valuemin={0}
      aria-valuemax={1}
      aria-valuenow={Number(norm.toFixed(3))}
      tabIndex={0}
      onPointerDown={onPointerDown}
    >
      <path d={arc} className="nr-gauge__track" />
      <path d={arcLive} className={`nr-gauge__live nr-gauge__live--${accent}`} />
      <line
        x1={cx}
        y1={cy}
        x2={tipX}
        y2={tipY}
        className={`nr-gauge__needle nr-gauge__needle--${accent}`}
      />
      <circle cx={cx} cy={cy} r={1.8} className="nr-gauge__hub" />
    </svg>
  );
}

// Cell-bars style indicator — 4 bars whose count lit follows norm.
function Bars({ norm, accent }: { norm: number; accent: RowAccent }) {
  const lit = Math.max(0, Math.min(4, Math.ceil(norm * 4)));
  return (
    <svg
      className="nr-row__bars"
      width={26}
      height={18}
      viewBox="0 0 26 18"
      aria-hidden
    >
      {[0, 1, 2, 3].map((i) => {
        const h = 4 + i * 4;
        const x = i * 6.5;
        const y = 18 - h;
        const on = i < lit;
        return (
          <rect
            key={i}
            x={x}
            y={y}
            width={4}
            height={h}
            rx={0.6}
            className={
              on
                ? `nr-bars__bar nr-bars__bar--on nr-bars__bar--${accent}`
                : 'nr-bars__bar'
            }
          />
        );
      })}
    </svg>
  );
}

// Standard SVG arc-path helper (degrees, clockwise).
function describeArc(cx: number, cy: number, r: number, a0: number, a1: number): string {
  const p0 = polar(cx, cy, r, a0);
  const p1 = polar(cx, cy, r, a1);
  const large = a1 - a0 > 180 ? 1 : 0;
  return `M ${p0.x.toFixed(2)} ${p0.y.toFixed(2)} A ${r} ${r} 0 ${large} 1 ${p1.x.toFixed(2)} ${p1.y.toFixed(2)}`;
}
function polar(cx: number, cy: number, r: number, deg: number): { x: number; y: number } {
  const rad = (deg * Math.PI) / 180;
  return { x: cx + Math.cos(rad) * r, y: cy + Math.sin(rad) * r };
}

// ---------- iOS-style toggle switch ------------------------------------

function Switch({
  id,
  checked,
  onChange,
}: {
  id: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked}
      className={`nr-switch ${checked ? 'is-on' : ''}`}
      onClick={() => onChange(!checked)}
    >
      <span className="nr-switch__thumb" />
    </button>
  );
}
