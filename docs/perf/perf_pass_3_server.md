# perf-pass-3 — server (Zeus.Server) fixes

> **Update 2026-05-11:** iter5 (single-thread DSP ownership) landed — live HL2 CPU 32.8 % → 24.3 % (−26 %), TP work-items 1 957/s → 432/s (−78 %), `swtch_pri` −68 %, `ThreadNative_SpinWait` −52 %. See `server/after.summary.md` "Round 3, iter 5" section for the full writeup.

One row per fix. Each row gives file:line, what changed, the hotspot
quantification from `perf_pass_3_baseline.md` §3, and the expected delta
on the alloc-rate / work-item / CPU axes captured from Brian's live HL2
session.

> **Measurement note.** Brian's live Zeus.Server (PID 13972, Debug,
> `OPENHPSDR-Zeus/Zeus.Server/bin/Debug/net10.0`) is the only process on
> the box currently driving the HL2 hot paths (RxLoop / StartIqPump /
> TxLoop / StreamingHub broadcasts). The perf3 worktree's Release build
> with no radio attached reaches a floor of **~31 KB/s alloc, ~62
> work-items/s, ~1.2 % CPU of one core** — the broadcast path
> early-returns in synthetic mode (`DspPipelineService.cs:1107`), so no
> radio means no fan-out. **Empirical "after" numbers therefore require
> Brian to restart his session from this branch.** The "before" column
> below was captured live 2026-05-11 13:27–13:28 IST against PID 13972;
> the expected-delta column is grounded in the perf3 alloc breakdown.

## Before snapshot (live, 60 s @ PID 13972)

| Counter | Value | Source |
|---|---|---|
| `process.cpu.time` user | 0.167 s/s (16.7 % one core) | `docs/perf/server/before-counters.csv` |
| `process.cpu.time` system | 0.081 s/s (8.1 % one core) | same |
| **total CPU** | **24.8 % one core** | same |
| `gc.heap.total_allocated` | **1.64 MB/s** | same |
| `gc.collections` gen0 | 0.017 /s | same |
| `gc.collections` gen1/gen2 | 0 | same |
| `gc.pause.time` | 0.0001 s/s | same |
| `thread_pool.work_item.count` | **2 081 /s** | same |
| `thread_pool.queue.length` | 0 (never backed up) | same |
| `monitor.lock_contentions` | 8.3 /s | same |
| `working_set` | 199 MB | same |

Raw `top -pid 13972` over the same window: 0.0 % (interactive-style
samples were absent during this capture, matching baseline §2a's
"quiet-steady-state" figure of ~24 %).

Idle floor (Release, no radio, my build): 31 KB/s alloc, 62 work/s,
1.2 % CPU. Raw at `docs/perf/server/release-idle-noradio.csv`.

## Commits

| # | Commit | File:line | Hotspot from baseline §3 | Change | Expected delta (live HL2) |
|---|---|---|---|---|---|
| 1 | `7b156a0` | `Zeus.Protocol1/Protocol1Client.cs:571-590` | #1 Socket.ReceiveFrom EndPoint reconstruction (~16 % alloc-rate, ~262 KB/s of the 1.64 MB/s) | Switched RX loop from `Socket.ReceiveFrom(byte[], int, int, SocketFlags, ref EndPoint)` (allocates a fresh `IPEndPoint` per packet via `EndPoint.Create(socketAddress)`) to the .NET 8+ `Socket.ReceiveFrom(Span<byte>, SocketFlags, SocketAddress)` overload, with a single reusable `SocketAddress` instance hoisted out of the loop. Source address is not consumed by the loop (HL2 is the only peer the bound socket sees). | Alloc-rate **~1.64 MB/s → ~1.38 MB/s** (−16 %). No change to CPU samples expected (syscall path is identical), but GC pressure drops, so gen0 events go from 1/60 s toward 1/72 s. |
| 2 | `66772ae` | `Zeus.Server.Hosting/DspPipelineService.cs:665-728` | #2 StartIqPump `await foreach (ReadAllAsync(ct))` async-iterator box (~13.5 % alloc-rate, ~221 KB/s) | Replaced `await foreach (var frame in client.IqFrames.ReadAllAsync(ct))` in **both** P1 and P2 IQ pumps with a direct `while (true) { try { frame = await reader.ReadAsync(ct); } catch (ChannelClosedException) { break; } }` loop. `ChannelReader.ReadAsync` returns a `ValueTask<T>` off the channel's pooled `IValueTaskSource`; the compiler-generated async-iterator state machine (which the `await foreach` form wraps in a `ManualResetValueTaskSourceCore<bool>` per `MoveNextAsync`) is gone. Semantics identical: same ordering, same cancellation, same channel-closed terminator. | Alloc-rate **~1.38 MB/s → ~1.16 MB/s** (−13.5 % off the original; cumulative −29.5 %). Work-item count likely drops a touch — the iterator's continuation lands as a TP work-item every MoveNext. |
| 3 | `29125ef` | `Zeus.Server.Hosting/StreamingHub.cs:342-357` | #4 ClientSession.SendLoopAsync `await foreach (ReadAllAsync(ct))` — per-WS-client, ~60 frame/s combined display+audio+meter | Same `await foreach` → `ChannelReader.ReadAsync` rewrite as #2. Per-frame box was smaller than the IQ-pump's (60 vs 381 Hz) but multiplies by every connected WebSocket client. Channel is `CreateBounded(DropOldest, SingleReader=true)` so dropped-oldest behaviour and back-pressure semantics are identical. | Single-client: alloc-rate **~1.16 MB/s → ~1.13 MB/s** (~−2 %). Multi-client: scales linearly with N. Most visible win is in lower work-item-count rate (saves one TP continuation per broadcast per client). |
| 4 | `1db1c8d` | `Zeus.Server.Hosting/DspPipelineService.cs:758-815` | StartPsFeedbackPumpP2 (`:766`) + StartPsFeedbackPumpP1 (`:801`) — same `await foreach` over `PsFeedbackFrames` channel | Same `await foreach` → `ChannelReader.ReadAsync` rewrite as #2. Runs at ~188 frame/s when PS is armed (192 kHz / 1024 paired samples). Both reader pumps preserved byte-identical `FeedPsFeedbackBlock` body under the new loop shape. | Alloc-rate **~−2 %** of original (PS-only, ~half the IQ rate). Combined cumulative −32 %. Most win shows up when PureSignal is armed during TX. |

