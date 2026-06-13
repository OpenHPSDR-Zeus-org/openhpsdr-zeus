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

export function VfoDisplay() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const applyState = useConnectionStore((s) => s.applyState);

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
    setDraft(formatKhz(vfoHz));
    setEditing(true);
  }, [vfoHz]);

  const cancelEdit = useCallback(() => {
    setEditing(false);
    setDraft('');
  }, []);

  const commitEdit = useCallback(() => {
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
      const target = e.target as Element | null;
      const digit = target?.closest<HTMLElement>('[data-decade]');
      if (!digit || !el.contains(digit)) return;
      const decadeAttr = digit.dataset.decade;
      if (!decadeAttr) return;
      const decade = Number.parseInt(decadeAttr, 10);
      if (!Number.isFinite(decade) || decade <= 0) return;
      e.preventDefault();

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

  return (
    <div className="freq-display">
      {editing ? (
        <div className="freq-digits mono" style={{ gap: 6 }}>
          <input
            ref={inputRef}
            type="text"
            inputMode="decimal"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKeyDown}
            onBlur={cancelEdit}
            aria-label="Frequency in kHz"
            style={{
              width: 220,
              background: 'transparent',
              border: 'none',
              borderBottom: '1px solid var(--accent)',
              outline: 'none',
              color: 'var(--fg-0)',
              fontFamily: 'inherit',
              fontSize: 'inherit',
              fontWeight: 700,
            }}
            placeholder="kHz"
          />
          <span className="label-xs" style={{ alignSelf: 'center' }}>
            kHz
          </span>
        </div>
      ) : (
        <button
          ref={digitsContainerRef}
          type="button"
          onClick={beginEdit}
          aria-label="Edit frequency"
          title="Click to enter frequency in kHz — scroll the wheel over a digit to tune it"
          className="freq-digits mono"
          style={{ background: 'none', border: 'none', cursor: 'text', width: '100%' }}
        >
          {digits.map((place) => {
            const d = digitAt(vfoHz, place.decade);
            const isLeading = vfoHz < place.decade;
            return (
              <Fragment key={place.decade}>
                <span
                  className={`digit ${isLeading ? 'leading' : ''}`}
                  data-decade={place.decade}
                  style={{ cursor: 'ns-resize' }}
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
      <div className="freq-bot" style={{ justifyContent: 'flex-end', gap: 6, marginTop: 4 }}>
        <span className="label-xs">MHz · click to type · wheel on a digit to step</span>
      </div>
    </div>
  );
}
