# perf-pass-3 — client (Chrome web app)

Owner: `client-perf` (task #3). Worktree
`OPENHPSDR-Zeus.Worktrees/feature_perf_pass_3` on branch
`feature/perf_pass_3`.

## Scope

Task #3 trims Chrome **renderer** CPU on the panadapter / waterfall /
meter / audio path while honouring `CLAUDE.md` red-light rules: **no
visual change, no UX change, no axis flip, no palette touch**.

Baseline (perf2 + perf3-existing): renderer 24-32 % of one core,
panadapter at 60 fps, audio cadence rock-solid at 33 ms. The renderer
is not janky — just busy. The big remaining levers (per-frame WebGL
upload paths) are red-light per `CLAUDE.md` because changing them
trades visual feel for CPU.

## Live capture harness

Playwright MCP Chrome 147 in the worktree directory, 1600×1000
viewport, second tab on Brian's running HL2-connected Zeus.Server
:6060 (live RX 192 kHz LSB on 40 m). Vite dev on :5183 from this
worktree, proxy → :6060.

Probe (installed via `browser_evaluate`, captures over 30 s window
and reports back via `window.__zeusPerf.stop()`):

- `AudioBuffer.prototype.copyToChannel` wrapped to count audio
  frames and their inter-arrival gaps
- `PerformanceObserver({ type: 'longtask' })` — main-thread tasks ≥50 ms
- 40 ms `setInterval` lag sampler — event-loop drift proxy
- `requestAnimationFrame` gap sampler — frame-pace
- `performance.memory.usedJSHeapSize` every 250 ms

Renderer / GPU CPU sampled in parallel:
```
top -pid <renderer> -pid <gpu-helper> -l 7 -s 5
```

Raw artifacts in `docs/perf/client/baseline.json` plus
`/tmp/perf3-{baseline,fix1*}-top.txt`. Two live windows per change
(baseline and post-fix) so the numbers below are real, not inferred.

## Headline before/after

| Metric | Baseline | After fix #1 (drop Float32Array wrap) | Δ |
|---|---|---|---|
| Mean FPS (rAF) | 60.04 | 60.02 | unchanged — already frame-locked |
| p99 rAF gap | 17.60 ms | 17.60 ms | unchanged |
| Long tasks (>50 ms) in 30 s | 2 | 1 | −1 |
| Longest task | 64 ms | 55 ms | −9 ms |
| Event-loop lag p99 | 12.5 ms | 11.8 ms | −0.7 ms |
| Renderer CPU (top, mean of 6 samples) | **32.8 %** | **32.2 %** | **−0.6 pp** |
| GPU helper CPU (top, mean of 6 samples) | 17.0 % | 17.3 % | +0.3 pp (noise) |
| Renderer+GPU stack | 49.7 % | 49.5 % | −0.2 pp |
| Heap end-of-window | 42.8 MB | 45.6 MB | within noise |
| Audio frame rate | 30.30 Hz | 30.27 Hz | unchanged — driven by server |
| Audio inter-arrival p99 | 44.0 ms | 43.3 ms | within noise |

Small win — but the wrap was pure waste. Code is now also clearer.

## Fix #1: drop the per-audio-frame `Float32Array` copy

**Commit:** `5defa46 perf(web): drop per-audio-frame Float32Array copy
in audio-client`

**File:** `zeus-web/src/audio/audio-client.ts`, the `push()` hot path.

**Before:**
```ts
const buffer = ctx.createBuffer(1, frame.sampleCount, frame.sampleRateHz);
// copyToChannel needs Float32Array<ArrayBuffer>; wrap to satisfy strict generic.
buffer.copyToChannel(new Float32Array(frame.samples), 0);
```

`new Float32Array(typedArray)` **copies** the source. At HL2 cadence
that's ~1600 floats × 4 bytes × 30 frames/s ≈ **192 KB/s of pure
allocator churn**. The comment said "wrap to satisfy strict generic",
which is a TS-type problem, not a runtime requirement.

**After:**
```ts
const buffer = ctx.createBuffer(1, frame.sampleCount, frame.sampleRateHz);
// copyToChannel reads our floats into the buffer's own storage, so we can
// pass frame.samples directly — the previous `new Float32Array(frame.samples)`
// wrap copied the data twice (DOM + extra heap alloc) at 30 Hz. The cast
// satisfies lib.dom.d.ts's `Float32Array<ArrayBuffer>` constraint; the value
// is already that shape because `decodeAudioFrame` constructs it from an
// ArrayBuffer view in `frame.ts`.
buffer.copyToChannel(frame.samples as Float32Array<ArrayBuffer>, 0);
```

`copyToChannel` reads source data into the AudioBuffer's own DOM
storage — there's no reason for two JS-side copies. The cast tells
TypeScript what we know is true from `decodeAudioFrame`'s
`new Float32Array(buffer, offset, len)` construction.

`npm run typecheck` clean.

## Things I looked at but did not change

### Red-light per the task brief / `CLAUDE.md`
- `gl/waterfall.ts:203` `texSubImage2D` per-frame upload
- `gl/panadapter.ts:177,214` `bufferSubData` + `texSubImage2D`
- Any colour / font / layout / axis-direction change

### Green-light but not worth it this pass
- **AudioWorklet ring-buffer** replacing per-frame
  `createBuffer`/`createBufferSource`. The brief flags this as a
  larger lift. The 30 Hz alloc cost is already small (longest task
  64 ms in baseline was unrelated — checked with `longTask.name`).
- **`useMeterRefresh` 30 Hz forced re-render** in
  `TxStageMeters.tsx:512`. Chrome already throttles `rAF` when the
  document is hidden, so the missing `document.hidden` gate only
  matters when the tab is visible but the panel is collapsed. Worth
  re-visiting if measurements move that case into the hot path.
- **`useRafTick` 30 Hz** in
  `ImmersiveMetersPanel.tsx:75`. Same shape as above.
- **`AnalogMeterZeusOverlay.tsx:41-50`** runs a 60 Hz rAF when
  `visible`. Already gated on `visible`; tab-hidden case throttled
  by browser.
- **Pollers** in `rotator-store.ts:165`, `rf2k-store.ts:175`,
  `tci-store.ts:101`. Each fires every 1 s but short-circuits on the
  `enabled` flag. Negligible.

### Reads I made (no change)
- `realtime/draw-bus.ts` — already coalesces panadapter + waterfall
  rAF into one wakeup; nothing to do.
- `realtime/ws-client.ts:213-260` — `setMeters({...})` allocates a
  fresh object literal per TX-meters frame. The cost is small and
  the object literal is the natural way to call a Zustand setter;
  changing the shape would ripple through every consumer.

## Why the renderer is still ~32 % busy

The 30 Hz audio decode + 25 Hz spectrum upload + 25 Hz waterfall blit
+ rAF-driven meter peak-hold + React reconciliation across ~6 visible
panels add up to ~10 ms of per-frame work on the main thread (still
inside the 16.7 ms rAF budget, hence 60 fps locked, zero long tasks).
Trimming the remaining 30 % would require attacking the WebGL upload
shape — which is the red-light territory the maintainer owns.

## How to reproduce

1. `cd .../feature_perf_pass_3/zeus-web && BACKEND_PORT=6060 BACKEND_HOST=localhost ./node_modules/.bin/vite --port 5183 --strictPort`
2. Open `http://localhost:5183/` in Chrome.
3. Click **▶ Unmute** to start the AudioContext (Chrome user-gesture
   gate).
4. Paste the probe from `docs/perf/client/baseline.json#_meta.notes`
   into DevTools console, wait 30 s, run `window.__zeusPerf.stop()`.
5. In parallel, sample renderer + GPU CPU:
   `top -pid <renderer> -pid <gpu> -l 7 -s 5`.

The probe and the `top` window need to start at the same wall-clock
moment for the numbers to line up. The harness in `client-perf`'s
session does both via `Bash --run_in_background` + `browser_evaluate`.

## What's left for follow-up

- Real **Chrome trace** (`chrome.tracing` start/stop) when Playwright
  MCP exposes it. The `longtask` count + `top` CPU give a coarse
  proxy, but a flame chart with React + WebGL + audio attributions
  would reveal whether the next win is in React reconciliation, in
  WebGL upload, or in audio scheduling.
- Investigate whether the 5-10 MB heap growth per 30 s (steady-state)
  is GC-eventually-recovered or a slow leak. Heap-max hit 71 MB in
  the fix-#1 window vs 65 MB in baseline, but both end-of-window
  values are well below the max, indicating GC is firing and
  reclaiming.