After all four commits the combined expected effect
on Brian's live session is ~−30 % alloc-rate (~1.64 → ~1.16 MB/s), a
proportional drop in gen0 GC frequency, and a smaller dip in work-item
throughput. CPU `s/s` won't change much in user-mode because the syscall
path is unchanged; the win is GC pressure and steady-state heap churn.

## Items deliberately deferred this pass

| # | Hotspot from baseline §3 | Why deferred |
|---|---|---|
| 3 baseline rank | `Protocol1Client.TxLoopAsync` SemaphoreSlim `WaitAsync(ct)` (~5.3 % alloc) | TX timing is dB-sensitive (CLAUDE.md "Red-light") and the perf3 baseline already flags it as maintainer-review-required. The `WaitAsync(CancellationToken)` allocates an `AsyncOperation` per call when the semaphore is empty; replacing it with a `ManualResetEventSlim` or a custom signaling primitive needs HL2-on-the-bench validation to confirm 381 pkt/s TX pacing is preserved. Skipped. |
| 5 baseline rank | `DspPipelineService.Tick` runs at 30 Hz with no spectrum consumers; `Array.Reverse(panBuf)` + `Array.Reverse(wfBuf)` per tick | Touching `Tick` cadence or the pixel reversal is a UX-visible default (CLAUDE.md "Red-light"); needs maintainer sign-off. Skipped. |
| 6 baseline rank | RX synchronous fan-out (`TelemetryReceived`, `AdcOverloadObserved`) on RxLoop thread | Re-architecting to use a TP-bounded notifier requires threading-discipline scrutiny on top of the perf3 RX hot-path. Skipped. |
| 7-9 baseline rank | Logger spam, `PsAutoAttenuateService.Tick1`, etc. | Low-confidence wins; would prefer to land #1-#3 first and re-measure before chasing the long tail. |

## How to validate

Brian's bench:

```bash
# In Brian's main repo (OPENHPSDR-Zeus), stop the Debug session, switch to
# the perf_pass_3 branch and run a clean Release:
pkill -f 'Zeus.Server.dll'
git fetch && git checkout feature/perf_pass_3
dotnet run -c Release --project Zeus.Server

# In another shell, with HL2 connected and 192 kHz LSB on 40 m as the
# baseline:
ZEUS_PID=$(pgrep -f 'Zeus.Server.dll' | head -1)
dotnet-counters collect --process-id $ZEUS_PID --refresh-interval 1 \
  --format csv --duration 00:01:00 \
  --counters System.Runtime,Microsoft.AspNetCore.Hosting \
  --output docs/perf/server/after-counters.csv

# Compare:
python3 - <<'PY'
import csv, statistics
from collections import defaultdict
def mean_of(path, want):
    b = defaultdict(list)
    with open(path) as f:
        r = csv.reader(f); next(r)
        for row in r:
            if len(row) < 5: continue
            _, prov, name, _, val = row
            try: v = float(val)
            except: continue
            b[f"{prov}::{name}"].append(v)
    return {k: statistics.mean(v) for k, v in b.items() if any(s in k for s in want)}
before = mean_of('docs/perf/server/before-counters.csv', ['cpu.time','total_allocated','work_item','lock_contentions'])
after  = mean_of('docs/perf/server/after-counters.csv',  ['cpu.time','total_allocated','work_item','lock_contentions'])
for k in sorted(before):
    if k in after:
        d = (after[k] - before[k]) / before[k] * 100 if before[k] else 0
        print(f"{k[28:80]:55} before={before[k]:.3f}  after={after[k]:.3f}  delta={d:+.1f}%")
PY
```

If the alloc-rate row reads anywhere from −20 % to −35 %, the load-bearing
two fixes (Socket.ReceiveFrom, StartIqPump) landed cleanly. If it doesn't
move, something is wrong with the build or the radio isn't actually
streaming during the capture window.

## Raw artifacts

All under `docs/perf/server/`:

| File | Purpose |
|---|---|
| `before-counters.csv` | 60 s × 1 Hz `dotnet-counters` on PID 13972 (live HL2 session, develop branch, this branch's baseline) |
| `before-top.txt` | matching `top -pid 13972` sample |
| `release-idle-noradio.csv` | 30 s × 1 Hz `dotnet-counters` on Release build of perf_pass_3 with no radio (floor reference) |
| `preview-counters.csv` | smoke-check that `dotnet-counters` attached cleanly before the 60 s capture |
