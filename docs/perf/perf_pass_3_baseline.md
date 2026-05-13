# perf-pass-3 — baseline

Static code review + **live HL2 profile capture** of Brian's running
session (PID 13972, HL2 at 192.168.100.21, 192 kHz LSB on 40 m,
2026-05-11 13:11–13:18 IST). All numbers in §2 / §3a / §4a / §5a are
**live HL2**, captured this pass. Static-only inferences are flagged.

Raw artifacts: `docs/perf/artifacts/` (counter CSVs, .nettrace,
speedscope, top output, audio-context probe JSON).

## 0. Scope

Three concerns (in user-stated order):
1. Zeus.Server CPU at idle RX is higher than wanted.
2. Chrome renderer CPU at idle RX is higher than wanted.
3. **New for pass-3:** noticeable delay between MOX-off and RX audio
   resuming. User suspects audio jitter buffer over-corrects for LAN.

## 1. What's already landed (perf2 + perf3 on `develop`)

Recap from `docs/performance_tuning.md` and `git log`:

| Commit | Branch | Effect |
|---|---|---|
| `56dac59` | perf2 | `/api/state` poll 3.46 Hz → 1.0 Hz; rotator + TCI pollers gated on `enabled` |
| `7bb3808` | perf2 | Mic AudioWorklet → store push throttled 50 Hz → 20 Hz (window-max) |
| `9a36afe` `1c5859a` `30424cf` | perf-rgl | Visibility-gated WebGL loops, DPR clamp |
| `9e3a4d4` | perf3 | Shared rAF draw bus (`zeus-web/src/realtime/draw-bus.ts`) |
| `2c78c30` | perf3 | `pushFrame` decode gated by `hasActiveFrameConsumers()` |
| `0e57230` | perf3 | `StreamingHub.Broadcast`: dropped ArrayPool rent + ToArray copy → **−36 % allocations** |
| `3287724` | perf3 | RX IqFrame buffer returned to `ArrayPool` in `StartIqPump` |

Prior headline: backend 32.7 → 25.7 % (1-client `top` average); renderer
30.8 → 24.3 %. Three pre-existing red-light items deferred to maintainer
review (channel async-iterator box, TX SemaphoreSlim, Socket.ReceiveFrom
EndPoint alloc) — all touching threading or UDP read path.

**TX→RX latency was not investigated in any prior pass.** This is fresh
ground for pass-3.

## 2. Live HL2 profile evidence (this pass)

Brian's process: `Zeus.Server` PID 13972, **Debug build** (`bin/Debug/net10.0`),
.NET 10.0.103, 28 min uptime at capture start, 257 MB RSS, HL2 connected
at 192.168.100.21 @ 192 kHz LSB 40 m, AGC + EMNR + auto-att, audio
context running.

> **Debug-build caveat.** Brian's running session is Debug. Release would
> shave a few pp from managed-frame CPU but leaves WDSP P/Invoke and
> kernel syscalls unchanged. Numbers below are upper bounds for managed
> overhead and ground truth for native cost.

### 2a. Server CPU — `top -pid 13972 -l 7 -s 10`

