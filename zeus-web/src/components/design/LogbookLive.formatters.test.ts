// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Regression for issue #486: the logbook used to render QSO timestamps
// in the browser's local timezone, drifting the clock by the user's
// offset. Ham-radio convention is UTC always — both formatters must pin
// timeZone:'UTC' regardless of where the operator's browser thinks it
// is, otherwise an operator in CET would see an 19:30Z QSO logged at
// "21:30" on a 21:30-wall-clock summer evening.

import { describe, expect, it } from 'vitest';
import { formatQsoDateUtc, formatQsoTimeUtc } from './logbook-formatters';

describe('LogbookLive formatters', () => {
  // Pick a timestamp where UTC and any plausible local timezone clearly
  // disagree on the calendar day, so a missing timeZone:'UTC' option
  // would obviously land on the wrong date and hour.
  const ISO_LATE_NIGHT_UTC = '2026-05-24T23:30:00Z';
  const ISO_EARLY_MORNING_UTC = '2026-05-25T01:15:00Z';

  it('formats time as the UTC clock value regardless of the browser TZ', () => {
    // 23:30 UTC must render as "23:30" everywhere. A CET-summer browser
    // (UTC+2) would render "01:30" if timeZone:'UTC' were missing.
    // Time format is digit-only (24h) so this assertion is locale-stable.
    expect(formatQsoTimeUtc(ISO_LATE_NIGHT_UTC)).toBe('23:30');
    // Sanity-check a second value to catch a transposed hour/minute fix.
    expect(formatQsoTimeUtc(ISO_EARLY_MORNING_UTC)).toBe('01:15');
  });

  it('formats date as the UTC calendar day, not the browser day', () => {
    // The localised month name differs by environment ("May 24" in en-US,
    // "24 may" in es-ES, "24 May" in en-GB, …) so we assert against the
    // reference UTC formatter rather than a hard-coded string. The key
    // property: format(localTz) === format(UTC) for THIS input only if
    // the formatter pinned timeZone:'UTC'. The day-number check on top
    // catches the "shifted to next day" failure mode that timezone bugs
    // produce on late-night-UTC timestamps.
    const refLateNight = new Date(ISO_LATE_NIGHT_UTC).toLocaleDateString([], {
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    });
    const refEarlyMorning = new Date(ISO_EARLY_MORNING_UTC).toLocaleDateString([], {
      month: 'short',
      day: 'numeric',
      timeZone: 'UTC',
    });
    expect(formatQsoDateUtc(ISO_LATE_NIGHT_UTC)).toBe(refLateNight);
    expect(formatQsoDateUtc(ISO_EARLY_MORNING_UTC)).toBe(refEarlyMorning);

    // Day-number must be 24 (the UTC day) — not 25, which is what a
    // CET-summer browser would render without timeZone:'UTC'.
    expect(formatQsoDateUtc(ISO_LATE_NIGHT_UTC)).toMatch(/\b24\b/);
    expect(formatQsoDateUtc(ISO_EARLY_MORNING_UTC)).toMatch(/\b25\b/);
  });
});
