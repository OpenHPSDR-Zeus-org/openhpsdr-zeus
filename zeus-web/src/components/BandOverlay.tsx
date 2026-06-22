// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useEffect, useMemo, useRef } from 'react';
import {
  binarySearchSegment,
  modeMatchesRestriction,
  type BandSegment,
  type RxMode,
} from '../api/bands';
import { useBandPlanStore } from '../state/bandPlan';
import { useConnectionStore } from '../state/connection-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import * as viewCenter from '../state/view-center';

// Issue #846: licence-class band overlay drawn behind the spectrum trace.
//
// Each band-plan segment for the active region renders as a translucent
// horizontal stripe along the bottom of the panadapter, plus a tiny label
// along the top edge. Colour encodes licence/mode legality:
//   - green   → amateur, the operator's current mode is permitted here
//   - amber   → amateur, but the current mode is NOT permitted (e.g. SSB in
//               the CW-only sub-band — the same colour code Zeus uses for
//               "almost legal but careful" everywhere else)
//   - red     → not an amateur allocation at all (gap between bands, SWL,
//               broadcast etc.)
//
// Motion model mirrors FreqAxis: segments are laid out at React-commit rate
// around the most recent frame's `centerHz`, and a draw-bus callback slides
// the whole strip by the (frame-center → animated view-center) fraction of
// the span so the bars glide in lock-step with the trace during a tuning
// glide. This keeps React out of the per-frame path entirely.
//
// The overlay is RX-1 only (`receiver === 'A'`) — RX2 / stitched halves keep
// their existing clean look so the operator can compare bands at a glance.

const COLORS = {
  // Bottom strip (the translucent bar) and the matching top boundary line.
  inLicence: { strip: 'rgba(80, 200, 120, 0.18)', edge: 'rgba(80, 200, 120, 0.45)', label: 'rgba(200, 240, 210, 0.95)' },
  outOfMode: { strip: 'rgba(255, 160, 40, 0.18)', edge: 'rgba(255, 160, 40, 0.45)', label: 'rgba(255, 220, 170, 0.95)' },
  nonAmateur: { strip: 'rgba(255, 80, 80, 0.18)', edge: 'rgba(255, 80, 80, 0.45)', label: 'rgba(255, 200, 200, 0.95)' },
} as const;

// Strip height as a fraction of the panadapter (bottom band only — the trace
// keeps the upper portion uncluttered). 18% lands well below typical signal
// peaks while staying tall enough to read at a glance.
const STRIP_HEIGHT_FRAC = 0.18;
// Min visible width before we even bother laying out a label, so a 100 Hz
// sliver doesn't try to render text under it.
const MIN_LABEL_WIDTH_PCT = 4;

type BandOverlayProps = {
  receiver?: 'A' | 'B';
};

type SegmentColors = (typeof COLORS)[keyof typeof COLORS];

function colorsFor(seg: BandSegment, mode: RxMode): SegmentColors {
  if (seg.allocation !== 'Amateur') return COLORS.nonAmateur;
  return modeMatchesRestriction(mode, seg.modeRestriction)
    ? COLORS.inLicence
    : COLORS.outOfMode;
}

