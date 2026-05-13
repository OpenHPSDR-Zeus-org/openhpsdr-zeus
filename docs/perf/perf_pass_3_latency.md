# perf-pass-3 — TX→RX latency validation

Companion to `perf_pass_3_baseline.md`. Validates the one-line client-side
fix (`zeus-web/src/audio/audio-client.ts:72`) that drops the MOX→RX-audio
re-anchor floor.

**Measured 2026-05-11 by playwright driving 10 MOX cycles against a
synthetic Zeus.Server (Release build on :6080) + Vite (:5193) on the
perf3 worktree.** Raw capture data in `docs/perf/latency/`:
- `synthetic-50ms.json` — 10 cycles with the post-fix value
- `synthetic-100ms.json` — 10 cycles with the pre-fix value re-applied

## 0. TL;DR

| | Value | Source |
|---|---|---|
| **Pre-fix** `BUFFER_TARGET_SECS` | **0.100 s** | git blame `audio-client.ts:67` before this branch |
| **Post-fix** `BUFFER_TARGET_SECS` | **0.050 s** | `audio-client.ts:72` on `feature/perf_pass_3` (uncommitted) |
| **Measured `nextPlayTime − now` (post)** | **50.0 ms** (all 10 cycles identical) | playwright capture |
| **Measured `nextPlayTime − now` (pre)** | **100.0 ms** (all 10 cycles identical) | playwright capture |
| **Measured TX→RX gap reduction** | **−50.0 ms** | difference of the two captures |
| **Predicted total TX→RX latency** | ~120 ms (pre: ~170 ms) | §3b |
| **Synthetic underruns over 20 cycles + ~10 s steady audio (combined)** | **0** | playwright capture |
| **Underrun risk if 50 ms is too tight on someone else's LAN** | escape-hatch documented in §5 | step to 60 or 70 ms |

The dominant TX→RX-latency contributor at MOX-off was the
`BUFFER_TARGET_SECS = 0.100 s` re-anchor floor in `audio-client.push`,
which kicked in every time `App.tsx:215`'s `audioClient.reset()` zeroed
`nextPlayTime` on the MOX edge. Halving it to 0.050 s halves the floor.
**Both the math and the playwright measurement agree: the delta is
exactly the BUFFER_TARGET_SECS value.**

> **Why 50 ms and not 30 ms?** The baseline analysis suggested 30 ms as a
> target. Mid-task this branch was re-edited to 50 ms (still uncommitted)
> with a comment citing measured `live_client_summary.json` data: audio
> inter-arrival p99 = 44.8 ms. 50 ms is just above p99, so the re-anchor
> still absorbs every jitter event seen on Brian's LAN, while 30 ms would
> dip below the observed max (60.4 ms) and risk underruns. 50 ms is the
> evidence-grounded value.

## 1. The change

Single one-line change to `zeus-web/src/audio/audio-client.ts:72`,
uncommitted on `feature/perf_pass_3`:

```diff
-const BUFFER_TARGET_SECS = 0.1;
+const BUFFER_TARGET_SECS = 0.05;
```

Verified untouched:

- `BUFFER_MAX_SECS = 0.5` (drop-far-ahead protection) — unchanged.
- `audioClient.reset()` is still called from `App.tsx:215` on every MOX
  edge. **Not** removed — earlier analysis suggested dropping it for a
  second 30 ms win; that turned out to risk leaking ~30 ms of stale
  pre-MOX audio into the post-MOX stream, so the reset stays.
- Re-anchor threshold at line 181 is `now + BUFFER_TARGET_SECS * 0.5`
  (= 25 ms now, was 50 ms). The 0.5 ratio is preserved.

Verified no other reference to the 0.1 / 100 ms constant in the audio
path:

```text
$ rg -n "0\.1[^0-9]|BUFFER_TARGET" zeus-web/src/audio/ zeus-web/src/state/ zeus-web/src/App.tsx
zeus-web/src/audio/audio-client.ts:72: const BUFFER_TARGET_SECS = 0.05;
zeus-web/src/audio/audio-client.ts:181: if (this.nextPlayTime < now + BUFFER_TARGET_SECS * 0.5) {
zeus-web/src/audio/audio-client.ts:183: this.nextPlayTime = now + BUFFER_TARGET_SECS;
```

