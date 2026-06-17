// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

export type SpectrumReceiverId = 'A' | 'B';

// Mirrors FilterMiniPan's passband palette: VFO A uses --accent, VFO B uses
// --signal with the same green fallback used by the bandwidth filter panel.
export function spectrumReceiverFilterColor(receiver: SpectrumReceiverId): string {
  return receiver === 'B' ? 'var(--signal, #25d366)' : 'var(--accent, #4a9eff)';
}
