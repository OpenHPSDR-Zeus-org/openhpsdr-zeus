# perf_pass_3 — Chrome client profile (via playwright, real session)

Captured 2026-05-11. Brian's Vite dev (:5173) → worktree backend (:6060,
perf_pass_3 / Release / Workstation GC + WaitToReadAsync iter1) → live
HL2 (192 kHz USB, 14.200 MHz). Window: 30 s normal RX, panadapter +
waterfall + meters rendering.

Probe: in-page `PerformanceObserver` for `longtask` + paint, rAF frame
timing, `performance.memory` heap sampling, setInterval(40ms) event-loop
lag tracker.

## Results

| Metric | Value | Verdict |
|---|---|---|
| Mean FPS | **60.00** (1800 frames in 30 s) | Frame-locked |
| Frame p50 / p95 / p99 | 16.70 / 17.60 / 17.70 ms | All frames within 16.67 ms ±1 ms; no drops |
| Event-loop lag (mean / p99) | 40.00 / 49.9 ms | Probe was setInterval(40), so true lag is mean−40 = 0 ms, p99−40 = 9.9 ms — negligible |
| Long tasks count / total ms / window % | **4 / 224 / 0.75 %** | All 4 between t=138 ms and t=540 ms = page load. **Zero steady-state long tasks.** |
| Heap start / end / Δ | 53.6 / 42.8 / **−10.8 MB** | GC reclaiming. No leak. |
| Paint timings | first-paint 224 ms, FCP 308 ms | Healthy initial render |

## Reading

The client is operating cleanly. All four >50 ms blocks landed during
the initial React mount + Zustand store hydration; once the steady-state
rAF/SignalR loop took over, the main thread stayed below 50 ms blocks
for the rest of the 30 s window. Heap shrunk, not grew — GC is keeping
up with audio buffer churn (BufferSource per frame at ~30 Hz remains).

## What this tells us about the remaining CPU complaint

The 35.7 % Zeus.Server CPU reading is **all server-side**. The client
isn't bottlenecked, isn't dropping frames, isn't leaking. The remaining
Zeus.Server CPU after Round-2 iter-1 is dominated by intrinsic WDSP
DSP work (`xrxa` → `xemnr` → `calc_gain`), not anything the client
could pull off its plate.

## What's NOT in this measurement

- Built / minified frontend (Vite dev includes source maps, HMR
  watchers, slower module loading — production wwwroot would be lower)
- CPU-time-per-function flame chart (`Profiler.start` via CDP not
  accessible via the MCP playwright wrapper; would require an external
  CDP client). The longtask + lag data above is enough to conclude
  there's no main-thread bottleneck — a flame chart would just
  attribute the remaining ~25-30 % renderer cost to existing red-line
  paths (waterfall texSubImage2D, panadapter bufferSubData) which we
  agreed not to touch.
- Mic uplink path while transmitting (RX-only window)

## Conclusion

No further client-side action needed for perf_pass_3. The client is
not on the critical path of any complaint the operator has raised.