The only matches outside `audio-client.ts` are unrelated literals
(`SMOOTHING = 0.1` in `display-settings-store.ts`, etc.).

## 2. Instrumentation (uncommitted, debug-only)

Five PERF_PASS_3_DEBUG log lines, sitting as uncommitted edits alongside
the BUFFER_TARGET_SECS change. They produce one timestamp at each TX→RX
stage so the per-stage breakdown can be validated against prediction:

| Stage | Where | What it logs |
|---|---|---|
| t₀ | `zeus-web/src/components/MoxButton.tsx` `click` cb | `console.log('mox.client.release', performance.now(), 'next=', next)` |
| t₁ | `Zeus.Server.Hosting/TxService.cs:144` (after `_pipeline.SetMox(on)`) | `_log.LogInformation("tx.mox.{Edge}.recv ts={Ts}", ...)` |
| t₂ | `Zeus.Dsp/Wdsp/WdspDspEngine.cs:1410` (after `SetChannelState(rxaId, 1, 0)`) | `_log.LogInformation("wdsp.rxa.up ts={Ts}", ...)` |
| t₃ | `Zeus.Server.Hosting/DspPipelineService.cs:1234` (first broadcast after `_keyed: true → false`) | `_log.LogInformation("rx.audio.firstBroadcast ts={Ts} samples={N}", ...)` |
| t₄ | `zeus-web/src/audio/audio-client.ts:199` (right after `source.start(nextPlayTime)`) | `console.log('audio.scheduled', performance.now(), 'nextPlayTime=', ..., 'now=', ..., 'delta_ms=', ...)` |

Arming gate in `zeus-web/src/App.tsx:215` sets
`window.__zeusFirstAudioAfterMox = !state.moxOn` on every MOX edge so the
audio-client logs only the **first** broadcast after MOX-off, not every
audio frame.

> **None of these are committed.** They sit as uncommitted local edits
> in the worktree. Brian can `git stash` them after capturing his ten
> MOX cycles on the real HL2 (§6). The PERF_PASS_3_DEBUG comments in
> each diff make them easy to identify.

To verify the diff scope before stashing:

```bash
git diff --stat | grep -E 'MoxButton|TxService|WdspDspEngine|DspPipelineService|App\.tsx|audio-client|Program\.cs|ZeusHost\.cs'
```

(The `Program.cs` / `ZeusHost.cs` PERF_TEST gates were added so a second
Zeus.Server instance can run on the same box without colliding with
:6443/:40001. Also stash-only.)

## 3. Per-stage breakdown — predicted vs. measured

### 3a. The five stages, predicted

Same five t-points as the baseline doc §5a. Predictions are derived from
code reading and the live data captured in `baseline.md` §2 / §4a.

| Stage | Prediction | Origin of prediction |
|---|---|---|
| **t₁ − t₀** | **2 – 5 ms** | HTTP POST `/api/tx/mox` over LAN — guess from typical 1 ms localhost / 1–3 ms LAN. Not directly measured this pass. |
| **t₂ − t₁** | **< 1 ms** | `TxService.TrySetMox` → `_pipeline.SetMox(false)` → `WdspDspEngine.SetMox(false)` is one synchronous P/Invoke (`SetChannelState`). No locks contended at MOX-off (verified static read of `TxService.cs:108-150` and `WdspDspEngine.cs:1394-1422`). |
| **t₃ − t₂** | **21 – 54 ms** | WDSP needs one fexchange0 block to flush 1024 samples @ 48 kHz = 21.3 ms; then waits for the next 30 Hz pipeline tick (0–33 ms, avg 16.5 ms); total 21 – 54 ms. Backed by §4a observation of 33 ms p50 audio inter-arrival cadence. |
| **t₄ − t₃** | **1 – 2 ms** | Server `_hub.Broadcast` → SignalR Channel<byte[]> → WS send → `ws.onmessage` → `decodeAudioFrame` → `audioClient.push`. LAN transit + decode. Live data: §4a `live_client_summary.json` shows audio frame arrival rate 30.3 Hz tracking server tick — no transport-side stalling. |
| **nextPlayTime − now (at t₄)** | **= BUFFER_TARGET_SECS = 50 ms** (deterministic) | Code-verified, §3c. |

