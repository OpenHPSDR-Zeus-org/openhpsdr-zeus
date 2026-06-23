# FreeDV Digital Voice Integration

Status: initial implementation landed (epic `zeus-6zma`). Bench-test on HL2 pending.

## Goal

Add FreeDV (Codec2) HF digital voice to Zeus as a first-class mode: the operator
clicks **FreeDV** in the mode picker and TX/RX audio is routed through the modem
automatically — no virtual audio cables, no external app to launch. A native
FreeDV panel surfaces the same controls/telemetry as the freedv-gui app
(submode, sync, SNR, squelch, text).

## Why not embed freedv-gui

`drowe67/freedv-gui` is a **wxWidgets desktop application**, not a library or a
plugin. It has:

- **no embeddable/reparentable window** (standalone top-level wx frame),
- **no headless / remote-control API** (its hamlib integration controls the
  *radio*, not the app), and
- **PortAudio device I/O only** — hams bridge SDR-app audio to it with virtual
  audio cables (VB-Cable/VAC on Windows, PulseAudio null-sink on Linux,
  BlackHole on macOS). Zeus deliberately avoids virtual cables (fragile,
  per-platform, user-installed).

So "embed the native UI + auto-route audio" is not achievable with the GUI app
on a cross-platform target. **However**, the entire FreeDV modem lives in
`libcodec2` (`drowe67/codec2`, `freedv_api.h`, LGPL-2.1) and is fully usable as
a C library — exactly the model Zeus already uses for WDSP. freedv-gui is just
one consumer of that library.

## Architecture (what we built)

1. **Native build (`native/codec2/`)** — CMake `FetchContent` pulls
   `drowe67/codec2` pinned to tag **1.2.0**, builds a shared library
   (`codec2.dll` / `libcodec2.so` / `libcodec2.dylib`) **without LPCNet**, so the
   classic modes (700C/700D/700E/1600/800XA) ship dependency-free on every
   platform (incl. arm64 / Pi). "Install from the FreeDV repo + get updates" =
   bump the `GIT_TAG` and CI rebuilds. CI (`build-native-libs.yml`) stages the
   artifact alongside WDSP/miniaudio. See `native/codec2/VENDORING.md`.

2. **P/Invoke + modem (`Zeus.Dsp.FreeDv`)** — a *separate assembly* (so it owns
   its own `NativeLibrary.SetDllImportResolver` for `codec2` without touching the
   load-bearing WDSP resolver in `Zeus.Dsp`). `FreeDvModem` opens two `freedv`
   instances (TX + RX, default **700D**), resamples 48 kHz ⇄ 8 kHz with a
   dependency-free polyphase 6:1 FIR, and presents both directions as
   **streaming, frame-synchronous, in-place filters** (fixed block size,
   internally buffered, silence-padded while starved/unsynced, latency bounded to
   ~250 ms). Telemetry: sync + SNR via `freedv_get_modem_stats`.

3. **Mode plumbing** — `RxMode.FreeDv` (byte 10, append-only). FreeDV is a
   *Zeus-level* mode only: `WdspDspEngine.MapMode` already defaults unknown modes
   to **USB**, so the radio runs USB underneath and the modem occupies the SSB
   audio band. All RadioService filter-family switches default to the SSB/USB
   family, so FreeDV needs no special filter handling.

4. **Audio insert points**
   - **RX:** `DspPipelineService` post-demod (RX0), before the RX plugin/squelch —
     replaces the received modem audio with decoded speech.
   - **TX:** `TxAudioIngest`, after mic accumulation and before
     `engine.ProcessTxBlock` — replaces mic speech with the transmitted modem
     signal; WDSP's USB TXA then SSB-modulates it. TX buffer flushed on the MOX
     falling edge so each over starts clean.
   Both gate on `FreeDvService.Active`, which `DspPipelineService` reconciles
   from the live RX0 mode each tick.

5. **Service + API** — `FreeDvService` (singleton) owns the modem and exposes
   `GET /api/freedv/status` and `PUT /api/freedv/config`. The frontend adds a
   **FreeDV** mode button and a native `FreeDvPanel` (submode select, sync lamp,
   SNR readout, squelch, TX text) polling status at ~4 Hz. When the codec2
   library is absent, `NativeAvailable=false` and FreeDV passes audio through
   unchanged; the panel shows an unavailable hint.

## Deferred / follow-ups

- **RX/TX text sidechannel** (callsign) via `freedv_set_callback_txt` — wiring
  stubbed (`RxText` reported null).
- **2020/2020B (LPCNet)** — intentionally excluded; gate behind an optional
  LPCNet build later.
- **On-air bench validation** on HL2 (resampler quality, TX level into WDSP USB,
  RX sync) — the modem path compiles and is unit-testable but has not been keyed
  on real hardware.

## Gotcha worth remembering

`Zeus.Server.Hosting` types live in namespace **`Zeus.Server`**, not
`Zeus.Server.Hosting` (project name ≠ root namespace). A new file using the
project-name namespace will compile but be invisible to sibling files that lack
an explicit `using`.
