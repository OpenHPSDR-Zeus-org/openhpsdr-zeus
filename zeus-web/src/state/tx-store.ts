// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

import type { CfcConfigDto, RadioStateDto } from '../api/client';
import { CFC_CONFIG_DEFAULT } from '../api/client';

// TX-side state. Intentionally separate from connection-store so the TX panel
// can mount/unmount cleanly and so TX-specific fields (drivePercent, micGainDb,
// meter values, SWR alert) can accumulate here as subsequent slices land.
// TxMetersV2 wire payload (MsgType 0x16, 20 f32 LE). TXA per-stage peak/average
// readings from WdspDspEngine.ProcessTxBlock. Valid during MOX/TUN only — idle
// or bypassed WDSP stages emit ≤ −200 dBFS (near the −400 sentinel) and `*Gr`
// fields stay at 0 when the stage is idle. Consumers should treat ≤ −200 as
// "bypassed" rather than a real level (see P1.4).
export type TxMeters = {
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micPk: number;
  micAv: number;
  eqPk: number;
  eqAv: number;
  lvlrPk: number;
  lvlrAv: number;
  lvlrGr: number;
  cfcPk: number;
  cfcAv: number;
  cfcGr: number;
  compPk: number;
  compAv: number;
  alcPk: number;
  alcAv: number;
  alcGr: number;
  outPk: number;
  outAv: number;
};

export enum AlertKind {
  // Wire kinds — must match Zeus.Contracts.AlertKind byte values exactly.
  SwrTrip = 0,
  TxTimeout = 1,
  OutOfBand = 2,
  // Frontend-only sentinel for service-worker update prompts. Raised locally
  // by useSwUpdatePrompt and consumed by AlertBanner; never sent on the wire,
  // so its value lives outside the wire-byte range to avoid collisions.
  FrontendUpdate = 1000,
}

export type AlertAction = {
  label: string;
  onClick: () => void;
};

export type Alert = {
  kind: AlertKind;
  message: string;
  action?: AlertAction;
};