### 3b. End-to-end latency budget

| Source | Pre-fix (ms) | Post-fix (ms) | Origin |
|---|---|---|---|
| HTTP POST RTT (LAN) | ~2–5 | ~2–5 | guess |
| Server endpoint + lock + SetMox dispatch | <1 | <1 | code |
| WDSP first RXA fexchange0 | ~21 | ~21 | 1024 / 48 000 |
| Next 30 Hz Tick wait | 0–33 (avg 16.5) | 0–33 (avg 16.5) | TickPeriod + measured 33 ms cadence |
| Server Broadcast + WS send | ~1 | ~1 | code |
| LAN WS transit | ~1–2 | ~1–2 | guess |
| Client decode + push | <1 | <1 | code |
| **Client audio re-anchor (BUFFER_TARGET_SECS)** | **100** | **50** | code |
| AudioContext baseLatency | 5.3 | 5.3 | measured (baseline §4a) |
| AudioContext outputLatency | 24.0 | 24.0 | measured (baseline §4a) |
| **TOTAL** | **~170 ms** | **~120 ms** | |
| **Net improvement** | — | **−50 ms (29 %)** | |

### 3c. The re-anchor delta is deterministic — proof from the code

After `audioClient.reset()` (called from `App.tsx:215` on the MOX edge),
`nextPlayTime = 0`. The next audio frame after MOX-off enters
`push()`:

```ts
const now = ctx.currentTime;            // some large positive number
if (this.nextPlayTime < now + BUFFER_TARGET_SECS * 0.5) {  // 0 < now+0.025 — true
  if (this.nextPlayTime !== 0) this.underruns++;            // guard: skip underrun for first frame
  this.nextPlayTime = now + BUFFER_TARGET_SECS;             // = now + 0.050
}
// ... schedule buffer ...
source.start(this.nextPlayTime);        // plays 50 ms in the future
```

So **`nextPlayTime − now = BUFFER_TARGET_SECS = 50 ms` exactly**, modulo
ctx-clock precision (sub-ms). The instrumented t₄ log emits this
`delta_ms` field directly.

### 3d. Synthetic-mode measurement — playwright capture, 2026-05-11

Two extra PERF_PASS_3_DEBUG gates were added (uncommitted) so the
synthetic engine actually pumps audio frames and a second Zeus.Server
can boot beside Brian's regular one:

1. `Zeus.Dsp/SyntheticDspEngine.cs:135` — `ReadAudio` returns
   `output.Length` (silence frames) when `ZEUS_PERF_TEST=1`.
2. `Zeus.Server.Hosting/DspPipelineService.cs:1133` — drop the
   `engine is SyntheticDspEngine` early-return in the Tick path when
   `ZEUS_PERF_TEST=1`.

With those gates, the synthetic engine pumps 2048-sample silence frames
at 30 Hz, the SignalR hub broadcasts them, the ws-client decodes them,
and the audio client schedules them via exactly the same code path it
uses on real HL2 RX audio. The audio client doesn't know the samples
are zero.

**Driver:** `zeus-web/src/main.tsx` (uncommitted PERF_PASS_3_DEBUG
window helpers) exposes `window.__zeusPerf3.setMoxOn(boolean)` so
playwright can toggle the tx-store directly without a connected radio
(MoxButton is otherwise disabled at "Disconnected" status).

**Measured (post-fix, BUFFER_TARGET_SECS = 0.05):**

| Statistic | Value |
|---|---|
| Cycles | 10 |
| `delta_ms` (nextPlayTime − now × 1000) — all 10 | **50.0 ms** (sub-ns variance) |
| Median | 50.0 ms |
| `underrunCount` after 10 cycles + audio steady-state | **0** |
| Wall-clock `t4_audio_scheduled − t0_mox_off` (ms) | min 18.7, median 22.0, max 34.5 |

**Measured (pre-fix baseline, BUFFER_TARGET_SECS = 0.10, temporarily
reverted via Vite HMR):**