export function BandOverlay({ receiver = 'A' }: BandOverlayProps = {}) {
  const enabled = useDisplaySettingsStore((s) => s.showBandOverlay);
  const segments = useBandPlanStore((s) => s.segments);
  const centerHz = useDisplayStore((s) => selectDisplaySlice(s, receiver).centerHz);
  const hzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  const width = useDisplayStore((s) => selectDisplaySlice(s, receiver).width);
  const mode = useConnectionStore((s) => (receiver === 'B' ? s.modeB : s.mode));

  const stripRef = useRef<HTMLDivElement | null>(null);

  // Smooth motion (issue #597 pattern): the strip is laid out around the
  // frame's centerHz, then translated by the (layoutCenter → viewCenter)
  // fraction of its own width every draw-bus tick — same clock as the trace.
  useEffect(() => {
    if (!enabled) return;
    const vc = viewCenter.viewCenterFor(receiver);
    const update = () => {
      const strip = stripRef.current;
      if (!strip) return;
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (!s.width || s.hzPerPixel <= 0) return;
      const spanHz = s.width * s.hzPerPixel;
      const layoutCenter = Number(s.centerHz);
      const view = vc.isInitialized() ? vc.getViewCenterHz() : layoutCenter;
      const fracPct = ((layoutCenter - view) / spanHz) * 100;
      strip.style.transform = `translateX(${fracPct}%)`;
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = vc.subscribe(schedule);
    const unsubFrame = useDisplayStore.subscribe((s, prev) => {
      if (selectDisplaySlice(s, receiver).lastSeq !== selectDisplaySlice(prev, receiver).lastSeq) {
        schedule();
      }
    });
    schedule();
    return () => {
      unsubVc();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, [enabled, receiver]);

  const laidOut = useMemo(() => {
    if (!enabled || !width || hzPerPixel <= 0 || !Array.isArray(segments) || segments.length === 0) return null;
    const spanHz = width * hzPerPixel;
    const center = Number(centerHz);
    const startHz = center - spanHz / 2;
    const endHz = center + spanHz / 2;
    // Visible range with a small overscan so a glide can't expose an unlabelled
    // gap before the next React commit catches up. Half a span on each side
    // matches what FreqAxis does for its tick strip.
    const visStart = startHz - spanHz / 2;
    const visEnd = endHz + spanHz / 2;
    const visible = segments.filter((s) => s.highHz >= visStart && s.lowHz <= visEnd);
    return visible.map((seg) => {
      const segLow = Math.max(visStart, seg.lowHz);
      const segHigh = Math.min(visEnd, seg.highHz);
      const leftPct = ((segLow - startHz) / spanHz) * 100;
      const rightPct = ((segHigh - startHz) / spanHz) * 100;
      const widthPct = rightPct - leftPct;
      const colors = colorsFor(seg, mode);
      return { seg, leftPct, widthPct, colors };
    });
  }, [enabled, segments, centerHz, hzPerPixel, width, mode]);

  if (!enabled || !laidOut || laidOut.length === 0) return null;

  return (
    <div
      aria-hidden
      className="pointer-events-none absolute inset-0 z-[4] overflow-hidden"
    >
      <div ref={stripRef} className="absolute inset-0">
        {laidOut.map(({ seg, leftPct, widthPct, colors }) => (
          <div
            key={`${seg.regionId}:${seg.lowHz}`}
            className="absolute"
            style={{
              left: `${leftPct}%`,
              width: `${widthPct}%`,
              bottom: 0,
              height: `${STRIP_HEIGHT_FRAC * 100}%`,
              background: colors.strip,
              borderTop: `1px solid ${colors.edge}`,
              borderLeft: `1px solid ${colors.edge}`,
              borderRight: `1px solid ${colors.edge}`,
            }}
          >
            {widthPct >= MIN_LABEL_WIDTH_PCT && (
              <div
                className="absolute left-1/2 -translate-x-1/2 truncate whitespace-nowrap font-mono text-[9px]"
                style={{
                  bottom: 1,
                  maxWidth: '100%',
                  padding: '0 3px',
                  color: colors.label,
                  textShadow: '0 0 2px rgba(0,0,0,0.85)',
                }}
              >
                {seg.label}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * Hook: returns the live in-band status (vfo + mode + active plan) and fires
 * the audible band-edge alert on transitions from in-licence to out-of-licence.
 * Mounted once at the top of the app so the alert tracks both RX1 (VFO A) and
 * the operator's current mode regardless of which panadapter is on screen.
 *
 * Kept here next to the visual overlay because the two share the
 * `modeMatchesRestriction` decision; splitting them would invite future drift.
 */
export function useBandEdgeAlert(): void {
  const enabled = useDisplaySettingsStore((s) => s.bandEdgeAlertEnabled);
  const segments = useBandPlanStore((s) => s.segments);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);

  const prevStatusRef = useRef<'in' | 'out' | 'unknown'>('unknown');

  useEffect(() => {
    // Defensive: this hook runs inside BandPlanProvider (app-wide), so a
    // non-array `segments` must not throw here — it would trip AppErrorBoundary
    // and blank the entire UI. The store coerces to [] too; this is belt+braces.
    if (!enabled || !Array.isArray(segments) || segments.length === 0) {
      prevStatusRef.current = 'unknown';
      return;
    }
    const seg = binarySearchSegment(segments, vfoHz);
    const inLicence = seg !== null && seg.allocation === 'Amateur' && modeMatchesRestriction(mode, seg.modeRestriction);
    const status = inLicence ? 'in' : 'out';
    const prev = prevStatusRef.current;
    prevStatusRef.current = status;
    if (prev === 'in' && status === 'out') {
      // Lazy import keeps the Web Audio code out of the initial bundle path
      // for users who disable the alert.
      void import('../audio/band-edge-alert').then((m) => m.playBandEdgeAlert());
    }
  }, [enabled, segments, vfoHz, mode]);
}