export type TxState = {
  moxOn: boolean;
  setMoxOn: (on: boolean) => void;
  // Gates the mic-uplink WS push (use-mic-uplink.ts). Raised only by local
  // operator interaction — MoxButton click, spacebar PTT, MobilePttButton —
  // and lowered on the same interaction's release or when the server forces
  // MOX off (SWR trip, TX timeout, MoxStateFrame off-edge). NOT raised when
  // moxOn flips because of a TCI-driven server broadcast (issue #346): if
  // we raised it there, the browser would push silent mic samples in
  // parallel with the TCI audio path and corrupt the TX accumulator.
  localMicArmed: boolean;
  setLocalMicArmed: (on: boolean) => void;
  // PRD FR-7: TUN keys a single-tone carrier via WDSP SetTXAPostGen*.
  // Mutually exclusive with MOX — one is always canceled by the other so the
  // backend never sees both keyed. Exclusion is enforced inside the setters.
  tunOn: boolean;
  setTunOn: (on: boolean) => void;
  // PRD FR-4: drive starts at 10% so a first MOX click on an un-touched slider
  // can't flash full power into the PA.
  drivePercent: number;
  setDrivePercent: (p: number) => void;
  // TUN has its own drive %. Same PA-gain calibration as drive, so equal
  // percentages produce equal watts. Default 10 matches piHPSDR — zero would
  // make a first TUN press appear broken ("nothing happens").
  tunePercent: number;
  setTunePercent: (p: number) => void;
  // PRD FR-3: mic-gain slider 0..+20 dB (default 0). Server applies via
  // WDSP SetTXAPanelGain1(TXA, 10^(db/20)). Kept as int dB on the wire.
  micGainDb: number;
  setMicGainDb: (db: number) => void;
  // Leveler max-gain slider 0..+15 dB (default +5 — matches backend default
  // and HL2 community starting point). Higher = more aggressive voice
  // leveling; can push ALC into hard limiting. Persisted — a user preference
  // that should survive reload. Server clamps [0, 15]; we clamp here too so
  // persisted / race-condition writes can't poison the store.
  levelerMaxGainDb: number;
  setLevelerMaxGainDb: (db: number) => void;
  // Meter telemetry pushed from the server's TxMetersService over WS (0x16 v2).
  // Defaults look "quiet": 0 W forward/reflected, 1.0 SWR (matched), -100 dBfs
  // mic (near silence) so the SMeter/dBfs readouts don't spike on first paint.
  //
  // micDbfs is client-driven: set by the mic-uplink worklet's per-block peak
  // so the MicMeter animates even during RX. setMeters does not overwrite it.
  // wdspMicPk is server-driven: WDSP TXA_MIC_PK (post-panel-gain) carried in
  // TxMetersFrame.MicPk at 10 Hz during MOX; −Infinity when idle.
  fwdWatts: number;
  refWatts: number;
  swr: number;
  micDbfs: number;
  wdspMicPk: number;
  micAv: number;
  eqPk: number;
  eqAv: number;
  lvlrPk: number;
  lvlrAv: number;
  lvlrGr: number;
  cfcPk: number;
  cfcAv: number;
  cfcGr: number;
  compPk: number;
  compAv: number;
  alcPk: number;
  alcAv: number;
  alcGr: number;
  outPk: number;
  outAv: number;
  setMeters: (m: TxMeters) => void;
  setMicDbfs: (dbfs: number) => void;
  // Surfaced when getUserMedia / AudioWorklet init fails so MicMeter can
  // render a "click to enable" / "permission denied" hint instead of a
  // silent dead bar. null when mic capture is running normally.
  micError: string | null;
  setMicError: (msg: string | null) => void;
  // RX S-meter reading in dBm (MsgType.RxMeter / 0x14 pushed at ~5 Hz from
  // DspPipelineService). −160 floor matches the server's clamp so the SMeter
  // component never has to reason about -inf / tiny doubles.
  rxDbm: number;
  setRxDbm: (dbm: number) => void;
  // PRD FR-6: SWR trip alert. Server emits an AlertFrame (0x13) when SWR > 2.5
  // sustained ≥500 ms. Dismissable amber banner in UI; sticks until dismissed.
  alert: Alert | null;
  setAlert: (a: Alert | null) => void;
  // HL2 PA temperature (°C), from MsgType 0x17 at 2 Hz. Server clamps to
  // [-40, 125]. null means "no reading yet" (server hasn't sampled or we
  // haven't connected). Transient per-session — not persisted.
  paTempC: number | null;
  setPaTempC: (c: number) => void;

  // ---- PureSignal (predistortion) — MsgType 0x18 at 10 Hz when armed.
  // psEnabled is the master arm; not persisted (parity with MOX).
  // psAuto / psSingle are the cal-mode select. The advanced fields are
  // persisted because they're per-rack tuning the operator dials in once
  // and rarely revisits.
  psEnabled: boolean;
  setPsEnabled: (on: boolean) => void;
  psAuto: boolean;
  setPsAuto: (on: boolean) => void;
  psSingle: boolean;
  setPsSingle: (on: boolean) => void;
  psPtol: boolean;
  setPsPtol: (on: boolean) => void;
  psAutoAttenuate: boolean;
  setPsAutoAttenuate: (on: boolean) => void;
  psMoxDelaySec: number;
  setPsMoxDelaySec: (s: number) => void;
  psLoopDelaySec: number;
  setPsLoopDelaySec: (s: number) => void;
  psAmpDelayNs: number;
  setPsAmpDelayNs: (ns: number) => void;
  psHwPeak: number;
  setPsHwPeak: (p: number) => void;
  // Per-board factory default resolved by the server at connect time. UI
  // compares psHwPeak against this to show a "differs from default" hint.
  // mi0bot ref: PSForm.cs:830 pbWarningSetPk.Visible = _PShwpeak !=
  // HardwareSpecific.PSDefaultPeak.
  psHwPeakDefault: number;
  psIntsSpiPreset: string;
  setPsIntsSpiPreset: (p: string) => void;
  // Feedback antenna source — Internal coupler (default) or External
  // (Bypass). On G2/MkII this flips one ALEX bit; WDSP cal/iqc are
  // unaffected. The HW-Peak slider stays shared across sources to match
  // pihpsdr/Thetis behaviour.
  psFeedbackSource: 'internal' | 'external';
  setPsFeedbackSource: (s: 'internal' | 'external') => void;
  // PS-Monitor (issue #121) — operator-facing "Monitor PA output" toggle.
  // When on AND PS armed AND PS converged, the TX panadapter source flips
  // from the predistorted TX-IQ analyzer to the PS-feedback analyzer
  // (post-PA loopback). Default off; not persisted (parity with psEnabled
  // — viewing preference, resets each session). Hidden / disabled in the
  // UI on boards with no PS feedback path (e.g. HermesLite2).
  psMonitorEnabled: boolean;
  setPsMonitorEnabled: (on: boolean) => void;
  // TX Monitor (issue #106 follow-up) — audition toggle that engages a
  // parallel demod of the post-CFIR TX IQ. Substitutes for RX audio in the
  // AudioFrame stream while on. Operator preference, default off, not
  // persisted across sessions. UI lives in the VST Host submenu, not the
  // main GUI (per maintainer rule).
  txMonitorEnabled: boolean;
  setTxMonitorEnabled: (on: boolean) => void;
  // Live readout pushed via MsgType.PsMeters (0x18) at 10 Hz when armed.
  psFeedbackLevel: number;
  psCorrectionDb: number;
  psCalState: number;
  psCorrecting: boolean;
  psMaxTxEnvelope: number;
  // Hydrated from server state — true when calcc has been alive (PS armed +
  // keyed) for >5 s without producing a fit. Drives the HW-peak warning
  // banner in the PURESIGNAL panel. Server-side detection in
  // PsAutoAttenuateService stall path.
  psCalibrationStalled: boolean;
  setPsMeters: (m: {
    feedbackLevel: number;
    correctionDb: number;
    calState: number;
    correcting: boolean;
    maxTxEnvelope: number;
  }) => void;

  // ---- Two-tone test generator (TXA PostGen mode=1; protocol-agnostic).
  twoToneOn: boolean;
  setTwoToneOn: (on: boolean) => void;
  twoToneFreq1: number;
  setTwoToneFreq1: (hz: number) => void;
  twoToneFreq2: number;
  setTwoToneFreq2: (hz: number) => void;
  twoToneMag: number;
  setTwoToneMag: (m: number) => void;

  // ---- CFC (Continuous Frequency Compressor) — issue #123. Whole config
  // travels as one object so the panel's per-band edits + master toggles
  // round-trip atomically. Persisted via partialize so a reload reads the
  // same UI state without waiting for the server hydrate. Hydrated from
  // the server's StateDto.cfc on connect — the server is the source of
  // truth (LiteDB-backed) so a fresh browser sees the operator's last
  // dial-in.
  cfcConfig: CfcConfigDto;
  setCfcConfig: (cfg: CfcConfigDto) => void;

  // Hydrate the persistable PS / TwoTone fields from the server's StateDto.
  // Called from ConnectPanel and App.tsx alongside connection-store.applyState
  // so a fresh browser (no localStorage) sees the operator's last persisted
  // dial-in instead of the hard-coded defaults. Master-arm fields are
  // intentionally NOT hydrated — the operator must re-arm each session.
  hydrateFromState: (s: RadioStateDto) => void;
};

