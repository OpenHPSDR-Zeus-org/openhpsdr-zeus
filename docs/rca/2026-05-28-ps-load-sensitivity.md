# RCA: PureSignal quality is load-sensitive in Zeus (and isn't in Thetis / piHPSDR / deskHPSDR)

**Date:** 2026-05-28
**Branch:** `develop`
**Issue:** [#559](https://github.com/Kb2uka/openhpsdr-zeus/issues/559) (GitHub; `bd` re-file pending — see §10)
**Reporter:** KB2UKA, on-air observation across multiple sessions

> **Status:** investigation / findings. **No code changes proposed in this RCA.** Several of the structural changes implied here are red-light per CLAUDE.md (new threads, lock/scheduling restructure, DSP-engine signal routing) and need maintainer review before any of them turn into PRs.

## 1. Summary

PureSignal in Zeus produces a visibly cleaner TX signal when the operator runs Zeus in **web mode** (Vite + browser on one machine, backend headless on another) than when running in **desktop mode** (Photino webview + backend on the same Mac). Reference clients on the same radio — Thetis (Windows), piHPSDR (Linux), deskHPSDR (Heiko's fork) — do not show this load-sensitivity. **The load-sensitivity itself is the bug indicator**: a correctly-isolated PS feedback path should produce the same correction quality whether the host is rendering a UI or not.

A read-only comparison of the two feedback paths shows Zeus's PS hot path is **not isolated** from the rest of the process:

- The Zeus PS feedback path is end-to-end **managed C# code** allocating heap on every block.
- Threads carrying PS feedback samples run at **default OS priority**.
- A managed lock (`_psLock`) is **held across the `psccF` P/Invoke** *and* across `SetPSControl` from the state-change thread — they can block each other.
- TX-reference IQ and RX-feedback IQ reach calcc from **two independent threads** with no explicit sample-time coupling beyond WDSP's internal state.

In Thetis the same logical path is **entirely native code** (ChannelMaster + wdsp.dll), driven from the **ASIO driver callback** (real-time-priority thread Zeus cannot match on macOS), through **lock-free ring buffers**, with `Interlocked*` atomics on the MOX/runcal flags and zero managed allocation per block. Whether the Windows UI is rendering or not is invisible to PS.

This is why remote/web mode hides the problem: Photino is the dominant additional load on the shack Mac in desktop mode. Take it out of the picture and Zeus's already-fragile path stops being elbowed enough to visibly degrade `calcc`'s polynomial fit. The fragility is still there; it just isn't tripped.

## 2. Symptom (as observed)

- Same radio (ANAN-G2), same antenna, same band, same drive level.
- Run Zeus locally with `--desktop` (Photino webview + backend on the shack Mac). PS armed. PS converges, but shoulders on a monitor receiver are visibly higher than the operator gets from Thetis or piHPSDR on the same hardware.
- Run Zeus headless on the shack Mac (`OpenhpsdrZeus` without `--desktop`), browse the UI from a different machine. PS converges visibly cleaner — closer to what the reference clients produce.

The operator has years of cross-client experience and a calibrated reference for "what good PS looks like on this radio." This is qualitative but it is not noise.

## 3. Why this is a wiring problem, not a tuning problem

Three claims, in order:

1. **PS exists to correct PA non-linearity.** It does this by comparing the pristine TX baseband (the reference) to what comes back from the radio's feedback ADC (the observed), building an inverse model, and applying it to the TX IQ.

2. **A correctly-wired PS does not care about the shape of the mic-chain audio feeding TX.** The reference signal it sees is the post-WDSP-TX baseband, which already incorporates whatever EQ / CFC / leveler / compander the operator has dialed in. If those processors are hot, PS sees the hot version on *both* sides of the comparison; the fit still converges.

3. **A correctly-wired PS does not degrade under local UI load.** This is empirical: Thetis under heavy WinForms render load, piHPSDR under a busy SDR-Console display, deskHPSDR with multiple panadapter windows open — none of them produce visibly dirtier TX when the UI gets busier. They isolate PS from UI work at the architecture level.

If (1) and (2) are correct and Zeus visibly violates (3), then by elimination Zeus's PS *implementation* — specifically how the feedback path is plumbed in the .NET process — is the variable. Not mic chain, not band, not drive, not radio, not operator settings.

## 4. Side-by-side path comparison

Both clients ultimately call the same WDSP function: `psccF(channel, size, ItxBuff, QtxBuff, IrxBuff, QrxBuff, mox, solidmox)`. Everything *between the wire and that call site* is different.

### 4.1 Feedback receive path

| Axis | Thetis | Zeus |
|---|---|---|
| Source thread | Native ASIO driver callback (Windows real-time scheduling class) | Managed UDP receive loop (P1: dedicated `Zeus.Protocol1.Rx` thread at default priority; P2: `Task.Run` on the .NET thread pool) |
| Thread priority | Driver-priority on the audio thread; PS state-machine thread is `AboveNormal`; process can be `AboveNormal` | **None.** No `Thread.Priority`, no MMCSS, no QoS — anywhere on the path |
| Decode path | Native `cmaster.dll` parses, writes into **lock-free ring buffer** (`ring.h`, volatile pointers, no mutex) | Managed C# decode in `Protocol1Client.HandlePs4DdcPacket` / `Protocol2Client.HandlePsPairedPacket` |
| Buffer ownership | Native, ring-owned, no per-block heap allocation | **New `float[1024] × 4` allocated per PS block** in the protocol client (Protocol1Client.cs:385-388, Protocol2Client.cs:1699-1702) |
| Crossing to WDSP | Direct in-process native call inside WDSP's TX chain | Synchronous P/Invoke (`NativeMethods.psccF`) from `WdspDspEngine.FeedPsFeedbackBlock` |
| Allocations on second hop | None | `ToArray()` on each of the four buffers (WdspDspEngine.cs:2063-2066) — another `float[1024] × 4` even though the caller already owns float[] |
| Concurrency boundary count | One (ASIO callback → WDSP, both native, same thread) | Three (socket → protocol-client decode thread → DspPipelineService sink → WdspDspEngine → P/Invoke), all in managed code |

At 192 kHz feedback rate (Orion-MkII default), one PS block lands every ~5.3 ms. The protocol-client emission and the engine `ToArray()` together allocate roughly **two layers of `float[1024] × 4` per block** — on the order of **multiple megabytes per second of managed heap pressure**, just from PS feedback, before Photino, the panadapter, or the audio fan-out add anything. Thetis allocates zero per block in steady state.

### 4.2 TX-IQ reference path

| Axis | Thetis | Zeus |
|---|---|---|
| Where calcc gets the reference | Inside WDSP, at the call site where `psccF` is invoked from the TXA chain. TX-ref and RX-feedback are tightly coupled at the same call. | `ProcessTxBlock` (TX ingest thread) runs `fexchange2`; `FeedPsFeedbackBlock` (RX thread) calls `psccF`. They are two threads driving the same WDSP TXA channel asynchronously. |
| Synchronization | Implicit — single call site | None visible at the C# level beyond `_txaLock` (held briefly for channel-id reads). WDSP's internal locking, if any, is opaque to Zeus. |
| Coupling risk | None | If TX block cadence and RX feedback cadence drift, calcc may compute against an aged reference. Not observed to be catastrophic — calcc is a sliding-window fit — but not as tight as Thetis's structure. |

### 4.3 calcc arming / disarming

| Axis | Thetis | Zeus |
|---|---|---|
| Lifecycle thread | Dedicated PS thread (`PSForm.cs:137-148`) at `ThreadPriority.AboveNormal`, 10 ms tick | `DspPipelineService.SetPsEnabled` runs on the state-change handler thread (ThreadPool); `WdspDspEngine.SetPsEnabled` runs synchronously on the caller |
| MOX→PS coupling | `SetPSMox` flips an atomic flag; `pscc` sees it at the next sample window | Same mechanism, but the surrounding C# state-change path includes a `Task.Delay(100).Wait()` synchronous block (DspPipelineService.cs:781) |
| Concurrency guard | `Interlocked*` atomics on the hot flags; `EnterCriticalSection` (spin-count 2500) only at metadata access in calcc | `lock (_psLock)` is held **for the full duration of `psccF`** *and* for the full duration of `SetPSControl` / `SetPSEnabled`. These two callers can block each other. |
| Re-entrancy | Hardened in native code by the state machine | Recent PRs (#293, #341, #558) have repeatedly patched arm/disarm wedges — indicating the state surface around this lock is fragile in ways the native version isn't |

## 5. Root causes — ranked by likely contribution

1. **Managed-heap allocation on the PS hot path.** Multiple megabytes per second of `float[1024]` garbage, every second the operator transmits with PS armed. Each GC pass freezes the same threads that are pumping feedback into `calcc`. When Photino's webview thread is also competing for cycles and allocating, GC cadence becomes uneven, and `calcc`'s sample windows become uneven with it. This is the single biggest structural divergence from Thetis.

2. **No thread-priority isolation.** Zeus's PS feedback thread runs at exactly the priority Photino's render thread runs at — both default. Thetis's PS-bearing thread is the ASIO driver thread (real-time class). The shack Mac's scheduler has no instruction to keep the PS thread ahead of the UI thread; given the GC pressure in (1), the OS routinely makes the wrong choice.

3. **`_psLock` held across both `psccF` and `SetPSControl`.** The lock is acquired by the RX feedback thread for the duration of every block's P/Invoke (WdspDspEngine.cs:2068-2081) *and* by the state-change thread for the duration of every arm/disarm cycle (SetPsEnabled). Either side can starve the other. Recent PS PRs have been patching the symptoms (arm-during-MOX deferral, drain-skip if MOX-on, watchdog reset) without addressing this underlying lock shape.

4. **`Task.Delay(100).Wait()` synchronous block** on the state-change path (DspPipelineService.cs:781). Anti-pattern; blocks the state-change thread for 100 ms during arm. Not the steady-state problem but contributes to fragile MOX-edge behavior, which the recent PRs document.

5. **Decoupled TX-reference and RX-feedback threads.** `ProcessTxBlock` and `FeedPsFeedbackBlock` are two independent producers into the same WDSP TXA channel. Thetis collapses them into a single native call site. Probably not catastrophic on its own — calcc is forgiving — but it widens the window during which the other causes can do damage.

## 6. Why this manifests as "desktop dirty, web clean"

Each of (1)-(5) above is present whether the operator runs `--desktop` or `--server`. What changes between modes is **how much load is on the shack Mac**.

- **Desktop mode**: Photino webview runs on the shack Mac. WebKit renders the panadapter, the waterfall, the meters, the VFO. WebKit allocates. WebKit's UI thread competes with Zeus's PS thread for CPU. The shack Mac also drives the speaker/monitor output WASAPI.
- **Web mode (browser remote)**: The shack Mac runs only the backend. The panadapter, waterfall, meters render on a different machine. Browser allocation and render load lands on the *Air's* CPU, not the mini's.

In desktop mode the existing fragility gets tripped often enough to be visible. In web mode the same fragility is still there but the trigger conditions don't fire enough to show. The web/desktop delta is a *measurement* of how close to the edge Zeus's PS path is sitting all the time.

## 7. What would actually need to change (directions, not commitments)

None of these are PR-ready. All of them are at minimum architecture-level and need Brian's sign-off. Listed roughly in order of likely impact:

1. **Eliminate steady-state allocation on the PS feedback path.** Pool `float[1024]` buffers in `Protocol1Client` / `Protocol2Client` and accept `Span<float>` (or pooled arrays + a return contract) all the way through to `psccF`. Drop the `ToArray()` in `WdspDspEngine.FeedPsFeedbackBlock`. This is the change that matches Thetis's "zero allocation in steady state" most directly.

2. **Boost the PS-bearing thread priority.** On macOS this is `pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE)` or similar real-time class via `mach_thread_policy`. On Windows it's MMCSS `AvSetMmThreadCharacteristics(L"Pro Audio")` per Thetis-equivalent. On Linux it's `SCHED_FIFO` with `chrt`. This is per-platform native interop and warrants its own design pass.

3. **Shrink `_psLock` scope so it doesn't span the P/Invoke.** Either (a) replace with `Interlocked*` atomics on the few fields that genuinely need cross-thread coordination, matching what calcc.c does internally, or (b) split into a fast-path lock (held briefly for state read) and a control-path lock (held during arm/disarm). Recent PRs have been adding layers around this lock; the right move is to make the lock smaller.

4. **Make the state-change path async end-to-end** — kill the `Task.Delay(100).Wait()`. Standard async hygiene, low risk on its own.

5. **Re-examine TX-reference / RX-feedback coupling.** Lowest leverage of the five. Probably not worth touching until (1)-(4) land and a residual delta is still observed against Thetis.

## 8. What this RCA does *not* claim

- It does not claim a single one of the five root causes is **the** cause. The argument is structural: Zeus's path is fragile on multiple axes; in desktop mode multiple of them fire at once.
- It does not claim parity with Thetis is achievable without native-interop work (priority/QoS) that Zeus does not currently do on any platform.
- It does not claim the operator has measured the splatter quantitatively. The symptom is "the trained ear of a long-time PS operator across multiple reference clients hears the difference."
- It does not propose specific PRs. Each of the directions in §7 is its own design conversation.

## 9. Scope / what we read

- **Zeus**: `Zeus.Protocol1/Protocol1Client.cs` (RX loop + PS 4-DDC accumulator), `Zeus.Protocol2/Protocol2Client.cs` (PS paired-packet path), `Zeus.Server.Hosting/DspPipelineService.cs` (PS state plumbing, `OnPsFeedbackFrame`, `SetPsEnabled`), `Zeus.Dsp/Wdsp/WdspDspEngine.cs` (`FeedPsFeedbackBlock`, `SetPsEnabled`, `_psLock` / `_txaLock`), `Zeus.Dsp/Wdsp/NativeMethods.cs` (P/Invoke surface for `psccF`/`SetPSControl`/`SetPSRunCal`). Recent PRs read: #293, #341, #556, #557, #558.
- **Thetis** (`/Users/kb2uka_mac/Programs/Thetis`): `Project Files/Source/Console/PSForm.cs` (PS state machine + thread), `Project Files/Source/Console/console.cs` (MOX sequencing), `Project Files/Source/wdsp/calcc.c` and `calcc.h` (state machine + `psccF`), `Project Files/Source/ChannelMaster/ring.h` (lock-free ring), `Project Files/Source/cmASIO/hostsample.cpp` (ASIO callback).
- We did **not** read piHPSDR or deskHPSDR source for this pass. If the maintainer wants a second-axis check on the structural argument, those would be useful — but the Thetis comparison alone is enough to establish "reference clients isolate PS from UI load; Zeus doesn't."

## 10. Issue text (for `bd create`)

Filed on GitHub as [Kb2uka/openhpsdr-zeus#559](https://github.com/Kb2uka/openhpsdr-zeus/issues/559) on 2026-05-28; `bd` was not installed in the shell at the time. The block below is the canonical text to re-file into `bd` once it's set up, cross-referencing #559.

```yaml
title: PureSignal quality is load-sensitive in Zeus (reference clients aren't)
type: bug
priority: high
acceptance_criteria: |
  - Document the symptom and the structural comparison against Thetis (this RCA: docs/rca/2026-05-28-ps-load-sensitivity.md).
  - Decide whether to address by (a) allocation/lock changes only, (b) add native thread-priority interop, or (c) both. Maintainer call.
  - If (a) is chosen: PS feedback path in steady state allocates zero managed heap; _psLock is no longer held across psccF P/Invoke; Task.Delay(100).Wait() on the state-change path is removed.
  - If (b) is chosen: PS-bearing thread is registered to the OS's real-time / pro-audio scheduling class on each supported platform (macOS QoS_USER_INTERACTIVE or mach realtime, Windows MMCSS Pro Audio, Linux SCHED_FIFO).
  - Re-test desktop vs web mode on KB2UKA's G2: the visible TX-cleanliness delta should be gone.
notes: |
  See docs/rca/2026-05-28-ps-load-sensitivity.md for the full path comparison.
  Red-light per CLAUDE.md — touches threading, locking, and DSP signal routing. Needs maintainer review before any sub-PR.
  Related landed PRs: #293, #341, #556, #557, #558 (all patched symptoms around the same lock surface).
```

Once `bd` is available again: `bd create` with the above, then update this RCA's front-matter with the issue ID and `bd dolt push origin main`.
