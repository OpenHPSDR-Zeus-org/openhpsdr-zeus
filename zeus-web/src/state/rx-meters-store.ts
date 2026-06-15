// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Transient store for the RX-meter telemetry pushed via RxMetersV2Frame
// (MsgType 0x19) at 5 Hz. Mirrors the tx-store treatment of meter values:
// not persisted (operator preferences live elsewhere), defaults look quiet
// so first-paint doesn't spike a fresh widget.
//
// Field names mirror the wire layout (plan §1.3). Sentinels (≤ −200 dBm /
// dBFS) propagate through unchanged so the UI can render an em-dash for
// stages whose underlying WDSP path hasn't started — same convention as
// the TX-stage meters.

import { create } from 'zustand';

const QUIET_DB = -Infinity;

function finiteDb(value: number): number {
  return Number.isFinite(value) ? value : QUIET_DB;
}

function finiteOrZero(value: number): number {
  return Number.isFinite(value) ? value : 0;
}

export interface RxMeters {
  signalPk: number; // dBm, calibrated
  signalAv: number; // dBm, calibrated
  adcPk: number; // dBFS
  adcAv: number; // dBFS
  agcGain: number; // dB, signed (positive = AGC boosting)
  agcEnvPk: number; // dBm, calibrated
  agcEnvAv: number; // dBm, calibrated
}

export interface RxMetersState extends RxMeters {
  setMeters: (m: RxMeters) => void;
}

export const useRxMetersStore = create<RxMetersState>((set) => ({
  // Quiet defaults — same treatment as tx-store. Floor everything at -Infinity
  // so widgets render as "no data yet" until the first 0x19 frame lands.
  signalPk: -Infinity,
  signalAv: -Infinity,
  adcPk: -Infinity,
  adcAv: -Infinity,
  agcGain: 0,
  agcEnvPk: -Infinity,
  agcEnvAv: -Infinity,
  setMeters: (m) =>
    set({
      signalPk: finiteDb(m.signalPk),
      signalAv: finiteDb(m.signalAv),
      adcPk: finiteDb(m.adcPk),
      adcAv: finiteDb(m.adcAv),
      agcGain: finiteOrZero(m.agcGain),
      agcEnvPk: finiteDb(m.agcEnvPk),
      agcEnvAv: finiteDb(m.agcEnvAv),
    }),
}));
