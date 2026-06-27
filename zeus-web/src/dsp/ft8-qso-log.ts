// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ft8-qso-log — PURE glue between the FT8/FT4 QSO state machine and the native
// logbook. `qsoStateToLogEntry` maps a completed QsoState (+ the dial context)
// into the existing CreateLogEntryRequest used by LogService; `computeFt8Stats`
// derives the workspace STATS panel numbers from the logbook entries. No I/O, no
// React — same inputs, same output, so both are unit-testable directly.
//
// Reuses the existing logbook stack (LogService / useLoggerStore / /api/log) —
// this module never opens a new store or DB.

import { fmtSnr, type QsoState, type DigitalQsoMode } from './ft8-sequencer';
import type { CreateLogEntryRequest, LogEntry } from '../api/log';
import { distanceKm, gridToLatLon } from '../components/design/geo';

/** Dial/mode context for a logged QSO — supplied by the workspace at log time. */
export interface QsoLogContext {
  /** Band label, e.g. "20m". */
  band: string;
  /** Dial frequency in MHz (the FT8/FT4 dial; audio offset is not added). */
  freqMhz: number;
  /** Logged mode — FT8 or FT4. */
  mode: DigitalQsoMode;
}

/**
 * Map a completed (or in-progress) QSO state into a logbook create request.
 * Returns null when there is no DX callsign to log (nothing to record yet).
 *
 * FT8/FT4 report the SNR in dB, so RST SENT = the report we sent him
 * (`sentReportToHim`) and RST RCVD = the report he sent us
 * (`rcvdReportFromHim`), both formatted as signed 2-digit dB.
 */
export function qsoStateToLogEntry(
  state: QsoState,
  ctx: QsoLogContext,
  now: Date = new Date(),
): CreateLogEntryRequest | null {
  const callsign = state.dxCall?.trim().toUpperCase();
  if (!callsign) return null;

  return {
    callsign,
    frequencyMhz: ctx.freqMhz,
    // ADIF/QRZ expect the band as e.g. "20M"; the workspace label is "20m".
    band: ctx.band.toUpperCase(),
    mode: ctx.mode,
    rstSent: state.sentReportToHim != null ? fmtSnr(state.sentReportToHim) : '',
    rstRcvd: state.rcvdReportFromHim != null ? fmtSnr(state.rcvdReportFromHim) : '',
    grid: state.dxGrid4 ?? null,
    qsoDateTimeUtc: now.toISOString(),
  };
}

/** Aggregate numbers for the FT8 STATS panel. */
export interface Ft8Stats {
  /** QSOs whose UTC date is today. */
  qsosToday: number;
  /** Total QSOs in the (loaded) logbook. */
  qsosTotal: number;
  /** QSOs marked confirmed (proxy: uploaded to QRZ). */
  confirmed: number;
  /** Distinct 4-char grids worked. */
  distinctGrids: number;
  /**
   * Distinct DXCC entities. NOTE: only QSOs whose Country/Dxcc came from an
   * ADIF import carry this — native FT8 QSOs have no country resolver yet
   * (no cty.dat in-tree). Treat as a floor, not a true DXCC total.
   */
  distinctDxcc: number;
  /** Average received SNR (dB) across digital QSOs, or null when none parse. */
  avgSnrRx: number | null;
  /** Farthest QSO from the operator grid, if both grids are known. */
  bestDx: { callsign: string; grid: string; km: number } | null;
}

const isSameUtcDay = (a: Date, b: Date): boolean =>
  a.getUTCFullYear() === b.getUTCFullYear() &&
  a.getUTCMonth() === b.getUTCMonth() &&
  a.getUTCDate() === b.getUTCDate();

/** Parse a signed dB report ("-12", "+03", "R-09") to a number, or null. */
function parseDbReport(rst: string | null | undefined): number | null {
  if (!rst) return null;
  const m = rst.trim().match(/^R?([+-]?\d{1,2})$/i);
  if (!m) return null;
  const n = Number(m[1]);
  return Number.isFinite(n) ? n : null;
}

/**
 * Derive the STATS-panel aggregates from the logbook entries. Pure over the
 * entries + the operator grid (used only for the best-DX great-circle range).
 */
export function computeFt8Stats(
  entries: readonly LogEntry[],
  operatorGrid?: string | null,
  now: Date = new Date(),
): Ft8Stats {
  const grids = new Set<string>();
  const dxcc = new Set<number>();
  const snrs: number[] = [];
  let qsosToday = 0;
  let confirmed = 0;

  const opLatLon = operatorGrid ? gridToLatLon(operatorGrid) : null;
  let bestDx: Ft8Stats['bestDx'] = null;

  for (const e of entries) {
    if (e.grid) grids.add(e.grid.slice(0, 4).toUpperCase());
    if (e.dxcc != null) dxcc.add(e.dxcc);
    if (e.qrzUploadedUtc) confirmed += 1;

    const when = new Date(e.qsoDateTimeUtc);
    if (!Number.isNaN(when.getTime()) && isSameUtcDay(when, now)) qsosToday += 1;

    // Only FT8/FT4 reports are dB SNRs; a phone "59" is an RST, not an SNR, so
    // restrict the average to the digital modes this workspace logs.
    if (/^FT/i.test(e.mode)) {
      const rx = parseDbReport(e.rstRcvd);
      if (rx != null) snrs.push(rx);
    }

    if (opLatLon && e.grid) {
      const there = gridToLatLon(e.grid);
      if (there) {
        const km = distanceKm(opLatLon.lat, opLatLon.lon, there.lat, there.lon);
        if (!bestDx || km > bestDx.km) {
          bestDx = { callsign: e.callsign, grid: e.grid.slice(0, 4).toUpperCase(), km };
        }
      }
    }
  }

  return {
    qsosToday,
    qsosTotal: entries.length,
    confirmed,
    distinctGrids: grids.size,
    distinctDxcc: dxcc.size,
    avgSnrRx: snrs.length > 0 ? snrs.reduce((a, b) => a + b, 0) / snrs.length : null,
    bestDx,
  };
}
