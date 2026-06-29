// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

// Receiver discriminator. Numbers are canonical (0 = RX1, 1 = RX2, >= 2 =
// RX3+); 'A'/'B' remain accepted as legacy aliases for 0/1.
export type SpectrumReceiverId = 'A' | 'B' | number;

// Mirrors FilterMiniPan's passband palette: RX1/'A' uses --accent, RX2/'B' uses
// --signal (same green fallback as the bandwidth filter panel). RX3+ numeric
// keys delegate to receiverColorByIndex for distinct per-DDC hues.
export function spectrumReceiverFilterColor(receiver: SpectrumReceiverId): string {
  if (typeof receiver === 'number') return receiverColorByIndex(receiver);
  return receiver === 'B' ? 'var(--signal, #25d366)' : 'var(--accent, #4a9eff)';
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