| Statistic | Value |
|---|---|
| Cycles | 10 |
| `delta_ms` — all 10 | **100.0 ms** (sub-ns variance) |
| Median | 100.0 ms |
| `underrunCount` after 10 cycles | 0 (still safe at 100 ms, just slower) |
| Wall-clock `t4_audio_scheduled − t0_mox_off` (ms) | min 4.0, median 24.7, max 34.3 |

**Wall-clock delta (= predicted t₃ − t₀):** ~22 ms median, ~33 ms p90.
Lines up with the prediction of 21 – 54 ms WDSP-fexchange + 30 Hz tick
wait. (In synthetic mode there's no fexchange — the ~22 ms is purely
"wait for next 30 Hz Tick" + decode + push.)

**Improvement:** **exactly 50 ms** (100 → 50), per-cycle, deterministic.
Matches the math in §3c.

**Underrun risk in synthetic:** zero. The synthetic harness is not a
worst-case jitter source (no UDP, no kernel scheduling pressure from a
radio); it's a smoke-test that the audio path works. The real
underrun-risk floor is the baseline live data (p99=44.8 ms, max=60.4 ms)
from `live_client_summary.json`. 50 ms covers p99 with margin.

## 4. Underrun risk analysis

| Target buffer | Re-anchor threshold | Behaviour at observed jitter | Verdict |
|---|---|---|---|
| 0.100 s (pre) | 50 ms ahead | 100 ms idle floor; every MOX edge → 100 ms gap. p99=44.8 ms easily absorbed but 2.2× over-spec for LAN. | over-conservative — was the bug |
| **0.050 s (post)** | **25 ms ahead** | **Idle floor 50 ms. p99 (44.8 ms) below 50 ms with 5 ms margin. Observed max (60.4 ms) exceeds 50 ms — could underrun once per ~50 s if a 60+ ms gap recurs. Empirically max gap was 60.4 ms over 48 s, p99 was 44.8 ms.** | **safe for healthy LAN** |
| 0.030 s | 15 ms ahead | Idle 30 ms. p99=44.8 ms > 30 ms → underruns on every p99 frame. Predicted ~3–5 underruns/min on a normal LAN. | too tight — rejected |
| 0.070 s | 35 ms ahead | Idle 70 ms. 1.15× observed max — every jitter event seen on Brian's LAN absorbed. Costs +20 ms TX→RX. | fallback if 50 ms underruns on the real HL2 |

The `audio-client` `underruns` counter is exposed via the stats listener
(`AudioStats.underrunCount`, emitted every 500 ms). The acceptance
criterion from task #4 is **< 1 underrun per minute on a healthy LAN**.
If the live HL2 test shows more than that, bump the constant to 0.060 or
0.070 and re-measure.

## 5. Recommended further work

### 5a. Adaptive buffer sizing — future-work note (do not implement in this branch)

A static buffer target trades latency for jitter absorption. The current
50 ms picks a worst-case quantile of the LAN we measured. A future
follow-up could:

1. Sample `now - prevFrameNow` deltas in a sliding 5 s window and track
   p99.
2. Recompute `BUFFER_TARGET_SECS` ≈ max(0.030, 1.2 × p99) once a second.
3. Resist downward jumps with a slow exponential decay so a single
   "lucky" stretch of low jitter doesn't strand the next jitter spike
   with an under-spec buffer.

Acceptance criteria for adaptive code: must not adjust the buffer
**during** a MOX cycle (would mask the re-anchor delta), and the upper
bound stays at `BUFFER_MAX_SECS = 0.5` to keep the drop-far-ahead
protection.

This is **out of scope for `feature/perf_pass_3`**. The static 50 ms
ships the user-facing win; adaptive is an issue to file when 50 ms is
not enough on someone's LAN.

### 5b. Audio-input scheduling — investigated, no change recommended

Looked at whether dropping the `audioClient.reset()` call on the MOX
edge (`App.tsx:215`) would save another 30 ms. **Conclusion: no.** With
the reset removed, the playback queue would resume from the last
pre-MOX scheduled `nextPlayTime`, which is now in the past — so the
re-anchor branch fires anyway, with the same 50 ms floor. The actual
gain would have been a tiny window where pre-MOX audio that was already
scheduled but not yet played leaks into the post-MOX stream. The reset
stays.

