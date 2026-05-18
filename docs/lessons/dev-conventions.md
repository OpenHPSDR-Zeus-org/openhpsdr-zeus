# Development conventions

Small things that are easy to miss and cost time when you do.

## Port allocations

- `6060` — `Zeus.Server` (Kestrel, `Program.cs` `ListenAnyIP`). Chosen to avoid macOS `:5000` (AirPlay) and the sibling Log4YM project on `:5050`.
- `5173` — Vite dev server (`npm run dev`). Proxies API calls to `:6060`.

Only **one** server instance at a time — binding conflicts otherwise. `lsof -i :6060 -sTCP:LISTEN -t | xargs -r kill` before restarting.

## Static bundle vs Vite dev

`Zeus.Server.Hosting/wwwroot/` is gitignored but served by `app.UseStaticFiles()` from `OpenhpsdrZeus/Program.cs` (which references the `Zeus.Server.Hosting` library). `npm run build` writes directly there (Vite config's `outDir`). When debugging a frontend change, rebuild **before** reloading the browser — otherwise you're testing an old bundle.

Vite dev mode (`npm run dev` on `:5173`) reads source directly and hot-reloads — handy for fast iteration but not what a live user's browser would hit.

## Chrome `getUserMedia` over a LAN IP

Chrome treats `http://<LAN-IP>:6060/` as an **insecure origin** and silently refuses `getUserMedia` (mic access for TX waveform, etc.). Two workarounds:

1. **Launch Chrome with the flag** (recommended for LAN testing):
   ```bash
   open -n -a "Google Chrome" --args \
     --unsafely-treat-insecure-origin-as-secure=http://192.168.100.135:6060 \
     --user-data-dir=/tmp/chrome-zeus
   ```
2. **Use `http://localhost:6060/`** from the same machine the server runs on. `localhost` is a privileged secure context per the W3C spec even without TLS.

A production fix would be to serve Zeus behind HTTPS. Not on the critical path for single-user LAN dev.

## UI color conventions

Zeus uses a **single-hue amber/orange** for signal-strength visualization to match the panadapter trace:

- Reference color: `#FFA028` = `rgba(255, 160, 40, …)`.
- Source of truth: `zeus-web/src/gl/panadapter.ts:22-25` (`TRACE_R = 1.0`, `TRACE_G = 0.627`, `TRACE_B = 0.157`).
- Apply by **varying alpha**, not by shifting hue. Dim = low signal; full = high signal. No green/yellow/red transitions — red is reserved for actual alerts (e.g. SWR trip, clip warnings).

Active-state accents elsewhere (MOX-on, PTT) use the Tailwind `amber-600/80` which is close enough visually. The MeterStack reference image in `docs/prd/12-tx-feature.md` shows the target aesthetic.

## Useful repos next to this one

Per `memory/reference_upstream_sources.md`:

- `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis` — C# reference (same language as Zeus). First stop for porting TX/MOX/DSP orchestration. Key files: `Console/dsp.cs`, `Console/console.cs`, `Console/MeterManager.cs`, `Console/clsHardwareSpecific.cs`, `Console/rxa.cs`. For HPSDR Protocol 1 wire format, consult the `HPSDR/` subtree.
