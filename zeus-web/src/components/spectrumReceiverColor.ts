// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

// 'A' = RX1, 'B' = RX2, number = a 0-based receiver index >= 2 (RX3+).
export type SpectrumReceiverId = 'A' | 'B' | number;

// Mirrors FilterMiniPan's passband palette: VFO A uses --accent, VFO B uses
// --signal with the same green fallback used by the bandwidth filter panel.
// RX3+ (numeric index) delegate to receiverColorByIndex for a distinct hue.
export function spectrumReceiverFilterColor(receiver: SpectrumReceiverId): string {
  if (receiver === 'B') return 'var(--signal, #25d366)';
  if (typeof receiver === 'number') return receiverColorByIndex(receiver);
  return 'var(--accent, #4a9eff)';
}

// Per-receiver identity colour for the multi-DDC panels (VFO lanes, hero mixer
// chips). RX1/RX2 keep the canonical A/B accent/signal pair; RX3+ get distinct
// hues spread around the wheel, kept clear of the reserved accent/tx/power
// bands. This is data-viz identity (which receiver is which), not UI chrome.
export function receiverColorByIndex(index: number): string {
  if (index <= 0) return 'var(--accent, #4a9eff)';
  if (index === 1) return 'var(--signal, #25d366)';
  const hue = (175 + (index - 2) * 53) % 360;
  return `hsl(${hue} 68% 62%)`;
}