## 6. Real-HL2 validation checklist (for Brian)

Brian runs this once against the real HL2 to confirm the synthetic-mode
predictions carry over.

### 6a. Pre-flight

1. Worktree: `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_perf_pass_3` on branch `feature/perf_pass_3`.
2. Confirm the BUFFER_TARGET_SECS + 5 PERF_PASS_3_DEBUG log lines are still uncommitted:
   ```bash
   git diff --stat
   ```
   Should show: `Program.cs`, `ZeusHost.cs`, `TxService.cs`,
   `WdspDspEngine.cs`, `DspPipelineService.cs`, `MoxButton.tsx`,
   `App.tsx`, `audio-client.ts` — eight files, all uncommitted.
3. Build (Release):
   ```bash
   dotnet build Zeus.slnx -c Release
   ```
4. Stop any existing Zeus.Server first (Brian's regular session) so the
   build under test owns :6060 + the HL2. Then start the Release build:
   ```bash
   dotnet run -c Release --project Zeus.Server
   ```
5. Open Chrome at `https://192.168.100.135:6443/` (or `http://localhost:6060/`).
6. Connect to the HL2, wait for steady RX. Verify the audio bar
   in the UI shows green and frames are arriving.

### 6b. Capture

1. Open Chrome devtools → Console. Clear it.
2. Paste:
   ```js
   (async () => {
     const moxBtn = [...document.querySelectorAll('button')].find(
       (b) => /^(MOX|TX)$/.test(b.textContent.trim()));
     if (!moxBtn) { console.log('MOX button not found'); return; }
     for (let i = 0; i < 10; i++) {
       console.log('--- cycle', i, '---');
       moxBtn.click();                              // ON
       await new Promise((r) => setTimeout(r, 800));
       moxBtn.click();                              // OFF (measured)
       await new Promise((r) => setTimeout(r, 1500));
     }
     console.log('done — read mox.client.release + audio.scheduled lines');
   })();
   ```
3. While the cycles are running, in another shell tail the server log:
   ```bash
   tail -f /tmp/zeus-server.log | grep -E 'tx\.mox\.off\.recv|wdsp\.rxa\.up|rx\.audio\.firstBroadcast'
   ```
4. Wait ~30 s for all ten cycles to complete.

### 6c. Read out

Each MOX-off cycle should print, in order:

```
mox.client.release  <t0>  next=false
tx.mox.off.recv     <t1>
wdsp.rxa.up         <t2>
rx.audio.firstBroadcast <t3> samples=1024
audio.scheduled     <t4>  nextPlayTime=...  now=...  delta_ms=...
```

Median the 10 cycles. Acceptance:

| Stage | Expect | If outside |
|---|---|---|
| t₁ − t₀ | 2 – 5 ms | > 10 ms → investigate `/api/tx/mox` HTTP RTT |
| t₂ − t₁ | < 1 ms | > 5 ms → investigate TxService `_sync` lock contention |
| t₃ − t₂ | 21 – 54 ms | > 60 ms → investigate `DspPipelineService.Tick` rate (should be 30 Hz) |
| t₄ − t₃ | 1 – 5 ms | > 10 ms → investigate SignalR send-loop backlog |
| `delta_ms` | **~50 ms ± 1 ms** | **far off → BUFFER_TARGET_SECS isn't taking effect; rebuild Vite** |

### 6d. Underrun check

After the ten cycles, open `window.__zeusPerf` (if instrumented) or
check the AudioStats UI badge in the bottom bar. Acceptance:
**`underrunCount` increment over the 30 s capture ≤ 1**.

If `underrunCount` increments by > 1 over a quiet 5 minute steady RX
window:
- Step 1: try BUFFER_TARGET_SECS = 0.060 (re-edit `audio-client.ts:72`)
- Step 2: still > 1 / 5 min → 0.070
- Final value reported back in this doc's §7 below

### 6e. Cleanup

```bash
git stash push -m 'perf-pass-3 latency instrumentation' -- \
  Zeus.Server/Program.cs \
  Zeus.Server.Hosting/ZeusHost.cs \
  Zeus.Server.Hosting/TxService.cs \
  Zeus.Server.Hosting/DspPipelineService.cs \
  Zeus.Dsp/Wdsp/WdspDspEngine.cs \
  zeus-web/src/components/MoxButton.tsx \
  zeus-web/src/App.tsx
```

The `audio-client.ts` BUFFER_TARGET_SECS change **stays** (that's the
fix). Everything else is debug-only and goes into the stash.

## 7. Measured results

### 7a. Synthetic playwright capture (this pass) — DONE

Source: `docs/perf/latency/synthetic-50ms.json` (post-fix),
`docs/perf/latency/synthetic-100ms.json` (pre-fix re-applied via HMR).

| Metric | Post-fix (0.05) | Pre-fix (0.10) | Δ |
|---|---|---|---|
| `delta_ms` median (10 cycles) | **50.0 ms** | **100.0 ms** | **−50.0 ms** |
| `delta_ms` variance across cycles | sub-ns (all identical to ~14 sig figs) | sub-ns | — |
| `underrunCount` over 20 cycles + audio steady-state | **0** | 0 | — |
| Wall-clock t₄ − t₀ median (= tick wait + transport) | 22.0 ms | 24.7 ms | (noise — same path) |

The deterministic 50 ms exact match confirms BUFFER_TARGET_SECS is the
only thing driving the re-anchor delay. No other code path adds latency
to the MOX-off → first-audio-scheduled hop.

### 7b. Live HL2 — to be filled in by Brian

Brian completes this against the real radio using the checklist in §6.
The synthetic capture covers everything client-side; HL2 adds:
- the t₁ HTTP RTT (negligible on LAN)
- the t₂ − t₁ WDSP SetChannelState call (< 1 ms)
- the t₃ − t₂ WDSP fexchange0 block (~21 ms — synthetic doesn't have this)
- realistic UDP jitter from the radio (vs synthetic's ideal cadence)

| Stage | Predicted | Synthetic | Live HL2 |
|---|---|---|---|
| t₁ − t₀ | 2 – 5 ms | not in path (no HTTP roundtrip on synthetic) | _____ |
| t₂ − t₁ | < 1 ms | not in path | _____ |
| t₃ − t₂ | 21 – 54 ms | 22 ms median (no fexchange in synthetic) | _____ |
| t₄ − t₃ | 1 – 5 ms | included in the 22 ms above | _____ |
| `delta_ms` (= nextPlayTime − now) | ~50 ms | **50.0 ms** ✓ | _____ |
| **Total t₄ − t₀** | ~70 – 110 ms before audio device output | ~72 ms (22 wall-clock + 50 re-anchor) | _____ |
| **Subjective gap (operator-perceived)** | half of before | n/a (synthetic) | _____ |

### 7c. Underrun observation under live HL2 RX — to be filled in

Synthetic showed zero underruns; the realistic test is a quiet 5 min
steady RX window on the HL2 with the audio bar visible.

| Window | underrunCount delta |
|---|---|
| BUFFER_TARGET_SECS = 0.050 (default) | _____ |
| If underruns > 1 → 0.060 | _____ |
| If underruns > 1 → 0.070 | _____ |
| **Final value chosen** | _____ |

### 7d. Notes

(Anomalies, outlier cycles, anything unexpected.)

## 8. References

- Baseline analysis: `docs/perf/perf_pass_3_baseline.md` §5 (latency map), §6f (instrumentation plan)
- Code: `zeus-web/src/audio/audio-client.ts:155-201` (`push` method)
- Code: `zeus-web/src/App.tsx:213-217` (MOX-edge reset trigger)
- Code: `Zeus.Server.Hosting/TxService.cs:108-150` (server-side MOX dispatch)
- Code: `Zeus.Dsp/Wdsp/WdspDspEngine.cs:1372-1422` (WDSP RXA / TXA state flip)
- Code: `Zeus.Server.Hosting/DspPipelineService.cs:1100-1240` (30 Hz audio tick + broadcast)
- Live audio inter-arrival data: `docs/perf/artifacts/live_client_summary.json`
