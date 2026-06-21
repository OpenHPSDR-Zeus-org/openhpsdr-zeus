// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// One-stop selector hook: maps a `MeterReadingId` to its live numeric value
// pulled from the appropriate Zustand store (tx-store for TX + the existing
// 0x14 RX dBm; rx-meters-store for the new 0x19 fields).
//
// Returning NaN for "no reading yet" lets widgets render an em-dash without
// reasoning about Zustand store-init order.

import { useTxStore } from '../../state/tx-store';
import { useRxMetersStore } from '../../state/rx-meters-store';
import { MeterReadingId } from './meterCatalog';

/** Resolve a catalog reading to its current live value. NaN until the first
 *  matching frame lands. Re-renders only when the selected slice changes
 *  (Zustand selector closure). */
export function useMeterReading(id: MeterReadingId): number {
  // We have to call the same hooks unconditionally on every render. Each
  // selector reads ONE field, so React-Zustand will only re-render when
  // that field changes. The branch below decides which selector's value
  // gets returned. The non-selected stores' subscriptions are cheap.
  const rxStore = useRxMetersStore;
  const txStore = useTxStore;

  // RX 0x19 fields
  const rxSignalPk = rxStore((s) => s.signalPk);
  const rxSignalAv = rxStore((s) => s.signalAv);
  const rxAdcPk = rxStore((s) => s.adcPk);
  const rxAdcAv = rxStore((s) => s.adcAv);
  const rxAgcGain = rxStore((s) => s.agcGain);
  const rxAgcEnvPk = rxStore((s) => s.agcEnvPk);
  const rxAgcEnvAv = rxStore((s) => s.agcEnvAv);

  // TX 0x16 fields + the 0x14 RX dBm fallback
  const fwdW = txStore((s) => s.fwdWatts);
  const refW = txStore((s) => s.refWatts);
  const swr = txStore((s) => s.swr);
  const micPk = txStore((s) => s.wdspMicPk);
  const micAv = txStore((s) => s.micAv);
  const eqPk = txStore((s) => s.eqPk);
  const eqAv = txStore((s) => s.eqAv);
  const lvlrPk = txStore((s) => s.lvlrPk);
  const lvlrAv = txStore((s) => s.lvlrAv);
  const lvlrGr = txStore((s) => s.lvlrGr);
  const cfcPk = txStore((s) => s.cfcPk);
  const cfcAv = txStore((s) => s.cfcAv);
  const cfcGr = txStore((s) => s.cfcGr);
  const compPk = txStore((s) => s.compPk);
  const compAv = txStore((s) => s.compAv);
  const alcPk = txStore((s) => s.alcPk);
  const alcAv = txStore((s) => s.alcAv);
  const alcGr = txStore((s) => s.alcGr);
  const outPk = txStore((s) => s.outPk);
  const outAv = txStore((s) => s.outAv);

  switch (id) {
    case MeterReadingId.RxSignalPk:
      return rxSignalPk;
    case MeterReadingId.RxSignalAv:
      return rxSignalAv;
    case MeterReadingId.RxAdcPk:
      return rxAdcPk;
    case MeterReadingId.RxAdcAv:
      return rxAdcAv;
    case MeterReadingId.RxAgcGain:
      return rxAgcGain;
    case MeterReadingId.RxAgcEnvPk:
      return rxAgcEnvPk;
    case MeterReadingId.RxAgcEnvAv:
      return rxAgcEnvAv;

    case MeterReadingId.TxFwdWatts:
      return fwdW;
    case MeterReadingId.TxRefWatts:
      return refW;
    case MeterReadingId.TxSwr:
      return swr;
    case MeterReadingId.TxMicPk:
      return micPk;
    case MeterReadingId.TxMicAv:
      return micAv;
    case MeterReadingId.TxEqPk:
      return eqPk;
    case MeterReadingId.TxEqAv:
      return eqAv;
    case MeterReadingId.TxLvlrPk:
      return lvlrPk;
    case MeterReadingId.TxLvlrAv:
      return lvlrAv;
    case MeterReadingId.TxLvlrGr:
      return lvlrGr;
    case MeterReadingId.TxCfcPk:
      return cfcPk;
    case MeterReadingId.TxCfcAv:
      return cfcAv;
    case MeterReadingId.TxCfcGr:
      return cfcGr;
    case MeterReadingId.TxCompPk:
      return compPk;
    case MeterReadingId.TxCompAv:
      return compAv;
    case MeterReadingId.TxAlcPk:
      return alcPk;
    case MeterReadingId.TxAlcAv:
      return alcAv;
    case MeterReadingId.TxAlcGr:
      return alcGr;
    case MeterReadingId.TxOutPk:
      return outPk;
    case MeterReadingId.TxOutAv:
      return outAv;
  }
}
