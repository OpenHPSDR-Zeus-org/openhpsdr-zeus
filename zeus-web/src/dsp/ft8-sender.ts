// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// SENDER extraction for the worked-before decorate — a 1:1 TS port of the
// backend Ft8MessageParse.TryParseSender (the parser the server used to flag
// workedBefore before the digital suite moved into the Zeus Digital plugin).
// Deliberately a NEW function, NOT parseFt8Message: the sequencer's parser has
// different acceptance rules (it happily latches hashed <...> calls and does
// not do the CQ-directive skip / TARGET DE PAYLOAD sender selection this one
// pins). Unit-tested against the same vectors as the C# Ft8MessageParseTests
// so the render-time highlight matches what the server produced.
//
// Standard FT8/FT4 message grammar (all uppercase, space-separated):
//   CQ [DIRECTIVE] CALL [GRID4]   — sender = CALL, grid if a 4-char Maidenhead
//   TARGET DE PAYLOAD             — sender = DE (token 1); grid only when
//                                   PAYLOAD is a plain 4-char locator
// Anything else (free text, no plausible callsign) yields null.

// A 4-character Maidenhead locator: two A–R letters then two digits.
const GRID_RE = /^[A-R]{2}[0-9]{2}$/;

// The "core" of a plausible amateur callsign: at least one letter AND one
// digit, composed only of letters/digits/slash (covers /P, /MM, prefixes).
const CALL_CORE_RE = /^[A-Z0-9/]+$/;

export interface Ft8Sender {
  /** The transmitting callsign, uppercase, hash markers stripped. */
  call: string;
  /** 4-char Maidenhead locator when the message carries one, else null. */
  grid: string | null;
}

/**
 * Extract the sender (transmitting) callsign and, when present, the grid from
 * a decoded FT8/FT4 message line. Returns null when no plausible callsign can
 * be identified (free text, or a hashed/unresolvable `<...>` call).
 */
export function tryParseSender(text: string | null | undefined): Ft8Sender | null {
  if (text == null || text.trim().length === 0) return null;

  const tokens = text.trim().toUpperCase().split(/\s+/);
  const first = tokens[0];
  if (first === undefined) return null;

  // CQ family: "CQ [DIRECTIVE] CALL [GRID]".
  if (first === 'CQ') {
    // A directive (DX / POTA / TEST / a zone number, etc.) sits between CQ and
    // the callsign and is not itself a call.
    let i = 1;
    const second = tokens[1];
    const third = tokens[2];
    if (tokens.length >= 3 && second !== undefined && third !== undefined &&
        !looksLikeCall(second) && looksLikeCall(third)) {
      i = 2;
    }

    const candidate = tokens[i];
    if (candidate === undefined) return null;
    const call = tryRealCall(candidate);
    if (call == null) return null;

    const next = tokens[i + 1];
    const grid = next !== undefined && isGrid(next) ? next : null;
    return { call, grid };
  }

  // Directed: "TARGET DE PAYLOAD" — sender is the DE call (token 1).
  const de = tokens[1];
  if (tokens.length >= 2 && de !== undefined && (looksLikeCall(first) || first.startsWith('<'))) {
    const call = tryRealCall(de);
    if (call == null) return null;

    // The grid only appears as a plain Tx1 locator. RR73 also matches the
    // grid regex (R,R,7,3) so it must be excluded explicitly; reports
    // (+05 / R-12) and RRR/73 never match GRID_RE.
    let grid: string | null = null;
    const payload = tokens[2];
    if (payload !== undefined && payload !== 'RR73' && isGrid(payload)) grid = payload;
    return { call, grid };
  }

  return null;
}

function isGrid(token: string): boolean {
  return GRID_RE.test(token);
}

// Backend parity: a hashed <...> token "looks like" a call for grammar
// disambiguation, as does any token with a letter+digit core.
function looksLikeCall(token: string): boolean {
  if (token.length >= 2 && token.startsWith('<') && token.endsWith('>')) return true;
  const core = stripHash(token);
  return hasLetter(core) && hasDigit(core) && CALL_CORE_RE.test(core);
}

// A reportable callsign: the hash markers are stripped and the remainder must
// be a real call (letter+digit, valid charset). A purely-hashed unknown call
// (e.g. "<...>") strips to junk and is rejected. A bare 4-char Maidenhead
// locator (e.g. "FN42") also satisfies the letter+digit core check but is
// NEVER a callsign; rejecting it stops free-text decodes such as "K1ABC FN42"
// from flagging a nonexistent station.
function tryRealCall(token: string): string | null {
  const core = stripHash(token);
  if (GRID_RE.test(core)) return null;
  if (hasLetter(core) && hasDigit(core) && CALL_CORE_RE.test(core)) return core;
  return null;
}

function stripHash(token: string): string {
  let start = 0;
  let end = token.length;
  if (end > 0 && token[0] === '<') start = 1;
  if (end > start && token[end - 1] === '>') end--;
  return token.slice(start, end);
}

function hasLetter(s: string): boolean {
  return /[A-Z]/.test(s);
}

function hasDigit(s: string): boolean {
  return /[0-9]/.test(s);
}
