// SPDX-License-Identifier: GPL-2.0-or-later

// QSO timestamps are stored / exported / uploaded to QRZ in UTC throughout
// the server stack. Ham-radio convention is UTC always, so both formatters pin
// timeZone:'UTC' regardless of browser locale.
export function formatQsoTimeUtc(isoString: string): string {
  const date = new Date(isoString);
  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  });
}

export function formatQsoDateUtc(isoString: string): string {
  const date = new Date(isoString);
  return date.toLocaleDateString([], {
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  });
}
