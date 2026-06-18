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
//
// ── PREMIUM MACHINED-INSTRUMENT VFO ──────────────────────────────────────
// The frequency readout is rendered as a real machined instrument using the
// SAME five-layer render kit as the loved S-meter (components/meters/render/),
// not a flat glow card:
//   (1) recessed well behind the digits (recessedWell)         — the pocket,
//   (2) a top-sheen band over the well (CSS pseudo-element)     — cylindrical depth,
//   (3) GlassDome SVG over the readout                          — wet-glass specular,
//   (4) GaugeBezel rect ring around the readout                — machined chrome frame,
//   (5) lit/dim per-decade digits (two-tone lamp)              — the hero.
// All token-driven (--vfo-* aliases of the --meter-*/--immersive-lamp-* kit)
// and GPU-cheap: only the existing compositor-only .meter-sheen-drift sheen
// animates (reduced-motion already drops it). DISPLAY-ONLY — no value pipeline
// is touched here.
//
// The two SVG overlays are pointer-events:none so they never block the digit
// click / wheel handlers underneath.

import {
  Fragment,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { fetchState, setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useVfoLockStore } from '../state/vfo-lock-store';
import { GlassDome } from './meters/render/GlassDome';
import { GaugeBezel } from './meters/render/GaugeBezel';
import { recessedWell } from './meters/render/recessedWell';

const MAX_HZ = 60_000_000;
const STATE_POLL_MS = 2000;

type DigitPlace = {
  decade: number;
  separatorAfter?: '.' | null;
};

const DIGIT_PLACES: readonly DigitPlace[] = [
  { decade: 10_000_000 },
  { decade: 1_000_000, separatorAfter: '.' },
  { decade: 100_000 },
  { decade: 10_000 },
  { decade: 1_000, separatorAfter: '.' },
  { decade: 100 },
  { decade: 10 },
  { decade: 1 },
];

function clampHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, Math.trunc(hz)));
}

function digitAt(hz: number, decade: number): number {
  return Math.floor((hz / decade) % 10);
}

// User types kHz. Accept plain "14200", decimal "14200.5", leading/trailing
// whitespace, comma as decimal for EU keyboards. Reject anything else.
function parseKhzInput(raw: string): number | null {
  const cleaned = raw.trim().replace(',', '.');
  if (!cleaned) return null;
  if (!/^\d+(\.\d+)?$/.test(cleaned)) return null;
  const khz = Number(cleaned);
  if (!Number.isFinite(khz)) return null;
  return clampHz(Math.round(khz * 1000));
}

function formatKhz(hz: number): string {
  return (hz / 1000).toFixed(3);
}

// Per-digit wheel tuning debounce. Wheel events fire at ~60 Hz during a spin;
// we update the store (and therefore the display) on every tick for instant
// feedback, but only POST the last resting value to avoid flooding /api/vfo.
const WHEEL_DEBOUNCE_MS = 80;

// ── lock padlock glyph ───────────────────────────────────────────────────
// Identical open/closed inline-SVG padlock to the mobile shell's
// VfoLockButton (mobile/MobileApp.tsx) so mobile + desktop read the same. Both
// drive the one shared vfo-lock-store, so the lock state is unified.
function LockGlyph({ locked }: { locked: boolean }) {
  return locked ? (
    <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden>
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
    <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden>
      <rect x="5" y="11" width="14" height="9" rx="1.5" fill="currentColor" />
      <path
        d="M8 11V8a4 4 0 0 1 7.5-2"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
      />
    </svg>
  );
}

// Desktop VFO-lock toggle — pins the dial so no path (digit click/type/scroll,
// keyboard, pan/ruler, panadapter click-to-tune, band retune) can change the
// frequency until unlocked. Bound to the same shared store as the mobile
// padlock; the gate itself lives in api/client.setVfo + setRadioLo and the
// per-path guards.
function VfoLockButton() {
  const locked = useVfoLockStore((s) => s.locked);
  const toggle = useVfoLockStore((s) => s.toggle);
  return (
    <button
      type="button"
      onClick={toggle}
      aria-pressed={locked}
      aria-label={locked ? 'VFO locked — click to unlock' : 'VFO unlocked — click to lock'}
      title={locked ? 'VFO locked — click to unlock' : 'Lock VFO (no path can retune while locked)'}
      className={`vfo-lock-btn ${locked ? 'on' : ''}`}
    >
      <LockGlyph locked={locked} />
      <span className="vfo-lock-lbl">{locked ? 'LOCKED' : 'LOCK'}</span>
    </button>
  );
}

