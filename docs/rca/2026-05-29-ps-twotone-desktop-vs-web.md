# RCA — Two-tone / PureSignal clean in web, dirty in desktop (G2, Protocol 2)

**Date:** 2026-05-29
**Board:** ANAN-G2 (OrionMkII, Protocol 2) + RF2K-S, external feedback tap
**Issue:** #559 (PS quality) / related to PR #565 (two-tone)
**Status:** Root cause identified (architectural). NOT fixed. Speculative
thread-priority attempts tried and reverted. Workaround: operate in web mode.

## 1. Symptom

On the G2, with identical code (`feat/imd-measurement-window` = PR #565
two-tone fixes + IMD-measurement overlay):

- **Web mode** (Vite :5173 + standalone backend :6060): two-tone is clean,
  PureSignal corrects, SSB clean at 1 kW (tiny splatter at 1400 W = separate
  headroom item).
- **Desktop mode** (Photino `--desktop`): two-tone is dirty, IMD not corrected,
  calcc stalls. Same radio, same code, same operator settings.

## 2. Root cause (repeatable, high-confidence)

**It's the process architecture, not the DSP/PS code.**

- **Web mode = two processes.** The browser renders the panadapter/waterfall
  (WebGL) in its own OS process; the backend (WDSP DSP + TX pump + P2 UDP
  sender + RX/PS-feedback loop) runs alone in the dotnet process. The OS
  schedules the backend's TX-feed threads cleanly → the radio's TX DUC FIFO
  gets a smooth, on-time 192 kHz stream → clean two-tone, and calcc gets a
  clean feedback timeline → PS converges.
- **Desktop mode = one process.** Photino hosts the WebView (Chromium/WebKit)
  **and** the backend in the **same process**. The WebView's render —
  especially the panadapter/waterfall WebGL redrawing during TX — contends for
  CPU with the TX feed threads inside that one process. PR #565's deadline pump
  keeps the *average* rate right (~825 pkts/s) but the **sub-block timing goes
  jittery**: the radio FIFO sees uneven delivery → gappy/dirty two-tone, and
  calcc's pscc state machine (driven off the RX-feedback thread) stalls — we
  observed it frozen in **LWAIT (state=1)** with `info[5]` pinned, which then
  trips the wedge watchdog into a **reset-storm** (`engine.ResetPs()` every 5 s),
  compounding the dirtiness.

This matches the pre-existing load-sensitivity RCA
(`2026-05-28-ps-load-sensitivity.md`): "desktop worse than web, structural."

## 3. The "Observed clipping / HW peak too low" banner is a SYMPTOM, not the cause

`zeus-web/src/components/PsSettingsPanel.tsx` (and `PsStatusPopover.tsx`) raise
"HW peak too low — Observed is clipping into the ADC ceiling" purely on
`psMaxTxEnvelope > psHwPeak`. `psMaxTxEnvelope` = WDSP `GetPSMaxTX` =
`env_maxtx`, a **high-water mark** that only resets on a calcc reset. During the
desktop stall/reset-storm a garbage/clipped TX-DAC loopback sample gets latched
(observed jumped to **0.734** vs hw_peak ~0.613) and **stays** there, so the
banner fires permanently. Raising hw_peak clears the banner but does nothing for
the signal — operator correctly observed "it wasn't too low." Do not chase
hw_peak for this; it's downstream of the stall.

## 4. What was tried and REVERTED (don't repeat blindly)

All on top of the baseline, all reverted back to `cb1252c`:

1. **Per-thread pro-audio QoS** (slice-2 `RealtimeThreadPriority`,
   `pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE)`): **macOS denied
   it — rc=1/EPERM.** macOS reserves USER_INTERACTIVE for the app's UI/WebView
   thread; background threads can't get it. Silent no-op → desktop unchanged.
2. **mach real-time** (`THREAD_TIME_CONSTRAINT_POLICY`, 1 ms/5 ms): **granted
   (rc=0)** — but it changed the TX sender's pacing for the worse: the dedicated
   sync sender + RT made `Thread.Sleep(1)` precise enough to **overfeed at
   967 pkts/s** (vs 800 target), over-filling the radio FIFO → still dirty, and
   the wedge reset-storm persisted. Net regression.
3. Converting `TxIqSenderLoop` to a dedicated synchronous thread + promoting
   `RxLoop` (LongRunning). Kept the structure but the priority couldn't be
   granted usefully and the pacing regressed; reverted with the rest.

Lesson (again): per-thread priority can't reliably win a CPU fight **inside one
process** against the WebView, and tuning RT pacing by trial over chat made it
worse. This is the logged anti-pattern — stop iterating blind.

## 5. The proper fix (architectural — decision required)

Make desktop mode behave like web: **run the backend in its own OS process** and
have the Photino native window connect to it over loopback — i.e. desktop =
"standalone backend process + thin native window," exactly the web topology that
is already clean. The WebView render then competes with nothing in the backend's
process and the OS schedules the DSP/TX threads independently.

This is a red-light architecture change (process model). Options to weigh:
- **A. Split process:** desktop launcher spawns `OpenhpsdrZeus --server` as a
  child process, waits for it to bind loopback, then opens the Photino window
  pointed at it. Cleanest; mirrors web exactly. Adds process lifecycle mgmt.
- **B. Keep one process but isolate the DSP/TX onto OS real-time threads done
  right** (mach RT with correct *periodic* parameters per thread, + leave the
  sender pacing untouched). Lower-confidence; tuning RT params on hardware is
  finicky (see §4.2).
- **C. Reduce WebView contention:** throttle/suspend the panadapter+waterfall
  WebGL render rate during TX in desktop mode. Cheap, partial; doesn't fix the
  calcc-feedback-thread stall, only the render load.

Recommendation: **A** (process split) — it's the only one that structurally
guarantees web-equivalent behavior, and it's verifiable (web already proves it).

## 6. Workaround (now)

Operate in **web mode** (`/run --mode=web`, browse `localhost:5173` on the mini)
— PS and two-tone are clean there today. Native desktop mic differs from the
browser mic path, but for two-tone (internally generated) there's no difference,
and SSB via the shack chain into the browser is the operator's call.

## 7. Scope read this session

Zeus: `Zeus.Server.Hosting/TxTuneDriver.cs` (deadline pump),
`Zeus.Protocol2/Protocol2Client.cs` (`TxIqSenderLoop`, `RxLoop`, `SendTxIq`,
`FlushTxIqLocked`), `Zeus.Dsp/Wdsp/WdspDspEngine.cs` (`ProcessTxBlock`,
`FeedPsFeedbackBlock`), `native/wdsp/{calcc.c,iqc.c,TXA.c}` (PS state machine +
iqc apply — confirmed iqc engages and reaches the wire), `PsAutoAttenuateService`
(wedge watchdog), `PsSettingsPanel.tsx` (banner source). PR #565 = the two-tone
sideband-sign + deadline-pump-pacing fix (clean in web). The 1400 W SSB splatter
is a separate PS-headroom follow-up.
