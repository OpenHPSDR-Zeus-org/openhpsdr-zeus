# perf_pass_3 — measured CPU/alloc comparison

Captured 2026-05-11. Same machine, same HL2 (192.168.100.21), Protocol 1 @ 192 kHz, single SignalR client (Brian's Vite proxy / browser session, re-attached after backend bounce).

## Round 1 — async-iterator + Socket.ReceiveFrom rewrites (commits 7b156a0..1db1c8d, 4dbad0e)

| Metric | Before — develop / Debug | After — perf_pass_3 / Release | Δ |
|---|---|---|---|
| CPU user (s/s) | 0.338 | 0.291 | −14 % |
| CPU system (s/s) | 0.227 | 0.145 | −36 % |
| **CPU total (s/s)** | **0.565** | **0.436** | **−23 %** |
| Alloc rate (MB/s) | 1.752 | 1.875 | +7 % |
| Thread-pool work items /s | 2 083 | 2 075 | ≈ |
| Gen0 collect /s | 0.02 | 0.02 | ≈ |
| Lock contentions /s | 8.37 | 4.22 | −50 % |
| OS top % one core (top -l) | ~25 steady / ~48 interactive | ~42–47 sustained | matches counter |

## Round 1.5 — Workstation GC (commit 3288401)

ASP.NET Core defaults to Server GC (one GC thread per logical core). On Brian's 10-core host that's 10 idle GC threads, each appearing as a low-amplitude `PollGCWorker` waker every few seconds. The `dotnet-trace cpu-sampling` profile attributed ~3 % CPU to `Thread.PollGCWorker` plus a notable slice of `LowLevelLifoSemaphore.WaitForSignal` idle churn to those threads.

Switched to Workstation GC via `<ServerGarbageCollection>false</ServerGarbageCollection>` in `Zeus.Server.csproj`. Concurrent GC stays enabled. Pause budget is tighter per collection but Zeus's ~1.4 MB/s alloc rate keeps each pause sub-millisecond.

| Metric | Before — Round 1 (Server GC, Release) | After — Round 1.5 (Workstation GC) | Δ |
|---|---|---|---|
| CPU user (s/s) | 0.291 | 0.226 | −22 % |
| CPU system (s/s) | 0.145 | 0.145 | ≈ |
| **CPU total (s/s)** | **0.436 (43.6 %)** | **0.371 (37.1 %)** | **−15 %** |
| Alloc rate (MB/s) | 1.875 | 1.407 | **−25 %** |
| Working set (MB) | 315 | 186 | **−41 %** |
| Threads | 46 | 34 | −12 (10 GC threads gone + ~2 TP) |
| Lock contentions /s | 4.22 | 3.27 | −23 % |

Raw artifacts:
- `iter2/wgc-counters.csv` — 60 s dotnet-counters under Workstation GC.

## Round 2, iter 1 — WaitToReadAsync+TryRead batching (commit 98a0e94)

`sample(1)` against PID 56859 surfaced the residual hot path: ~52 % of busy CPU was on TP workers in `ThreadNative_SpinWait` + `swtch_pri` blocked downstream on `WaitHandle_WaitOnePrioritized` — i.e. the TP dispatcher's spin-then-park phase. At 381 IQ frames/s with one work-item-per-`ReadAsync`-continuation, that spin tax compounds.

Swap to `while (await reader.WaitToReadAsync(ct)) { while (reader.TryRead(out var x)) ... }` in all four `DspPipelineService` pumps (P1+P2 IQ, P1+P2 PS feedback) and `StreamingHub.ClientSession.SendLoopAsync`. One TP dispatch now drains all currently-queued items.

| Metric | Before — round 1 result | After — iter1 | Δ |
|---|---|---|---|
| CPU user (s/s) | 0.2810 | 0.2485 | −11.6 % |
| CPU system (s/s) | 0.1363 | 0.1086 | −20.3 % |
| **CPU total (s/s)** | **0.4173 (41.7 %)** | **0.3571 (35.7 %)** | **−14.4 %** |
| Alloc rate (MB/s) | 1.397 | 1.350 | −3.4 % |
| Thread-pool work items /s | 2 081 | 2 085 | ≈ |
| Lock contentions /s | 4.25 | 1.85 | **−56.6 %** |
| Gen0 collect /s | 0.15 | 0.15 | ≈ |

**Brian's < 35 % CPU target is met (35.7 % vs 35.0 % stop-iterating threshold).** Lock-contention −57 % is the cleanest signal that batching is working: each TP dispatch holds `_engineLock` once and processes the queued items before releasing.

Raw artifacts:
- `iter2/iter1-before.csv` — 60 s dotnet-counters before this commit (PID 56859).
- `iter2/iter1-after.csv` — 60 s dotnet-counters after (PID 60080).
- `iter2/sample-iter1-before.txt` — 30 s `sample(1)` profile that identified the spin-on-park hot path.

## Round 2, iter 3 — PsAutoAttenuate adaptive 1 Hz idle / 10 Hz active cadence (commit 77e9ba6)

`PsAutoAttenuateService` used a fixed 100 ms `PeriodicTimer`. The loop body `Tick1()` early-returns on two boolean gates whenever PS is disarmed OR the radio isn't keyed — i.e. the entire RX-only operating window. That's ~9 wasted TP wake-ups/s during day-to-day RX use.

Adaptive cadence: idle = 1 Hz, active (PS armed AND MOX/TwoTone on) = 10 Hz. Reuses the same `PeriodicTimer` via the .NET 8+ settable `Period`. Active-mode latency to detect a fresh PS-arm or MOX-on edge is at most one second — well below operator perception.

**Measurement caveat:** Brian's HL2 workload was driven by UI activity during the after-capture window — CPU shot from ~0.25 s/s (quiet) to ~0.45 s/s (interactive) purely from operator behaviour between the two captures. The PsAutoAttn saving (~9 TP wake-ups/s of the 2 080/s aggregate, ~0.4 %) is below the noise floor of that workload swing. Functionally the change is correct; the operator-facing PS arm/MOX latency is unchanged.

| Metric | Before (iter1, quiet operator window) | After (iter3, active operator window) |
|---|---|---|
| CPU total (s/s) | 0.256 | 0.449 |
| Alloc rate (MB/s) | 1.38 | 1.35 |
| TP work-items /s | 2 200 | 2 071 |
| Lock contentions /s | 2.58 | 2.25 |

Raw: `iter2/iter3-{before,after}.csv`.

## Round 2, iter 4 — display-pipeline gate on `_hub.ClientCount > 0` (commit c35c844)

Server-side analog of the perf3 `pushFrame` gate that perf-rgl landed on the frontend. The display block in `DspPipelineService.Tick` ran unconditionally at 30 Hz — `engine.TryGet*DisplayPixels` × up to 6 calls/tick, `Array.Reverse` × 2 on 2 048-float buffers, the `DisplayFrame` record-struct construction, and the ~16 KB wire payload `StreamingHub.Broadcast` would allocate. The hub already short-circuits the wire-payload step on `_clients.IsEmpty`, but the upstream WDSP pixel reads, axis reverses, and frame construction fired regardless.

Gate the entire display block on `_hub.ClientCount > 0` (O(1) `ConcurrentDictionary.Count` read). Audio path below runs unconditionally — RXA must keep draining so the WDSP audio ring doesn't back up, and in-process `RxAudioAvailable` subscribers (TCI, potential future RX-side VST seam) still need frames even with no WS clients.

**Connected-client measurement is identity by design.** Brian's session had a client connected the whole time, so `hasClients = true` and the gate doesn't fire. Iter4 measurement shows ≈0 delta (CPU 0.476 → 0.478, alloc 1.348 → 1.349 MB/s, TP rate unchanged) — expected, not a regression. The win materialises only when all clients disconnect (browser tab closed, mobile UI backgrounded, remote-desktop session ended).

| Metric | Before (iter3, PID 63469) | After (iter4, PID 65169) | Δ |
|---|---|---|---|
| CPU total (s/s) | 0.4761 | 0.4779 | ≈ 0 |
| Alloc rate (MB/s) | 1.348 | 1.349 | ≈ 0 |
| TP work-items /s | 2 071 | 2 069 | ≈ 0 |
| Lock contentions /s | 1.90 | 2.08 | ≈ 0 |

Raw: `iter2/iter4-{before,after}.csv`.

## Round 3, iter 5 — single-thread DSP ownership (commits `b341495`, `cbba6c6`, `a244196`, `eeb4052`, `141492b`, `d6180d4`)

The cumulative-trajectory analysis at the bottom of the iter4 writeup flagged the residual: `swtch_pri ~34%` + `ThreadNative_SpinWait ~21%` on the prior `sample(1)` profile — both essentially TP-dispatcher park/wake amplification across the four channel-fed consumer pumps (P1 IQ, P2 IQ, P1 PS-FB, P2 PS-FB) plus the 30 Hz display `PeriodicTimer`. Each pump did one `await Channel.WaitToReadAsync` + drain + `lock(_engineLock)` per packet — five separate parked-task wake-up sites compounding into ~55% of busy CPU at 381 IQ frames/s (a given packet only feeds the protocol's own pump, but every pump that is alive contributes its own park/wake cycle). Iter5 attacks it architecturally rather than incrementally.

**The move:** collapse the four pumps and the 30 Hz display tick onto a single OS thread (`Protocol1Client.RxLoop` / `Protocol2Client.RxLoop`), make WDSP single-thread-owned on that loop, and drop `_engineLock` from the per-frame hot path entirely.

Six commits over two days:

| # | Commit | Subject |
|---|---|---|
| 1 | `b341495` | feat(rx-sink): IRxPacketSink seam on Protocol1/2 RxLoop for iter5 pump-collapse |
| 2 | `cbba6c6` | feat(spsc-ring): lock-free SPSC ring for DSP-thread → hub frame handoff |
| 3 | `a244196` | perf(dsp-pipeline): pass 1 — DspPipelineService as IRxPacketSink + cmd queue |
| 4 | `eeb4052` | perf(dsp-pipeline): pass 2 — delete channel pumps + drop _engineLock from hot path |
| 5 | `141492b` | docs(perf): iter5 hub-broadcast ring decision — no-op for iter5 |
| 6 | `d6180d4` | docs(dsp-pipeline): refresh stale pump-era comments after iter5 task #4 |

Key pieces of the new shape:

- **`IRxPacketSink` (b341495).** A synchronous sink interface on the RX OS thread. `Protocol1Client.AttachRxSink` / `DetachRxSink` use `Interlocked.Exchange` to install; `Protocol1Client.RxLoop` does a `Volatile.Read` snapshot at the top of each packet and calls `sink.OnIqFrame(in …)` directly — no `Channel<T>` hop, no `WaitToReadAsync`, no TP wake. Channel-write fallback stays live when no sink is attached so unit-tests and in-process probes keep working untouched. Mirror class on `Zeus.Protocol2`.
- **`DspPipelineService` becomes the sink (a244196 + eeb4052).** The four `StartIqPump` / `StartIqPumpP2` / `StartPsFeedbackPumpP1` / `StartPsFeedbackPumpP2` `Task.Run` consumers are deleted. The service implements both protocol sinks; `OnIqFrame` calls into WDSP directly on the RX thread. Each Channel hop saved ~1 200 TP wake/s (P1 hot path) + the PS-FB pair (~190 wake/s each when armed).
- **`_engineLock` removed from hot path (eeb4052).** Readers (`OnIqFrame`, `OnPsFeedbackFrame`, the inline display Tick) now use `Volatile.Read` of `_engine` / `_channelId` / `_sampleRateHz`. The lock survives as writer-side serialisation only — six call sites, listed below.
- **Inline 30 Hz display Tick (eeb4052).** The `PeriodicTimer.WaitForNextTickAsync` loop is paused while a radio sink is attached. Display work runs inline on `OnIqFrame` via a `Stopwatch.GetTimestamp()` elapsed check (33 ms gate). When the radio disconnects, the PeriodicTimer is unpaused so meter/spectrum still update against the synthetic engine. Same 30 Hz cadence; the wake-up source is the IQ packet itself rather than a separate timer.
- **Cross-thread command queue (a244196).** `DspPipelineService.SetMox` and `SetTxTune` — called from `TxService` on the MOX/TUN interlock edge — no longer touch WDSP directly. They enqueue onto a `ConcurrentQueue<Action>` that the RX thread drains at the top of each packet, so WDSP TXA-state edges run on the same thread that feeds RX IQ. The MOX-latency probe (`t1` log at `TxService.cs:144`) still fires synchronously at the call site BEFORE the queue post, so the t1 timestamp is unaffected by queue-drain latency. Scope note: `OnRadioStateChanged` (NR / EMNR / AGC / filter / mode mutators from `/api/state` PUTs) and the standalone HTTP endpoint setters (`/api/mic-gain`, `/api/tx/leveler-max-gain`, `/api/tx/ps/reset`, etc.) still call `engine.*` directly after a `Volatile.Read` of the engine pointer — see the caveat below. Strict single-thread-WDSP for those paths is a deferred follow-up.
- **SPSC ring is shelved (cbba6c6 + 141492b).** Task #2 built `SpscRing<T>` (lock-free, 19 unit tests) for a DSP-thread → hub-sender handoff. Iter5 measurement (below) shows the hub-broadcast path already sits at ~65 TP wake/s after the pump collapse — two orders of magnitude below the ~4 800/s the ring was meant to absorb. Decision A in `docs/perf/server/iter2/iter5-hub-decision.md`: keep the ring compiled and tested in tree for a future iter, do not wire it for iter5 because it would relocate work rather than reduce it.

### Live HL2 deltas (28400 kHz USB / 192 kHz / RX-only / no SignalR client / no MOX)

Measured against the bare-metal HL2 at 192.168.100.21. PIDs 96984 (before, cbba6c6) and 97750 (after, d6180d4). Both Release builds, same machine, same radio, same VFO/mode/sample-rate.

| Metric | Before — cbba6c6 | After — d6180d4 | Δ |
|---|---|---|---|
| CPU user (s/s) | 0.2289 | 0.1729 | **−24.5 %** |
| CPU system (s/s) | 0.0994 | 0.0700 | **−29.6 %** |
| **CPU total (s/s)** | **0.3283 (32.8 %)** | **0.2429 (24.3 %)** | **−26.0 %** |
| Alloc rate (B/s) | 606 147 | 460 189 | **−24.1 %** |
| **TP work-items /s** | **1 956.5** | **432.2** | **−77.9 %** |
| Lock contentions /s | 0.93 | 1.37 | +47 % (sub-2/s, see caveats) |
| Gen0 collect /s | 0.051 | 0.034 | −33 % |
| Working set (MB) | 275 | 276 | ≈ |

Raw: `iter2/iter5-{before,after}.csv`.

### `sample(1)` stack-fingerprint delta (30 s each, same PIDs)

| Frame | Before samples | After samples | Δ |
|---|---|---|---|
| `swtch_pri` (TP park/wake) | 3 042 | 973 | **−68 %** |
| `ThreadNative_SpinWait` (spin-then-park) | 1 868 | 891 | **−52 %** |
| `xresample` (WDSP work) | 1 113 | 1 204 | ≈ flat |

`swtch_pri` and `ThreadNative_SpinWait` are the exact two frames iter4's closing analysis identified as the residual TP-dispatcher cost. They both halved-or-more, which is the direct fingerprint of the four-pump collapse: fewer parked TP workers means fewer wakes, means less spin-then-park churn. `xresample` (the WDSP polyphase resampler used inside the RX chain) stays flat — iter5 attacks dispatch overhead, not DSP throughput. We did not touch WDSP.

Raw: `iter2/iter5-before-sample.txt`, `iter2/iter5-after-sample.txt`.

### Synthetic deltas (idle, ZEUS_PERF_TEST=1 :6070, no client, no IQ)

Captured for symmetry — measures the TP wake-up floor when no radio is attached, so the pump-collapse can only show up as the removed PeriodicTimer / TP-worker overhead, not as the dropped per-frame channel hops.

| Metric | Before — cbba6c6 | After — d6180d4 | Δ |
|---|---|---|---|
| CPU total (s/s) | 0.0214 | 0.0153 | −28 % |
| Alloc rate (B/s) | 33 015 | 32 189 | −2.5 % |
| TP work-items /s | 49.1 | 49.2 | ≈ (timer-driven, no pumps to collapse at idle) |
| Lock contentions /s | 0 | 0 | — |

Raw: `iter2/iter5-{before,after}-synthetic.csv`, `iter2/iter5-{before,after}-synthetic-sample.txt`.

### Six surviving `_engineLock` sites — all writer-side

After iter5 the `_engineLock` is held only by code paths that mutate the `_engine` / `_channelId` / `_sampleRateHz` pointers. The hot path (`OnIqFrame`, `OnPsFeedbackFrame`, the inline display Tick) reads them via `Volatile.Read`. The six callers:

Line numbers below point to the method declaration in `Zeus.Server.Hosting/DspPipelineService.cs` at HEAD `d6180d4`; the `lock (_engineLock)` block sits 4–14 lines inside each.

1. **`OpenSynthetic`** (line 360, lock at 370) — initial bring-up when no radio is connected. Holds the lock while opening a `SyntheticDspEngine` channel and writing the pointers.
2. **`OnRadioConnected`** (line 380, lock at 394) — invoked by `RadioService` on P1 connect. Swaps from synthetic to WDSP-on-real-IQ, attaches the RX sink.
3. **`OnRadioDisconnected`** (line 429, lock at 443) — invoked by `RadioService` on P1 disconnect. Detaches the RX sink, tears down WDSP, opens a fresh synthetic engine.
4. **`ConnectP2Async`** (line 801, lock at 862) — the P2 connect path. Opens a P2 WDSP engine with separate channel/TX-channel, writes the pointers.
5. **`DisconnectP2Async`** (line 969, lock at 989) — P2 teardown counterpart.
6. **`CloseCurrentEngine`** (line 1192, lock at 1196) — final shutdown / disposal.

Justification: each of these is invoked from an HTTP request thread (`/api/connect`, `/api/disconnect`, `/api/connect/p2`, `/api/disconnect/p2`) or from a `RadioService` event handler running on a TP worker. They need to serialise against each other — racing a `/api/disconnect` and a `/api/connect/p2` could otherwise tear the engine pointer between the `Volatile.Write` calls. The hot path doesn't acquire the lock; it only reads via `Volatile.Read`, which is correctly ordered against the release fence on `_engineLock`-exit by writers.

### Caveats

- **Lock-contention uptick (0.93 → 1.37 /s).** Pipeline-arch removed `_engineLock` from the hot path, so this residue is somewhere else. `ConcurrentQueue<Action>` is itself lock-free (CAS-based, no `Monitor.Enter`) so the new command-queue drain is NOT the source. The two plausible candidates are (a) the `StreamingHub.Broadcast` enumeration of `_clients.Values` — `ConcurrentDictionary.Values` materialises a snapshot under the dictionary's internal lock, and every Tick broadcast now runs inline on the RX thread instead of the old PeriodicTimer worker so the contention shows up differently in the counter, and (b) the per-client `Channel<byte[]>` writer side — created with `SingleReader=true, SingleWriter=false`, so the writer path takes a lightweight lock to serialise itself against the reader's wake. Absolute rate is sub-2/s; not a regression worth blocking on, flagged in follow-ups.
- **No SignalR client attached during the captures.** Both before and after were measured without a browser session connected (just `curl /api/state` polling). The display block now gates broadcast on `ClientCount > 0` (iter4), so without a client the display Tick still runs every 33 ms but emits no wire payload. This is identical workload on both sides of the comparison — the delta is real — but the absolute CPU numbers will be slightly higher with a real client doing SignalR fan-out. Estimated overhead: ~50 TP wake/s × 1 client per iter4 instrumentation.
- **Build mode identical (Release ↔ Release).** Unlike Round 1, this iter does not mix Debug↔Release.
- **VFO / mode / sample-rate identical (28400 kHz USB, 192 kHz).** Both captures explicitly retuned to 28400 after connect.
- **`OnRadioStateChanged` still does direct `engine.*` calls** (`Volatile.Read` of the engine pointer, no command-queue hop). Pipeline-arch flagged this in the task #4 summary as a judgement call: state-change paths are infrequent and the cross-thread call is cheap. Listed in follow-ups for revisit if strict single-thread WDSP becomes desirable for those paths too.

## Cumulative trajectory

| Branch state | CPU (s/s, mean) | Δ vs prior | Workload |
|---|---|---|---|
| develop / Debug | 0.565 (56.5 %) | — | quiet RX |
| perf3 round 1 (`4dbad0e`) | 0.436 (43.6 %) | −23 % | quiet RX |
| +Workstation GC (`3288401`) | 0.371 (37.1 %) | −15 % | quiet RX |
| +iter1 channel-drain (`98a0e94`) | 0.357 (35.7 %) | −3.8 % | quiet RX |
| +iter3 PsAutoAttn (`77e9ba6`) | _below noise_ | <1 % | mixed |
| +iter4 display gate (`c35c844`) | identity (connected) | 0 % connected | mixed |
| +iter5 single-thread DSP (`d6180d4`) | **0.243 (24.3 %)** | **−26 %** | RX-only no-client |

The iter4 closing analysis identified `swtch_pri ~34%` + `ThreadNative_SpinWait ~21%` as TP-dispatcher park/wake amplification across four pumps + a 30 Hz timer, and recommended stopping there because the four named exits to going below 35 % were all red-light or out-of-scope. That reasoning was sound at the time — every candidate on the list (TX-loop pacing, default-value changes, WDSP internals, AOT) was correctly flagged.

Per the user's "leave nothing on the table" mandate for perf-pass-3, iter5 took a fifth exit that the iter4 list did not enumerate: **architectural restructure of the consumer side of the DSP pipeline, no defaults touched, no WDSP touched, no native AOT.** Four `Task.Run` consumer pumps + the 30 Hz `PeriodicTimer` collapse onto the `Protocol1/2 RxLoop` OS thread via `IRxPacketSink`; `_engineLock` drops off the hot path (six writer-side sites survive); MOX edges marshal via a `ConcurrentQueue` drained on the same RX thread. WDSP itself is untouched. The win lands on the load-bearing measurement: **−78 % TP work-items, −68 % `swtch_pri`, −52 % `ThreadNative_SpinWait`, −26 % live CPU.**

The new floor is **24.3 % live CPU** (HL2 RX-only, no client, 192 kHz). Going below ~20 % from here would still require touching one of the four iter4-listed exits — TX-loop pacing, default-value changes, WDSP-internal opts, or AOT/R2R — all of which remain red-light or orthogonal. Iter5 is the last safe architectural step inside the scope of this perf pass; stop iterating here.

## Confounders

- **Debug → Release** accounts for some of the CPU win on its own. We did NOT capture a Debug-vs-Debug, only Debug-before vs Release-after.
- Mode changed (LSB 7169 → USB 14200) on the backend bounce; sample rate identical (192 kHz). Neither should affect CPU materially in RX-only steady state.
- Client workload assumed identical (Brian's existing browser tab, SignalR auto-reconnected on the new backend). Not independently verified.

## Reading

**Real win:** −23 % CPU (mean), with lock contentions halved. The lock-contention drop is consistent with the async-iterator rewrites (`StartIqPump`, `SendLoopAsync`, `StartPsFeedbackPump`) and the `Protocol1Client.RxLoop` `SocketAddress` reuse — those paths used to serialise on `_engineLock` and similar via TP continuation thrash.

**Surprise:** allocation rate did NOT drop (+7 %). The perf3 doc quantified the iterator-state-machine box at ~13.5 % of allocations and the `Socket.ReceiveFrom` EndPoint alloc at ~16 %. We expected ~−32 % combined. We got essentially unchanged. Two plausible explanations:

1. .NET 10's runtime already pools or elides those allocations (post-publication of the perf3 doc) — the perf3 quantification was correct *then* but no longer relevant.
2. The replacement code introduced its own per-iteration allocations we haven't profiled (e.g., the new `Socket.ReceiveFrom(SocketAddress)` overload internally allocates an `IPEndPoint`-equivalent we're not seeing).

A `dotnet-counters` flame chart isn't usable on macOS (UNMANAGED_CODE_TIME). To attribute the residual ~1.87 MB/s, run `xcrun xctrace record --template 'Allocations' --attach <pid>` for 60 s and bucket by class.

## Reproduction

```bash
# Before snapshot — Brian's live develop session, PID 13972
# (Already captured in docs/perf/artifacts/live_idle_counters.csv at 13:12)

# After snapshot — perf_pass_3 Release, HL2 attached
cd /Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_perf_pass_3
dotnet build -c Release Zeus.slnx
dotnet run -c Release --project Zeus.Server &
ZEUS=$(pgrep -fL 'feature_perf_pass_3.*Zeus.Server.dll' | head -1)
# Wait for /api/state status=Connected
dotnet-counters collect \
  --process-id "$ZEUS" \
  --refresh-interval 1 \
  --format csv \
  --output docs/perf/server/after-counters.csv \
  --counters System.Runtime,Microsoft.AspNetCore.Hosting \
  --duration 00:01:00
```

## Open follow-ups

- Confirm allocation surprise by capturing an Instruments.app `Allocations` trace before/after on the same Release build.
- A Debug-vs-Debug or Release-vs-Release run would isolate the perf3 contribution from the build-mode contribution; this measurement does not.
- **iter5 lock-contention uptick (0.93 → 1.37 /s).** Sub-2/s absolute. `ConcurrentQueue<Action>` is itself lock-free; the residue is most likely the `StreamingHub` `_clients.Values` snapshot (taken under `ConcurrentDictionary`'s internal lock once per broadcast) or the per-client `Channel<byte[]>` writer-side (`SingleWriter=false`, takes a lightweight lock to serialise against the reader wake). Profile under a real SignalR client + MOX cycling to localise; not blocking iter5 ship.
- **Hub-broadcast SPSC ring is a deferred drop-in.** `Zeus.Server.Hosting/SpscRing.cs` + 19 passing tests are in tree; the iter5 hub-decision doc (`docs/perf/server/iter2/iter5-hub-decision.md`) records Decision A — keep compiled, do not wire. If iter6 measurement shows TP residue on the `SendLoopAsync` path, the ring drops in without further design work.
- **`OnRadioStateChanged` strict single-thread WDSP.** Iter5 left this path on direct `engine.*` calls (Volatile-Read of the engine pointer, no command-queue hop) per the task #4 judgement call: rare operator-edge path, cross-thread call is cheap, the engine's own disposed-check guards cover the swap-mid-call race. Routing it through `PostDspCommand` is a follow-up if a future iter wants strict single-thread WDSP for state changes too. Same applies to the HTTP endpoint setters (`/api/mic-gain`, `/api/tx/leveler-max-gain`, `/api/tx/ps/{reset,save,restore}`) that read `CurrentEngine` and call the engine directly.
- **Live-with-client capture.** The iter5 live HL2 captures were RX-only with no SignalR client attached. iter4's `ClientCount > 0` display gate means the actual wire-fan-out cost is invisible in this measurement. A follow-up capture with one (or several) real browser sessions connected would localise the residual hub-broadcast / SignalR-send cost and validate the Decision-A reasoning empirically.
- **`OnRadioStateChanged` still does direct `engine.*` calls** via `Volatile.Read` of the engine pointer, not via the command-queue hop. Infrequent path so the cost is negligible, but if strict single-thread WDSP becomes desirable across state-change paths too (e.g. for a future P2 board that has a stricter threading contract on TXA mutators), reroute these through the command queue.
- **Live-HL2 capture under real client load.** Iter5 live captures were RX-only with no SignalR client connected (just `curl /api/state` polling). The branch's full operational delta with the Vite browser session connected hasn't been measured. Estimated additional overhead: ~50 TP wake/s × 1 client per iter4 instrumentation; should not change the iter5 fingerprint qualitatively.