type VfoDisplayProps = {
  /** Header label. Defaults to "VFO A". (Prop preserved from Christiano's
   *  fork superset so a future RX2 readout inherits the premium look for
   *  free; VFO-B store plumbing is not present in this tree yet.) */
  label?: string;
  /** Compact variant — scales the bezel / well / digit gap down for a dense
   *  RX2 / narrow placement while keeping the full machined-instrument stack. */
  compact?: boolean;
};

export function VfoDisplay({ label = 'VFO A', compact = false }: VfoDisplayProps = {}) {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const applyState = useConnectionStore((s) => s.applyState);
  const locked = useVfoLockStore((s) => s.locked);

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const inputRef = useRef<HTMLInputElement | null>(null);
  const digitsContainerRef = useRef<HTMLButtonElement | null>(null);

  const wheelTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const wheelPending = useRef<number | null>(null);
  const wheelInflight = useRef<AbortController | null>(null);

  useEffect(() => () => {
    wheelInflight.current?.abort();
    if (wheelTimer.current != null) clearTimeout(wheelTimer.current);
  }, []);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const tick = async () => {
      if (!cancelled && !editing) {
        try {
          const next = await fetchState();
          if (!cancelled && !editing) applyState(next);
        } catch {
          /* swallow — retry next tick */
        }
      }
      if (!cancelled) timer = setTimeout(tick, STATE_POLL_MS);
    };
    tick();
    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
    };
  }, [applyState, editing]);

  const beginEdit = useCallback(() => {
    // VFO locked — block edit entry entirely; the typed-entry field never opens.
    if (useVfoLockStore.getState().locked) return;
    // Start EMPTY so the operator types the whole frequency fresh (no pre-filled
    // value or decimals to edit around) — matches the old VFO entry feel.
    setDraft('');
    setEditing(true);
  }, []);

  const cancelEdit = useCallback(() => {
    setEditing(false);
    setDraft('');
  }, []);

  const commitEdit = useCallback(() => {
    // VFO locked — discard the typed entry (defensive; beginEdit already
    // refuses to open the field while locked).
    if (useVfoLockStore.getState().locked) {
      setEditing(false);
      setDraft('');
      return;
    }
    const next = parseKhzInput(draft);
    setEditing(false);
    setDraft('');
    if (next == null || next === vfoHz) return;
    useConnectionStore.setState({ vfoHz: next });
    setVfo(next)
      .then(applyState)
      .catch(() => {
        /* next poll will reconcile */
      });
  }, [draft, vfoHz, applyState]);

  useLayoutEffect(() => {
    if (editing && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [editing]);

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        commitEdit();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        cancelEdit();
      }
    },
    [commitEdit, cancelEdit],
  );

  // Per-digit wheel tuning: hover over a digit, scroll wheel to step that
  // digit's decade. Wheel up = freq up. Updates local store immediately so
  // the display tracks the wheel, POSTs the final resting value after the
  // user stops scrolling.
  //
  // Attached as a NATIVE listener via addEventListener with { passive: false }
  // rather than a React `onWheel` JSX prop. React 17+ delegates wheel events
  // through a root-level passive listener, which means `e.preventDefault()`
  // inside a synthetic onWheel handler is silently ignored — letting the
  // ancestor `.freq-panel` (overflow:auto, see layout/panels/VfoPanel.tsx) and
  // any other scrollable parent perform their default scroll. Compare the
  // canonical pattern at `util/use-pan-tune-gesture.ts:307` (panadapter zoom).
  // Event-delegated on the digits container: wheel over a `[data-decade]`
  // span is consumed; wheel over a separator or padding is left alone so the
  // outer page can still scroll naturally.
  useEffect(() => {
    const el = digitsContainerRef.current;
    if (!el || editing) return;
    const handler = (e: WheelEvent) => {
      // VFO locked — wheel-step tuning no-ops. We still preventDefault on a
      // digit so the page doesn't scroll under a locked digit, matching the
      // unlocked feel; we just don't move the dial.
      const lockedNow = useVfoLockStore.getState().locked;
      const target = e.target as Element | null;
      const digit = target?.closest<HTMLElement>('[data-decade]');
      if (!digit || !el.contains(digit)) return;
      const decadeAttr = digit.dataset.decade;
      if (!decadeAttr) return;
      const decade = Number.parseInt(decadeAttr, 10);
      if (!Number.isFinite(decade) || decade <= 0) return;
      e.preventDefault();
      if (lockedNow) return;

      const direction = e.deltaY < 0 ? 1 : -1;
      const current = useConnectionStore.getState().vfoHz;
      const next = clampHz(current + direction * decade);
      if (next === current) return;
      useConnectionStore.setState({ vfoHz: next });
      wheelPending.current = next;

      if (wheelTimer.current != null) clearTimeout(wheelTimer.current);
      wheelTimer.current = setTimeout(() => {
        wheelTimer.current = null;
        const pending = wheelPending.current;
        wheelPending.current = null;
        if (pending == null) return;
        wheelInflight.current?.abort();
        const ac = new AbortController();
        wheelInflight.current = ac;
        setVfo(pending, ac.signal)
          .then((reply) => {
            if (ac.signal.aborted) return;
            applyState(reply);
          })
          .catch((err) => {
            if (ac.signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            /* next state poll will reconcile */
          });
      }, WHEEL_DEBOUNCE_MS);
    };
    // passive:false so preventDefault() actually stops the ancestor scroll.
    el.addEventListener('wheel', handler, { passive: false });
    return () => el.removeEventListener('wheel', handler);
  }, [applyState, editing]);

  const digits = useMemo(() => DIGIT_PLACES, []);

  // Track the rendered readout-face size so the SVG overlays (GlassDome +
  // GaugeBezel) use a viewBox matched to the pixel rect — keeps the bezel rx /
  // thickness proportional and the dome ellipse centred regardless of the
  // container-query font clamp. nonScaling strokes keep the chrome crisp under
  // the preserveAspectRatio="none" stretch.
  const readoutRef = useRef<HTMLDivElement | null>(null);
  const [readoutSize, setReadoutSize] = useState({ w: 320, h: 96 });
  useEffect(() => {
    const el = readoutRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver((entries) => {
      const r = entries[0]?.contentRect;
      if (!r) return;
      const w = Math.max(8, Math.round(r.width));
      const h = Math.max(8, Math.round(r.height));
      setReadoutSize((prev) => (prev.w === w && prev.h === h ? prev : { w, h }));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const { w: vbW, h: vbH } = readoutSize;
  const bezelRx = compact ? 5 : 7;
  const bezelThick = compact ? 2.4 : 3.2;

  return (
    <div
      className={`freq-display${compact ? ' compact' : ''}${locked ? ' locked' : ''}`}
    >
      <div className="freq-head">
        <span className="label-xs freq-head-lbl">{label}</span>
        <VfoLockButton />
      </div>

      {/* The machined readout face — recessed well (layer 1) + CSS top-sheen
          (layer 2, ::before) carry the pocket + cylindrical depth. The two SVG
          overlays (layers 3+4) sit on top, pointer-events:none. The digits
          (layer 5) render LAST in DOM so they receive the click / wheel. */}
      <div className="freq-readout" ref={readoutRef} style={recessedWell({ radius: bezelRx })}>
        <svg
          className="freq-instrument"
          viewBox={`0 0 ${vbW} ${vbH}`}
          preserveAspectRatio="none"
          aria-hidden
        >
          {/* (3) wet-glass specular dome + drifting sheen over the readout */}
          <GlassDome defsId="vfo" x={0} y={0} width={vbW} height={vbH} rx={bezelRx} />
          {/* (4) machined chrome bezel ring framing the readout */}
          <GaugeBezel
            variant="rect"
            defsId="vfo"
            x={0}
            y={0}
            width={vbW}
            height={vbH}
            rx={bezelRx}
            thickness={bezelThick}
            nonScaling
          />
        </svg>

        {locked && (
          <span className="freq-lock-mark" aria-hidden title="VFO locked">
            <LockGlyph locked />
          </span>
        )}

        {editing ? (
          <div className="freq-digits mono" style={{ gap: compact ? 3 : 6 }}>
            <input
              ref={inputRef}
              type="text"
              inputMode="decimal"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={onKeyDown}
              onBlur={cancelEdit}
              aria-label={`${label} frequency in kHz`}
              className="freq-edit-input"
              placeholder="kHz"
            />
            <span className="label-xs freq-edit-unit">kHz</span>
          </div>
        ) : (
          <button
            ref={digitsContainerRef}
            type="button"
            onClick={beginEdit}
            aria-label="Edit frequency"
            title={
              locked
                ? 'VFO locked — unlock to tune'
                : 'Click to enter frequency in kHz — scroll the wheel over a digit to tune it'
            }
            className="freq-digits mono freq-digits-btn"
          >
            {digits.map((place) => {
              const d = digitAt(vfoHz, place.decade);
              const isLeading = vfoHz < place.decade;
              return (
                <Fragment key={place.decade}>
                  <span
                    className={`digit ${isLeading ? 'leading' : ''}`}
                    data-decade={place.decade}
                  >
                    {d}
                  </span>
                  {place.separatorAfter && (
                    <span aria-hidden className="sep">
                      {place.separatorAfter}
                    </span>
                  )}
                </Fragment>
              );
            })}
          </button>
        )}
      </div>

      <div className="freq-bot">
        <span className="label-xs">
          {locked
            ? 'LOCKED — unlock to tune'
            : 'MHz · click to type · wheel on a digit to step'}
        </span>
      </div>
    </div>
  );
}
