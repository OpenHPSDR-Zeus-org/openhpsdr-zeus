// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FT8/FT4 decoded-message parser. WSJT-X/JTDX QSO messages follow a tiny fixed
// grammar; this classifies a decoded line and pulls out the fields the UI needs:
// who sent it (DE), who it is directed at (target, for "calling me"
// highlighting), the grid locator, and the signal report. It drives decode-table
// highlighting and click-to-call staging. It does NOT transmit anything.
//
// Standard message shapes (all uppercase, space-separated):
//   CQ CALL GRID                 — calling CQ (also "CQ DX CALL GRID",
//                                  "CQ POTA CALL GRID" with a 1–4 char directive)
//   TARGET DE GRID               — Tx1: answer/begin, sender's grid
//   TARGET DE +RPT | -RPT        — Tx2: signal report (dB, not RST)
//   TARGET DE R+RPT | R-RPT      — Tx3: roger + report
//   TARGET DE RR73 | RRR | 73    — Tx4/Tx5: roger-roger / 73
//   anything else                — free text / non-standard

/** Classification of a decoded FT8/FT4 message. */
export type Ft8MessageKind =
  | 'cq'
  | 'grid'
  | 'report'
  | 'rreport'
  | 'rr73'
  | 'rrr'
  | '73'
  | 'free';

export interface Ft8Message {
  raw: string;
  kind: Ft8MessageKind;
  /** Station that sent this message (the DE call), if identifiable. */
  deCall: string | null;
  /** Station this message is directed at, if any (null for CQ / free text). */
  targetCall: string | null;
  /** Optional CQ directive, e.g. "DX", "POTA" (null for a plain CQ). */
  cqDirective: string | null;
  /** 4-char Maidenhead grid, if present. */
  grid: string | null;
  /** Signal report in dB (for report / rreport kinds). */
  reportDb: number | null;
  /** True when this message is directed at `myCall`. */
  isCallingMe: boolean;
}

/** A 4-character Maidenhead locator: two A–R letters then two digits. */
const GRID_RE = /^[A-R]{2}[0-9]{2}$/;
const REPORT_RE = /^[+-]\d{1,2}$/;
const RREPORT_RE = /^R[+-]\d{1,2}$/;

/** Looks like an amateur callsign (incl. /P, /MM, hashed <...> forms). */
function looksLikeCall(token: string): boolean {
  if (token.startsWith('<') && token.endsWith('>')) return true;
  // At least one digit and one letter, plausibly with a / suffix/prefix.
  const core = token.replace(/^</, '').replace(/>$/, '');
  return /[A-Z]/.test(core) && /[0-9]/.test(core) && /^[A-Z0-9/]+$/.test(core);
}

function normCall(token: string | undefined): string | null {
  if (!token) return null;
  return token.replace(/^<|>$/g, '') || null;
}

/**
 * Parse one decoded FT8/FT4 message line. `myCall` (optional) enables the
 * `isCallingMe` flag. Never throws; unrecognized input returns kind 'free'.
 */
export function parseFt8Message(text: string, myCall?: string | null): Ft8Message {
  const raw = text;
  const me = myCall ? myCall.trim().toUpperCase() : null;
  const base: Ft8Message = {
    raw,
    kind: 'free',
    deCall: null,
    targetCall: null,
    cqDirective: null,
    grid: null,
    reportDb: null,
    isCallingMe: false,
  };

  const tokens = text.trim().toUpperCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return base;

  // CQ family: "CQ [DIRECTIVE] CALL [GRID]"
  if (tokens[0] === 'CQ') {
    // Distinguish "CQ DX CALL GRID" / "CQ POTA CALL GRID" from "CQ CALL GRID":
    // a directive sits between CQ and the callsign and is not itself a call.
    let i = 1;
    let directive: string | null = null;
    if (tokens.length >= 3 && !looksLikeCall(tokens[1]!) && looksLikeCall(tokens[2]!)) {
      directive = tokens[1]!;
      i = 2;
    }
    const deCall = normCall(tokens[i]);
    const maybeGrid = tokens[i + 1];
    return {
      ...base,
      kind: 'cq',
      deCall,
      cqDirective: directive,
      grid: maybeGrid && GRID_RE.test(maybeGrid) ? maybeGrid : null,
    };
  }

  // Directed: "TARGET DE <payload>"
  if (tokens.length >= 2 && (looksLikeCall(tokens[0]!) || tokens[0]!.startsWith('<'))) {
    const targetCall = normCall(tokens[0]);
    const deCall = normCall(tokens[1]);
    const payload = tokens[2];
    const isCallingMe = !!me && targetCall === me;

    // Order matters: RR73 also matches the grid regex (R,R,7,3), so the
    // roger/73 literals are tested first.
    if (payload === 'RR73') {
      return { ...base, kind: 'rr73', targetCall, deCall, isCallingMe };
    }
    if (payload === 'RRR') {
      return { ...base, kind: 'rrr', targetCall, deCall, isCallingMe };
    }
    if (payload === '73') {
      return { ...base, kind: '73', targetCall, deCall, isCallingMe };
    }
    if (payload && RREPORT_RE.test(payload)) {
      return {
        ...base,
        kind: 'rreport',
        targetCall,
        deCall,
        reportDb: parseInt(payload.slice(1), 10),
        isCallingMe,
      };
    }
    if (payload && REPORT_RE.test(payload)) {
      return {
        ...base,
        kind: 'report',
        targetCall,
        deCall,
        reportDb: parseInt(payload, 10),
        isCallingMe,
      };
    }
    if (payload && GRID_RE.test(payload)) {
      return { ...base, kind: 'grid', targetCall, deCall, grid: payload, isCallingMe };
    }
    // Directed but non-standard payload (e.g. free text addressed to a call).
    return { ...base, kind: 'free', targetCall, deCall, isCallingMe };
  }

  return base;
}