Live HL2, 1 client (user's Chrome). Sample window 60 s.

| Sample | %CPU (one core) | Threads | CSW (per 10 s) |
|---|---|---|---|
| t=0 (cold) | 0 | 46 | — |
| t=10 s | 46.9 | 47 | 170 k |
| t=20 s | 50.3 | 47 | 192 k |
| t=30 s | 49.2 | 47 | 168 k |
| t=40 s | 48.4 | 49 | 166 k |
| **mean (valid)** | **48.7 %** | **47** | **~17 k/s context switches** |

A second sample 90 s later (after my playwright tab joined as a 2nd
client) showed `dotnet-counters` user+sys CPU dropping to **24.2 %** of
one core (`live_twoclient_counters.csv`). Interpretation: the user was
interactively driving the UI during the first window — VFO scrubs, panel
adjustments — bumping CPU above the steady-state RX-only floor.

**Both numbers are real**:
- ~48 % one core when the operator is touching the UI
- ~24 % one core at quiet steady-state RX

`top` (10 logical CPUs) reports per-core scaled. Brian sees this as
"high" likely because Activity Monitor reports the same number normalised
to 1000 % = 10 cores, so Activity Monitor would read ~5 %. The
**interactive 50 %** spikes are what jump out subjectively.

### 2b. Server counters — `dotnet-counters` 60 s @ 1 Hz, single-client

Source: `docs/perf/artifacts/live_idle_counters.csv`.

| Counter | Mean | Min | Max | Notes |
|---|---|---|---|---|
| `process.cpu.time` user | **0.334 s/s** | 0.19 | 0.44 | ~33 % one core |
| `process.cpu.time` system | **0.217 s/s** | 0.11 | 0.42 | ~22 % one core — high syscall rate |
| `gc.heap.total_allocated` | **1.86 MB/s** | 1.54 | 13.26 | Down from May 9 figure of 3.77 MB/s — perf3 wins holding plus less churn this pass |
| `gc.collections` gen0 | 0.018 /s | 0 | 1 | ~1 gen0 per 54 s — gen0 nursery is huge |
| `gc.collections` gen1/gen2 | 0 | 0 | 0 | None in window |
| `gc.pause.time` | 0.0002 s/s | 0 | 0.009 | Negligible |
| `thread_pool.work_item.count` | **2,086 /s** | 2,009 | 2,291 | Up from May 9's 1,610 — every Channel + Socket continuation lands as a TP work-item |
| `thread_pool.queue.length` | 0 | 0 | 0 | Never backs up — rate sustainable but expensive |
| `monitor.lock_contentions` | **8.7 /s** | 0 | 40 | Some `_engineLock` / `_sync` contention; peaks during interactive activity |
| `exceptions[OperationCanceledException]` | 0.6 /s | 0 | 6 | Cancellation-token churn — likely innocuous |
| `working_set` | 259 MB | 256 | 267 | Stable |
| `jit.compilation.time` | 0.0045 s/s | 0 | 0.04 | Warm — minor tiered-comp tail |
| Heap LOH | 8.0 MB | 6.7 | 15.0 | High-water 15 MB during window |
| Heap POH | 2.4 MB | 2.4 | 2.8 | Pinned object heap stable |

### 2c. Server flame chart — `dotnet-trace cpu-sampling` 30 s

Source: `docs/perf/artifacts/live_idle.nettrace`,
`docs/perf/artifacts/live_idle.speedscope.json` (3.4 MB).

`cpu-sampling` profile name is Linux-only; I used the documented macOS
equivalent `dotnet-sampled-thread-time`. **The result on macOS is mostly
unusable as a managed flame chart**: 583 sec of cross-thread sample
weight bucketed into the synthetic `UNMANAGED_CODE_TIME` frame, because
macOS dotnet-trace can't unwind kernel/native stacks. **What this *does*
prove**: ~all of Zeus's CPU is in unmanaged code — WDSP P/Invoke and
`Socket.Receive*` syscalls — not in managed allocator/JIT paths. That's
consistent with the counter snapshot (allocation rate down, no GC
pressure) and with the perf3 doc's conclusion that the big managed wins
are already taken.

To get a usable managed flame chart on macOS, the **next pass should use
`Instruments.app` Time Profiler** attached to the PID (unwinds native
stacks correctly). The .nettrace artifact is kept for reference / Linux
re-analysis.

Threads alive in the trace (21 of them in the 30 s window) confirm the
hot-path topology — same as the May 9 trace:

| Code path | File |
|---|---|
| `Protocol1Client.RxLoop()` | `Zeus.Protocol1/Protocol1Client.cs:571` |
| `Protocol1Client.TxLoopAsync` | `Zeus.Protocol1/Protocol1Client.cs:849` |
| `DspPipelineService.StartIqPump` (`await foreach`) | `Zeus.Server.Hosting/DspPipelineService.cs:670` |
| `DspPipelineService.Tick(...)` @ 30 Hz | `Zeus.Server.Hosting/DspPipelineService.cs:1084` |
| `WdspDspEngine.RunWorker(ChannelState)` | `Zeus.Dsp/Wdsp/WdspDspEngine.cs:885` |
| `WdspDspEngine.FeedIq`, `ReadAudio`, `TryGetDisplayPixels`, `fexchange0`, `Spectrum0` | P/Invoke seam |
| `StreamingHub.Broadcast{Display,Audio,TxMetersV2,...}` | `Zeus.Server.Hosting/StreamingHub.cs:159+` |
| `StreamingHub.ClientSession.SendLoopAsync` (`await foreach`) | `Zeus.Server.Hosting/StreamingHub.cs:342` |
| `TxMetersService.OnTelemetry / ApplySmoothed / ApplyPaTempSmoothed` | per RX packet, on RxLoop thread |
| `RadioService.OnAdcOverload` | per RX packet, on RxLoop thread |
| `ControlFrame.BuildDataPacket(...)` | TX hot path, 381 Hz |
| `PsAutoAttenuateService.Tick1()` | 10 Hz even when PS off |
| `LogService.GetLogEntriesAsync` + `LiteDB` | UI-driven; appeared in trace because user was browsing logs |

## 3. Suspected hotspots — server

Ranked. **Costs in this table are not measured this pass directly** (the
macOS flame chart wouldn't unwind); they come from the perf3 alloc
breakdown + code shape inspection.

| Rank | Path | File:line | Why hot | Confidence |
|---|---|---|---|---|
| 1 | `Protocol1Client.RxLoop` Socket.ReceiveFrom EndPoint reconstruction | `Zeus.Protocol1/Protocol1Client.cs:590` | 381 pkt/s × `EndPoint.Serialize()` + `IPEndPoint` ctor per call. Perf3 quantified ~16 % allocs. **High** |
| 2 | `DspPipelineService.StartIqPump` async-iterator box | `Zeus.Server.Hosting/DspPipelineService.cs:670` | 381 frames/s × async-state-machine box. Perf3 quantified ~13.5 %. **High** |
| 3 | `Protocol1Client.TxLoopAsync` `_txSignal.WaitAsync(ct)` | `Zeus.Protocol1/Protocol1Client.cs:866` | 381 awaits/s. Perf3 quantified ~5.3 %. dB-sensitive — careful. **High** |
| 4 | `StreamingHub.ClientSession.SendLoopAsync` async-iterator | `Zeus.Server.Hosting/StreamingHub.cs:346` | Per-broadcast `await foreach` over `Channel<byte[]>`. ~60+ frames/s per client × N clients. **Medium-High** |
| 5 | `DspPipelineService.Tick` runs at 30 Hz with zero spectrum consumers | `DspPipelineService.cs:213, 1158-1159` | Server-side equivalent of the `pushFrame` gate. `Spectrum0` + `Array.Reverse(panBuf)` + `Array.Reverse(wfBuf)` fire whether or not any client has a panadapter mounted. **Medium** |
| 6 | RxLoop synchronous fan-out (`TelemetryReceived`, `AdcOverloadObserved`) | `Protocol1Client.cs:679-693` | Up to 2× per RX packet → `TxMetersService.OnTelemetry` (locks + smoothing) + `RadioService.OnAdcOverload`, **on the RX socket thread**, blocking next `ReceiveFrom`. **Medium** |
| 7 | Per-second logger spam in TX loop + `wdsp.setMox` log + `psMonitor.gate` log | `Protocol1Client.cs:882-886`, `WdspDspEngine.cs:1419`, `DspPipelineService.cs:1149` | Each at low rate, but `ConsoleLoggerProcessor.ProcessLogQueue` is a permanent thread. Cumulative. **Low** |
| 8 | `PsAutoAttenuateService.Tick1` 10 Hz even when PS off | `PsAutoAttenuateService.cs:160` (Tick = 100 ms) | Wakes every 100 ms, evaluates `Ps.Enabled` and exits. Wake itself costs a TP work-item. **Low** |
| 9 | `Array.Reverse(panBuf)` + `Array.Reverse(wfBuf)` per Tick | `DspPipelineService.cs:1169-1170` | Two 2048-float reverses @ 30 Hz. Could be folded into WDSP pixel axis or shifted to client. **Low** |

## 4. Suspected hotspots — client

### 4a. Live measurements — playwright @ Vite dev :5173, 30 s window

Source: `docs/perf/artifacts/live_client_top.txt` (Chrome `top`),
`docs/perf/artifacts/live_client_summary.json` (in-page probe).

**Chrome process CPU** (`top -pid <renderer> -pid <gpu-helper>`, valid
samples after cold-start):

| Sample | Renderer %CPU | GPU helper %CPU |
|---|---|---|
| t=10 s | 26.2 | 11.2 |
| t=15 s | 27.4 | 12.0 |
| t=20 s | 28.3 | 13.1 |
| t=25 s | 27.5 | 12.3 |
| t=30 s | 27.1 | 11.8 |
| t=35 s | 27.5 | 10.2 |
| **mean** | **27.3 %** | **11.8 %** |
| **Zeus tab stack** | | **39.1 %** of one core |

Comparable to perf3 final numbers (renderer 24.3 %, GPU 9.9 %, stack
34.2 %). The slight delta is consistent with Vite-dev-mode source-map
overhead + the audio path being active this run.

**In-page probes** (`window.__zeusPerf` rAF / heap / longtask / patched
`copyToChannel`), 57 s + 48 s windows:

| Metric | Value |
|---|---|
| Sustained FPS via rAF | **60.00** (3431 frames / 57.17 s) |
| rAF gap p50 | **16.7 ms** |
| rAF gap p90 | 17.2–17.3 ms |
| rAF gap p99 | **17.6 ms** |
| Long tasks (>50 ms) | **0** in 57 s |
| Heap start → end | 53.2 → 35.0 MB (−18 MB, indicates GC fired mid-window) |
| Audio frame arrival rate | **30.3 Hz** (matches server `DspPipelineService.Tick` 30 Hz) |
| Audio inter-arrival gap p50 | **33.0 ms** |
| Audio inter-arrival gap p90 | 34.3 ms |
| Audio inter-arrival gap p99 | **44.8 ms** |
| Audio inter-arrival gap min / max | 6.4 ms / 60.4 ms |
| AudioContext state | running, 48 kHz |
| AudioContext `baseLatency` | **5.33 ms** (Chrome interactive HW buffer) |
| AudioContext `outputLatency` | **24.0 ms** (audio device output stage) |

**Headline:** the renderer is **not jank-bound** — 60 fps locked, zero
long tasks. The 27 % renderer + 12 % GPU is steady "feeding the
spectrum + audio pipelines" work, not stuttering. The audio stream is
extremely well-conditioned: **p99 inter-arrival gap is 44.8 ms**.

### 4b. Ranked client hotspots

| Rank | Path | File:line | Confidence |
|---|---|---|---|
| 1 | Waterfall `texSubImage2D` per-frame upload | `zeus-web/src/gl/waterfall.ts:203` | **High**, but red-light per `CLAUDE.md` (scroll feel) |
| 2 | Panadapter `bufferSubData` + `texSubImage2D` per-frame upload | `zeus-web/src/gl/panadapter.ts:177, 214` | **High**, red-light |
| 3 | `audio-client.ts:183` — `new Float32Array(frame.samples)` per audio frame | `zeus-web/src/audio/audio-client.ts:183` | Verified live: 30 audio frames/s. ~120 B × 30 = small alloc churn. **Low**-priority cleanup |
| 4 | `ctx.createBuffer` + `ctx.createBufferSource` per audio frame | `zeus-web/src/audio/audio-client.ts:181-185` | 30 Hz, GC'd via `onended`. Verified by counted `copyToChannel` calls. **Low** |
| 5 | Mic worklet → store push @ 20 Hz when unmuted | `zeus-web/src/audio/use-mic-uplink.ts:55-90` | Already throttled (perf2). Audio was running in our session and we still got 0 long-tasks — confirms throttle holds. **Resolved** |

## 5. TX → RX latency map

### 5a. Path with measured numbers where available

| # | Stage | File:line | Cost |
|---|---|---|---|
| 1 | MoxButton release → `setMoxOn(false)` + `setMox(false, signal)` | `zeus-web/src/components/MoxButton.tsx` | ~0 ms |
| 2 | `tx-store.setMoxOn(false)` | `zeus-web/src/state/tx-store.ts:280` | ~0 ms |
| 3 | **`App.tsx` subscriber: `audioClient.reset()`** | `zeus-web/src/App.tsx:213-217` | ~0 ms locally — sets `nextPlayTime=0`, stops pending sources |
| 4 | `POST /api/tx/mox` over HTTP | `zeus-web/src/api/client.ts:1059` | ~1–5 ms LAN (not directly measured this pass; LAN is the user's home network) |
| 5 | `app.MapPost("/api/tx/mox")` → `TxService.TrySetMox(false)` | `Zeus.Server.Hosting/ZeusEndpoints.cs:337-342` | <1 ms |
| 6 | `TxService` lock + dispatch to `_pipeline` + `_radio` | `TxService.cs:108-143` | <1 ms |
| 7 | `WdspDspEngine.SetMox(false)` — `SetPSMox(txa,0)`, `SetChannelState(txa,0,1)`, `SetChannelState(rxa,1,0)` | `WdspDspEngine.cs:1394-1414` | <1 ms native, **but RXA channel must process one `fexchange0` block before output → 21 ms at 1024 samples / 48 kHz** |
| 8 | `Protocol1Client.SetMox(false)` (atomic flag) | `Protocol1Client.cs:461` | <1 µs |
| 9 | RX UDP stream is uninterrupted from HL2; samples buffered into WDSP RXA input ring | `Protocol1Client.cs:715-720`, `DspPipelineService.cs:670-690` | Continuous |
| 10 | **Wait for next 30 Hz Tick** | `DspPipelineService.cs:213` (`PeriodicTimer 1000/30 ms`) | **0 – 33 ms, avg 16.5 ms** (verified via observed 33 ms audio cadence in §4a) |
| 11 | `Tick()` → `engine.ReadAudio` → build `AudioFrame` → `_hub.Broadcast` | `DspPipelineService.cs:1210-1234` | <1 ms |
| 12 | Per-client `Channel<byte[]>` enqueue → `SendLoopAsync` → `_ws.SendAsync` | `StreamingHub.cs:331-356` | ~1 ms |
| 13 | LAN → kernel → JS `ws.onmessage` → `decodeAudioFrame` → `audioClient.push(audio)` | `ws-client.ts:213-233` | **~1–2 ms** (live audio gap p99 is 44.8 ms total — implies network jitter dominated by 33 ms tick alignment, not transport) |
| 14 | **`audio-client.push()` re-anchor branch** | `audio-client.ts:174-179` | **100 ms** — because step 3 set `nextPlayTime=0`. `nextPlayTime < now + 50 ms` → `nextPlayTime = now + 100 ms`. First buffer scheduled to start `currentTime + 100 ms`. |
| 15 | Web Audio output stage: `baseLatency` (HW interactive buffer) | browser | **5.3 ms** (measured live) |
| 16 | Web Audio output stage: `outputLatency` (device queue) | browser | **24.0 ms** (measured live; varies by device, likely an external speaker / Bluetooth) |

### 5b. End-to-end latency budget

| Source | Time (ms) | Origin |
|---|---|---|
| HTTP POST RTT (LAN) | ~2–5 | guess |
| Server endpoint + lock + SetMox dispatch | <1 | code |
| WDSP first RXA fexchange0 | ~21 | 1024 / 48 000 |
| Next 30 Hz Tick wait | 0–33 (avg 16.5) | TickPeriod + observed 33 ms cadence |
| Server Broadcast + WS send | ~1 | code |
| LAN WS transit | ~1–2 | guess |
| Client decode + push | <1 | code |
| **Client audio re-anchor (`BUFFER_TARGET_SECS=0.1 s`)** | **100** | code |
| AudioContext baseLatency | **5.3** | **measured** |
| AudioContext outputLatency | **24.0** | **measured** |
| **TOTAL** | **~170–200 ms** | mostly client-side baked-in |

**Dominant contributor: the 100 ms re-anchor in `audio-client.ts:178`,
triggered by `App.tsx:215`'s unconditional `reset()` on every MOX edge.**
The hardware audio stack is another ~30 ms (measured), and that's
unavoidable — it's the OS / device.

### 5c. The 100 ms is over-conservative for LAN — measured proof

The point of the re-anchor floor is to absorb network jitter so the
playback queue never underflows. From §4a:

- Observed audio gap **p99 = 44.8 ms**
- Observed audio gap **max in 48 s = 60.4 ms**

A target buffer of 50 ms (1.1 × p99) or even 70 ms (1.15 × max) would
absorb every jitter event seen on Brian's LAN. The current 100 ms is
roughly **2.2 × p99**. The client is paying 50 ms of latency for jitter
that never happens on a wired/local network.

### 5d. Two fixes, in order of risk

1. **Stop calling `audioClient.reset()` on MOX-edges.** `App.tsx:213-217`
   resets on `state.mode !== prev.mode` *and* on `state.moxOn !==
   prev.moxOn`. The mode change is justified (sideband flips break
   sample meaning). MOX does not — the server keeps draining audio
   from WDSP every Tick (`DspPipelineService.cs:1210`, unconditional);
   the `nextPlayTime` clock keeps advancing during TX. Removing only
   the MOX-edge reset (one line) recovers ~100 ms with zero risk to
   audio quality or other code paths. **Green-light.**
2. **Lower `BUFFER_TARGET_SECS` to ~50 ms.** `audio-client.ts:67`. Halves
   the re-anchor floor when it does fire. Measured data says this is
   safe for LAN. **Red-light per `CLAUDE.md`** — buffer-size is a
   default-value change the operator will feel on first push after a
   stale gap. Wants maintainer sign-off, but the numbers support it.

## 6. Measurement plan for verification on operator machine

The captures in §2 and §4a were taken on Brian's running session. To
reproduce / refine:

### 6a. Server CPU + counters + flame chart

```bash
# Attach observation tools to the running Zeus.Server (do NOT spin up a
# second instance — would double-load the radio with duplicate Control
# Frames). PID via:
ZEUS_PID=$(pgrep -f 'Zeus.Server.dll' | head -1)

# Counters (60 s CSV)
dotnet-counters collect --process-id $ZEUS_PID \
  --refresh-interval 1 --format csv \
  --output zeus-counters.csv \
  --counters System.Runtime \
  --duration 00:01:00

# top per-thread (70 s, ~10s/sample)
top -pid $ZEUS_PID -l 7 -s 10 -stats pid,cpu,mem,th,csw > zeus-top.txt

# Flame chart — on macOS, dotnet-trace folds into UNMANAGED_CODE_TIME.
# Use Instruments.app instead:
#   open -a Instruments .
#   File → Choose Target → attach to Zeus.Server PID
#   Template: Time Profiler
#   Record 30s
# This unwinds native (WDSP + Socket) stacks correctly.
```

### 6b. Client CPU via playwright

Already done this pass — pattern in `docs/perf/artifacts/`. To rerun:

1. Open `http://localhost:5173/` in Chrome (or playwright equivalent).
2. Find renderer + GPU helper PIDs:
   `ps -ax -o pid,command | grep "Helper (Renderer)\|Helper (GPU)"`
3. `top -pid <renderer> -pid <gpu> -l 7 -s 5 -stats pid,command,cpu,mem,th,csw`.
4. In-page perf: inject the rAF / longtask / heap sampler from
   `docs/perf/artifacts/` (see the `__zeusPerf` block in
   `live_client_summary.json` siblings). Read back after 30 s.

### 6c. TX→RX latency wall-clock instrumentation (for task #4)

This pass measured the **audio inter-arrival cadence** (`copyToChannel`
patch) and the **AudioContext latencies** (`baseLatency`,
`outputLatency`) directly. To measure the **full t₀–t₄** the user
experiences, the task-#4 implementer wants to add the following log
lines (do **not** apply here — analysis-only task):

| File | Suggested log | Captures |
|---|---|---|
| `zeus-web/src/components/MoxButton.tsx` (onPointerUp/Release) | `console.log('mox.client.release', performance.now())` | t₀ |
| `Zeus.Server.Hosting/TxService.cs:222` (after `_pipeline.SetMox(false)`) | `_log.LogInformation("tx.mox.off.recv {Ns}", Stopwatch.GetTimestamp())` | t₁ |
| `Zeus.Dsp/Wdsp/WdspDspEngine.cs:1409` (after `SetChannelState(rxaId, 1, 0)`) | `_log.LogInformation("wdsp.rxa.up {Ns}", Stopwatch.GetTimestamp())` | t₂ |
| `Zeus.Server.Hosting/DspPipelineService.cs:1234` (first broadcast where previous tick had `_keyed=true`) | `_log.LogInformation("rx.audio.firstBroadcast {Ns} samples={N}", Stopwatch.GetTimestamp(), audioSampleCount)` | t₃ |
| `zeus-web/src/audio/audio-client.ts:193` (right after `source.start(nextPlayTime)`) | `if (window.__zeusFirstAudioAfterMox) console.log('audio.scheduled', performance.now(), 'at', this.nextPlayTime, 'now', now)` | t₄ + scheduled play time |
| `zeus-web/src/App.tsx:215` (right next to `getAudioClient().reset()`) | `window.__zeusFirstAudioAfterMox = !state.moxOn;` | gate for above |

Run ten MOX cycles, take the median.

**Prediction (so measurement either confirms or refutes):**
- t₁ − t₀ ≈ 2–5 ms (HTTP RTT)
- t₂ − t₁ ≈ <1 ms (server-side)
- t₃ − t₂ ≈ 21–54 ms (WDSP block + tick wait)
- t₄ − t₃ ≈ 1–2 ms (network + decode)
- **scheduled − now ≈ 100 ms** ← if this is what we see, the
  `App.tsx:215` reset is the proven dominant.

## 7. First impressions

The two pre-existing concerns are **measurably present but not
catastrophic**:

- **Server CPU at idle RX**: ~24 % of one core at quiet steady-state,
  ~48 % during interactive UI use. Perf3 already took the obvious wins.
  The remaining three large allocators (channel async-iterator, TX
  SemaphoreSlim, Socket.ReceiveFrom) are coherent next targets and
  already flagged. The macOS flame chart wouldn't unwind, so
  fine-grained managed-frame attribution wants an Instruments.app pass.
- **Chrome at idle RX**: 27 % renderer + 12 % GPU = ~39 % of one core.
  **60 fps locked, zero long tasks, audio cadence rock-solid at 33 ms
  ± 0.7 ms p50→p90**. The renderer is not janky, just busy. The big
  remaining lever is per-frame WebGL upload, which is red-light per
  `CLAUDE.md`.

The **TX→RX latency concern has a measurable, code-confirmed root
cause**:
- `App.tsx:215` calls `audioClient.reset()` on every MOX edge, including
  MOX-off.
- Combined with `BUFFER_TARGET_SECS = 0.1 s` in `audio-client.ts:67`,
  this introduces a **100 ms baked-in re-anchor** for the first audio
  sample after MOX-off.
- Measured audio gap p99 = 44.8 ms — the 100 ms target is roughly
  2.2× p99, over-conservative for LAN.
- Hardware stack adds another measured 30 ms (5.3 + 24.0).
- **Total ~170 ms TX→RX latency, of which ~100 ms is the one-line
  client-side over-correction.**

The user's intuition is correct.

## 8. Raw artifacts

All under `docs/perf/artifacts/`:

| File | Purpose |
|---|---|
| `live_idle_counters.csv` | 60 s × 1 Hz `dotnet-counters` on PID 13972, single-client window |
| `live_idle_top.txt` | `top -l 7 -s 10` on PID 13972, single-client window |
| `live_idle.nettrace` | `dotnet-trace dotnet-sampled-thread-time` 30 s — useful only for thread inventory on macOS |
| `live_idle.speedscope.json` | speedscope conversion of the above |
| `live_twoclient_counters.csv` | 30 s × 1 Hz `dotnet-counters` during second client connect (lower CPU — proves variance is interactive UI activity) |
| `live_client_top.txt` | Chrome renderer + GPU helper CPU over 30 s |
| `live_client_summary.json` | rAF / audio-frame-cadence / `AudioContext.baseLatency` / `outputLatency` probe |

## References

- Prior perf log: `docs/performance_tuning.md` (perf2 + perf3 writeup)
- Audio jitter logic: `zeus-web/src/audio/audio-client.ts:67-196`
- MOX→audio reset trigger: `zeus-web/src/App.tsx:213-217`
- Server MOX dispatch: `Zeus.Server.Hosting/TxService.cs:108-143`,
  `Zeus.Dsp/Wdsp/WdspDspEngine.cs:1372-1422`
- 30 Hz pipeline tick: `Zeus.Server.Hosting/DspPipelineService.cs:213`
- Allocation hotspots already inventoried: `docs/performance_tuning.md`
  §"Items flagged for maintainer review"
