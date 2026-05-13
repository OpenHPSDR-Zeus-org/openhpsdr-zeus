# iter5 hub-broadcast SPSC ring — decision

**Decision:** **A — no-op for iter5. Keep the existing
`_hub.Broadcast → ClientSession.TryEnqueue → SendLoopAsync` chain.**

The literal `SpscRing<FrameEnvelope>` between the DSP thread and a single
dedicated hub-sender thread was on the iter5 plan as task #5, but the
plan flagged it as profile-first: "_actual problem the user's plan
attacks (step 4): producer side acquiring `_engineLock` per frame.
That goes away in task #4. A literal SPSC ring may not actually be
needed_". This file captures the call.

## Why a ring is not needed

After task #4 (commits `a244196` + `eeb4052`) the four per-frame
consumer pumps (`StartIqPump`, `StartIqPumpP2`, `StartPsFeedbackPumpP1`,
`StartPsFeedbackPumpP2`) are deleted. Those were the dominant residual:

| stage | before task #4 (iter4) | after task #4 | notes |
|--|--|--|--|
| RX IQ → engine | 1× TP wake/packet × 1 pump = ~1200 wake/s | 0 (inline on RxLoop) | hot path |
| P2 IQ → engine | 1× TP wake/packet × 1 pump = variable | 0 (inline on RxLoop) | hot path |
| PS-FB P1 → engine | 1× TP wake/block × 1 pump = ~190 wake/s when armed | 0 (inline on RxLoop) | hot path |
| PS-FB P2 → engine | 1× TP wake/block × 1 pump = ~190 wake/s when armed | 0 (inline on RxLoop) | hot path |
| Display Tick | 30 Hz PeriodicTimer = 30 TP wake/s | 30 Hz inline on RxLoop (Stopwatch elapsed) | hot path |
| Hub → SendLoop | 1× TP wake/frame × N clients = ~65 wake/s × N | unchanged | NOT the hot path |

The hub → SendLoop wake-up volume is roughly two orders of magnitude
smaller than the eliminated pump volume. With Brian's single-client
measurement scenario (~65 Hz: 30 display + 30 audio + 5 meters), the
hub-sender path is ~65 TP wake-ups/sec on ONE TP worker. That's well
under the per-iter floor we measured pre-iter1.

Inserting a ring + dedicated sender thread would:

* Add complexity (a new long-running task, ring drain loop, FrameEnvelope
  discriminated union, lifecycle wiring).
* Move the work from "1 TP worker handling parked-channel wake-ups
  for each client" to "1 dedicated thread draining the ring + 1 TP
  worker per client doing the WS send". Net wake count is unchanged
  — the wake is just relocated.
* The ring batches bursts of broadcasts into one sender wake, which
  IS a small win when `Tick` fires multiple broadcasts in the same
  iteration (display + audio + meters). But the same batching could
  happen by having `SendLoopAsync` keep draining until the channel
  empties — which it already does (`reader.TryRead` in a `while`
  loop, see StreamingHub.cs:359). So batching is already in place
  upstream; the only saving is one wake per Tick-burst, ~30 Hz max.

If iter5-after counters (task #7) still show TP-dispatch dominance, this
decision should be revisited. Profile-driven: ring goes in only if
empirical data supports it.

## What we keep

The SPSC ring lives at `Zeus.Server.Hosting/SpscRing.cs` (built by
`ring-author` in task #2) with passing unit tests. It compiles and is
ready to be wired up. Iter6 (or post-iter5 if measurement shows residual
wake amplification on the hub path) can drop it in without further work.

## Evidence

`docs/perf/server/iter2/iter5-before-synthetic.csv` (measurement
teammate, task #1) captures the iter5-before counters. Task #7 will
capture the iter5-after counters; if the deltas show TP-dispatcher
contention is materially reduced (target: swtch_pri well below 10%,
spin-wait stacks broken up), option A is vindicated. If the SendLoop
path still amortises 10%+ of busy CPU, option B becomes the next move.