export const useTxStore = create<TxState>()(
  persist(
    (set) => ({
      moxOn: false,
      setMoxOn: (on) => set(on ? { moxOn: true, tunOn: false } : { moxOn: false }),
      localMicArmed: false,
      setLocalMicArmed: (on) => set({ localMicArmed: on }),
      tunOn: false,
      setTunOn: (on) => set(on ? { tunOn: true, moxOn: false } : { tunOn: false }),
      drivePercent: 10,
      setDrivePercent: (p) => set({ drivePercent: p }),
      tunePercent: 10,
      setTunePercent: (p) => set({ tunePercent: p }),
      micGainDb: 0,
      setMicGainDb: (db) => set({ micGainDb: db }),
      levelerMaxGainDb: 5,
      setLevelerMaxGainDb: (db) =>
        set({ levelerMaxGainDb: Math.max(0, Math.min(15, db)) }),
      fwdWatts: 0,
      refWatts: 0,
      swr: 1.0,
      micDbfs: -100,
      wdspMicPk: -Infinity,
      micAv: -Infinity,
      eqPk: -Infinity,
      eqAv: -Infinity,
      lvlrPk: -Infinity,
      lvlrAv: -Infinity,
      lvlrGr: 0,
      cfcPk: -Infinity,
      cfcAv: -Infinity,
      cfcGr: 0,
      compPk: -Infinity,
      compAv: -Infinity,
      alcPk: -Infinity,
      alcAv: -Infinity,
      alcGr: 0,
      outPk: -Infinity,
      outAv: -Infinity,
      setMeters: (m) => set({
        fwdWatts: m.fwdWatts,
        refWatts: m.refWatts,
        swr: m.swr,
        wdspMicPk: m.micPk,
        micAv: m.micAv,
        eqPk: m.eqPk,
        eqAv: m.eqAv,
        lvlrPk: m.lvlrPk,
        lvlrAv: m.lvlrAv,
        lvlrGr: m.lvlrGr,
        cfcPk: m.cfcPk,
        cfcAv: m.cfcAv,
        cfcGr: m.cfcGr,
        compPk: m.compPk,
        compAv: m.compAv,
        alcPk: m.alcPk,
        alcAv: m.alcAv,
        alcGr: m.alcGr,
        outPk: m.outPk,
        outAv: m.outAv,
      }),
      setMicDbfs: (dbfs) => set({ micDbfs: dbfs }),
      micError: null,
      setMicError: (msg) => set({ micError: msg }),
      rxDbm: -160,
      setRxDbm: (dbm) => set({ rxDbm: dbm }),
      alert: null,
      setAlert: (a) => set({ alert: a }),
      paTempC: null,
      setPaTempC: (c) => set({ paTempC: c }),

      // PureSignal — psEnabled / psSingle / live read-out are NOT persisted;
      // operator must re-arm each session.
      psEnabled: false,
      setPsEnabled: (on) => set({ psEnabled: on }),
      psAuto: true,
      setPsAuto: (on) => set({ psAuto: on }),
      psSingle: false,
      setPsSingle: (on) => set({ psSingle: on }),
      psPtol: false,
      setPsPtol: (on) => set({ psPtol: on }),
      psAutoAttenuate: true,
      setPsAutoAttenuate: (on) => set({ psAutoAttenuate: on }),
      psMoxDelaySec: 0.2,
      setPsMoxDelaySec: (s) => set({ psMoxDelaySec: s }),
      psLoopDelaySec: 0,
      setPsLoopDelaySec: (s) => set({ psLoopDelaySec: s }),
      psAmpDelayNs: 150,
      setPsAmpDelayNs: (ns) => set({ psAmpDelayNs: ns }),
      psHwPeak: 0.4072,
      setPsHwPeak: (p) => set({ psHwPeak: p }),
      // Pre-connect default mirrors PsHwPeak; ApplyPsHwPeakForConnection on
      // the server will push the per-board value into the StateDto, and
      // hydrateFromState below picks it up. mi0bot PSForm.cs:830 ref.
      psHwPeakDefault: 0.4072,
      psIntsSpiPreset: '16/256',
      setPsIntsSpiPreset: (p) => set({ psIntsSpiPreset: p }),
      psFeedbackSource: 'internal',
      setPsFeedbackSource: (s) => set({ psFeedbackSource: s }),
      psMonitorEnabled: false,
      setPsMonitorEnabled: (on) => set({ psMonitorEnabled: on }),
      txMonitorEnabled: false,
      setTxMonitorEnabled: (on) => set({ txMonitorEnabled: on }),
      psFeedbackLevel: 0,
      psCorrectionDb: 0,
      psCalState: 0,
      psCorrecting: false,
      psMaxTxEnvelope: 0,
      psCalibrationStalled: false,
      setPsMeters: (m) => set({
        psFeedbackLevel: m.feedbackLevel,
        psCorrectionDb: m.correctionDb,
        psCalState: m.calState,
        psCorrecting: m.correcting,
        psMaxTxEnvelope: m.maxTxEnvelope,
      }),

      // Two-tone — defaults match pihpsdr.
      twoToneOn: false,
      setTwoToneOn: (on) => set({ twoToneOn: on }),
      twoToneFreq1: 700,
      setTwoToneFreq1: (hz) => set({ twoToneFreq1: hz }),
      twoToneFreq2: 1900,
      setTwoToneFreq2: (hz) => set({ twoToneFreq2: hz }),
      twoToneMag: 0.49,
      setTwoToneMag: (m) => set({ twoToneMag: m }),

      // CFC — defaults mirror CfcConfig.Default on the server (master OFF,
      // pihpsdr-classic 10-band split). The server is authoritative and
      // hydrates over this on connect.
      cfcConfig: CFC_CONFIG_DEFAULT,
      setCfcConfig: (cfg) => set({ cfcConfig: cfg }),

      hydrateFromState: (s) =>
        set({
          // Drive sliders — server is the source of truth. Persisted via
          // RadioStateStore and broadcast on every SetDrive/SetTuneDrive so
          // the operator's last-set values come back on relaunch. The
          // localStorage mirror in `partialize` below exists only to avoid
          // a first-paint flicker before this hydrate runs.
          drivePercent: s.drivePercent,
          tunePercent: s.tunePercent,
          // Mic gain + Leveler max-gain — server-authoritative (RadioStateStore).
          // Previously localStorage-only and reverted on every desktop relaunch.
          micGainDb: s.micGainDb,
          levelerMaxGainDb: s.levelerMaxGainDb,
          psAuto: s.psAuto,
          psPtol: s.psPtol,
          psAutoAttenuate: s.psAutoAttenuate,
          psMoxDelaySec: s.psMoxDelaySec,
          psLoopDelaySec: s.psLoopDelaySec,
          psAmpDelayNs: s.psAmpDelayNs,
          psIntsSpiPreset: s.psIntsSpiPreset,
          psFeedbackSource: s.psFeedbackSource,
          // mi0bot ref: PSForm.cs:830 — per-board factory default frozen by
          // the server in ApplyPsHwPeakForConnection. Hydrated alongside the
          // operator-tuned psHwPeak so the UI sees a coherent pair.
          psHwPeak: s.psHwPeak,
          psHwPeakDefault: s.psHwPeakDefault,
          psCalibrationStalled: s.psCalibrationStalled ?? false,
          twoToneFreq1: s.twoToneFreq1,
          twoToneFreq2: s.twoToneFreq2,
          twoToneMag: s.twoToneMag,
          cfcConfig: s.cfc,
        }),
    }),
    {
      name: 'zeus-tx',
      // Persist only operator-tuning fields. Master arm bits (psEnabled,
      // twoToneOn, mox/tun) are transient per-session.
      partialize: (s) => ({
        drivePercent: s.drivePercent,
        tunePercent: s.tunePercent,
        micGainDb: s.micGainDb,
        levelerMaxGainDb: s.levelerMaxGainDb,
        // PS tuning is persisted server-side too, but we mirror it here so
        // the slider seeks don't flicker on first paint after a reload.
        psAuto: s.psAuto,
        psPtol: s.psPtol,
        psAutoAttenuate: s.psAutoAttenuate,
        psMoxDelaySec: s.psMoxDelaySec,
        psLoopDelaySec: s.psLoopDelaySec,
        psAmpDelayNs: s.psAmpDelayNs,
        psHwPeak: s.psHwPeak,
        psIntsSpiPreset: s.psIntsSpiPreset,
        psFeedbackSource: s.psFeedbackSource,
        twoToneFreq1: s.twoToneFreq1,
        twoToneFreq2: s.twoToneFreq2,
        twoToneMag: s.twoToneMag,
        // CFC tuning — persisted client-side too so the panel paints with
        // the operator's last config before the server hydrate lands.
        cfcConfig: s.cfcConfig,
      }),
    },
  ),
);
