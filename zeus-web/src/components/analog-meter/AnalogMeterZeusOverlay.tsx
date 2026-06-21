// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// "Zeus Mode" overlay for the analog S-meter. The bitmap of Zeus
// (docs/wallpaper/zeus_lightening.png, served from /zeus_lightening.png)
// is anchored to the bottom-center of the dial face. It fades in starting
// at S9 and is fully solid at S9+20 ("S20+"). At full power the blue
// drop-shadow flickers to suggest crackling lightning.
//
// Pure visual flair — never gates behaviour, never reads anything off the
// wire. Caller decides whether the overlay should be active (Zeus mode on,
// S-scale active, S-scale enabled).

import { useEffect, useState } from 'react';

const FADE_IN_START = 9; // begin to materialise (S9)
const FADE_IN_FULL = 11; // fully solid (S9+20 = "S20+")

function visibility(sValue: number): number {
  if (sValue <= FADE_IN_START) return 0;
  if (sValue >= FADE_IN_FULL) return 1;
  return (sValue - FADE_IN_START) / (FADE_IN_FULL - FADE_IN_START);
}

export interface AnalogMeterZeusOverlayProps {
  /** Current S-meter value (0..15). Drives fade-in + lightning flicker. */
  sValue: number;
  /** Caller-controlled gate. Overlay renders nothing when false. */
  active: boolean;
}

export function AnalogMeterZeusOverlay({ sValue, active }: AnalogMeterZeusOverlayProps) {
  const [tick, setTick] = useState(0);

  // Only animate when there is something to show.
  const vis = active ? visibility(sValue) : 0;
  const visible = vis > 0.001;

  useEffect(() => {
    if (!visible) return;
    let raf = 0;
    const loop = () => {
      setTick((t) => (t + 1) % 1_000_000);
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [visible]);

  if (!visible) return null;

  const bobPx = Math.sin(tick * 0.05) * 4;
  const isFull = vis >= 0.999;
  const flicker = isFull
    ? 0.7 + Math.abs(Math.sin(tick * 0.6)) * 0.3 + (Math.sin(tick * 1.7) > 0.92 ? 0.4 : 0)
    : 1;

  return (
    <div className="am-zeus-overlay" style={{ opacity: vis }} aria-hidden="true">
      <img
        className="am-zeus-img"
        src="/zeus_lightening.png"
        alt=""
        style={{
          transform: `translateY(${bobPx}px)`,
          filter:
            `drop-shadow(0 0 ${10 + flicker * 14}px rgba(74,158,255,${0.55 * flicker}))` +
            ` drop-shadow(0 0 ${24 + flicker * 30}px rgba(120,180,255,${0.35 * flicker}))`,
        }}
      />
    </div>
  );
}
