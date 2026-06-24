# RADE V1 integration — design & implementation plan

**Status:** Phase 1 (UI/contract groundwork) landed. Native decoder NOT yet
implemented — this document is the execution plan for it.

## Why

RADE V1 ("Radio Autoencoder") is FreeDV's neural HF voice mode and is now the
default in freedv-gui 2.1.0 (selector: **RADEV1 / 700D / 700E / 1600**). Most
on-air FreeDV activity is moving to it. Zeus ships only the classic codec2 1.2.0
modes (700C/700D/700E/1600/800XA, `LPCNET=OFF`), so a RADE signal decodes to
**garbage on every classic mode** — a strong signal still won't produce coherent
audio, and the classic decoders false-sync on RADE's OFDM-like energy.

This was diagnosed on-air on 2026-06-23 (40 m): operator on a strong signal,
no decode on any classic mode, station confirmed on RADEV1.

See also: `docs/designs/freedv-integration.md` (the classic-mode integration this
builds on) and the `rade-v1-integration` agent memory.

## What RADE is (decode pipeline)

RADE is a neural codec: a NN encoder compresses speech to a latent vector
modulated onto an OFDM waveform; the receiver's NN decoder reconstructs vocoder
*features*, which the **FARGAN** vocoder synthesises into audio.

RX decode chain Zeus must implement:

```
radio SSB demod (real, 48 kHz)
  → resample 48k→8k
  → real→complex (RADE modem input is complex IQ @ 8 kHz)
  → rade_rx()  ──► vocoder FEATURES (not audio)  [+ sync/SNR telemetry]
  → FARGAN vocoder ──► speech @ 16 kHz
  → resample 16k→48k
  → audio bus
```

Two things make this materially harder than classic FreeDV:
1. **Two ML stages**: `rade_rx` emits *features*; FARGAN turns features→speech.
2. **Complex modem IO @ 8 kHz**, **16 kHz speech** (not the classic real-short,
   8 kHz-both world). New resampler rates (48k↔16k = 3:1) and a real→complex
   front end are required. Zeus's `FreeDvResampler` is 48k↔8k (6:1) only.

## Native C API (`rade_api.h`, drowe67/radae)

```c
#define RADE_MODEM_SAMPLE_RATE  8000
#define RADE_SPEECH_SAMPLE_RATE 16000
#define RADE_USE_C_ENCODER 0x1
#define RADE_USE_C_DECODER 0x2

void          rade_initialize(void);
void          rade_finalize(void);
struct rade  *rade_open(char model_file[], int flags);
void          rade_close(struct rade *r);
int           rade_version(void);
int           rade_nin(struct rade *r);
int           rade_nin_max(struct rade *r);
int           rade_n_features_in_out(struct rade *r);
// RX: provide nin() complex samples in rx_in[]; returns >0 when features_out[] valid.
int           rade_rx(struct rade *r, float features_out[], int *has_eoo_out,
                      float eoo_out[], RADE_COMP rx_in[]);
int           rade_sync(struct rade *r);
float         rade_freq_offset(struct rade *r);
int           rade_snrdB_3k_est(struct rade *r);
// TX (later): rade_n_tx_out, rade_tx, rade_tx_eoo, rade_tx_set_eoo_bits.
```

`RADE_COMP` is a `{float real; float imag;}` pair. FARGAN synthesis is a separate
call (Opus/LPCNet `fargan_*` API) consuming `features_out` → 16 kHz PCM.

## ⚠ Phase 2 build investigation — VERIFIED BLOCKER (2026-06-23)

A real build attempt (clone + read the actual CMake/source, toolchain present:
cmake 4.3, gcc/MinGW, clang, Python 3.12) found that **upstream `drowe67/radae`
`librade` cannot be vendored dependency-free.** Concretely:

- `src/rade_api.c` **unconditionally** `#include <Python.h>` + `numpy/arrayobject.h`
  (no compile guard). `rade_open()` imports Python modules `radae_txe`/`radae_rxe`
  and instantiates PyTorch classes pointing at a `.pth` checkpoint.
- `src/CMakeLists.txt` always `target_link_libraries(rade Python3::Python ...)`.
- The `RADE_USE_C_ENCODER/DECODER` flags only move the **neural core** to C
  (`rade_enc.c`/`rade_dec.c` with committed weights `rade_*_data.c`); the **OFDM
  modem + sync still run in embedded Python** (`radae_rxe.py`). So the flags do
  NOT yield a Python-free decoder.
- Confirmation: even **freedv-gui's official Windows release ships an embedded
  Python and downloads the PyTorch modules from the internet at install time.**

Embedding CPython + PyTorch + a `.pth` is antithetical to Zeus's
cross-platform/arm/Pi, dependency-free design. So the "vendor librade like
codec2" plan below is **not possible against upstream main today.**

### The only dependency-free candidates
1. **`peterbmarks/radae_nopy`** — a *pure-C* port (no Python.h, no interpreter;
   OFDM modem + sync + neural core all in C; weights compiled in from
   `rade_enc_data.c`/`rade_dec_data.c`; BSD-2). **This is the viable base.** But:
   tested Linux/macOS only (no Windows/arm), and **the repo says "no longer
   recommended"** — FreeDV is consolidating future work into a `freedv-backend`
   repo.
2. **Upstream C port (`freedv-backend` / future librade V2)** — the *supported*
   long-term path, "planned… eventually negate the need for Python." Not ready.

### Revised recommendation
RADE-into-Zeus today = fork/adopt **`radae_nopy`** as the native base and port it
to Windows (MinGW+autotools, like freedv-gui) + arm, OR **wait/track** the upstream
C port (`freedv-backend`). The original codec2-style "FetchContent drowe67/radae"
approach is a dead end (drags in Python).

> **Maintainer decision (2026-06-23): adopt `radae_nopy`. "Put RADE in."**

### ✅ PROVEN — radae_nopy decodes a real off-air RADE signal (2026-06-23)

Built `peterbmarks/radae_nopy` in WSL Ubuntu 24.04 (cmake + autotools; Opus built
with `--enable-osce --enable-dred` for FARGAN, then `librade.so`) and decoded the
repo's off-air sample end-to-end:

```
sox FDV_offair.wav -r 8000 -e float -b 32 -c 1 -t raw - \
  | real2iq | radae_rx > features.f32        # OFDM demod + sync + neural decode
lpcnet_demo -fargan-synthesis features.f32 - \
  | sox -t .s16 -r 16000 -c 1 - decoded.wav   # FARGAN vocoder → 16 kHz speech
```

Result: `radae_rx` reported **2717 modem frames, 2624 valid outputs, sync acquired,
SNR ≈ 12.6 dB**; `decoded.wav` = 16 kHz mono, max amplitude 0.9999 (real speech).
**The dependency-free C decode path works.** RADE is now an integration task, not a
feasibility risk.

Key facts confirmed from the real source/build:
- **Weights compiled in** (`rade_enc_data.c`/`rade_dec_data.c`) — **no model file to
  ship**. `rade_open(model_file, …)` ignores the path for weights.
- **Two libraries**: `librade` (IQ↔features, the OFDM modem+sync+NN core; links opus)
  AND **Opus/FARGAN** (features↔16 kHz audio). RADE's `rade_api.h` does NOT
  encapsulate the vocoder — Zeus must call the Opus FARGAN C API (`fargan_*` /
  `lpcnet_demo -fargan-synthesis` path) for the features→speech stage.
- Bundled **kiss_fft** (no FFTW). C11. `RADE_PYTHON_FREE=1`. BSD-2.
- Opus is an **autotools** ExternalProject (`autogen.sh && ./configure
  --enable-osce --enable-dred`), patched via `src/opus-nnet.h.diff`. Windows build
  = MinGW + autotools (freedv-gui's approach); arm = `--disable-rtcd`.
- `rade_api.h` streaming RX: `rade_nin()` → `rade_rx(r, features_out, &has_eoo,
  eoo_out, rx_in)` (complex `RADE_COMP` in, feature floats out), `rade_sync()`,
  `rade_snrdB_3k_est()`. EOO frame carries an 8-char callsign
  (`rade_rx_get_eoo_callsign`) — maps nicely to the existing FreeDV RX-text UI.

> Note: `radae_nopy` is upstream-deprecated in favour of `tmiw/freedv-backend`
> (same C port lineage). Pin radae_nopy now for the proven build; track
> freedv-backend for the long-term base.

---

## Build — the hard part (assumes a Python-free base per the box above)

The native build is the bulk of the effort and the reason this is multi-day:

- **License:** BSD-2-clause (compatible). Good.
- **drowe67/radae root `CMakeLists.txt` requires `Python3` (Interpreter,
  Development, NumPy)** for the normal build, and pulls **Opus's `spl_fargan`
  branch** via `cmake/BuildOpus.cmake` for FARGAN. There is a cross-compile path
  (`-DPython3_ROOT_DIR`, manual `Python3::Python`/`Python3::NumPy` targets) but it
  still wants Python present at configure time.
- The **C-only decoder** path (`RADE_USE_C_DECODER`) needs: the C encoder/decoder
  sources + the FARGAN/Opus C library + the **model weights as C** (`rade_enc_data.c`
  / `rade_dec_data.c`, generated from a `.pth` checkpoint via the repo's export
  scripts), or a runtime binary blob loaded by `rade_open(model_file, ...)`.
- **This reverses the deliberate `LPCNET=OFF` / dependency-free decision** in
  `native/codec2/VENDORING.md` — RADE brings the FARGAN/LPCNet ML vocoder. Accepted
  by the maintainer (2026-06-23): bigger binaries + more CPU, fine on desktop,
  heavier but feasible on Pi (RADE runs on TI AM625 / Librem 5; ~32 MMACs, 2×1 MB
  weights). Not micro-controllers.
- **Cross-platform** (the codec2 precedent): MSVC can't compile the C99 `_Complex`
  OFDM/filter code, so Windows builds via **clang-cl** with an `if(NOT MSVC)`
  patch (see `native/codec2/patch-codec2-msvc.cmake`). RADE will need the same
  treatment plus the Opus/FARGAN branch building under clang-cl and arm.

### Recommended build approach
1. Pin a drowe67/radae release tag (no moving HEAD), FetchContent like codec2.
2. Use the C decoder path; **avoid the Python build coupling** by exporting the
   model weights to C once (committed or downloaded), and building only the
   `rade` + FARGAN/Opus C targets.
3. Vendor the model: ship `rade_enc_data.c`/`rade_dec_data.c` (compiled-in) OR a
   binary blob distributed via the existing `FreeDvNativeInstaller` download
   affordance (codec2 already does this). ~2 MB total.
4. Build native binaries in CI (`build-native-libs.yml`) for win-x64/arm64,
   linux-x64/arm64, osx-arm64/x64; emit `rade.dll` / `librade.{so,dylib}` next to
   `codec2.*`. Reference freedv-gui's CMake for the cross-platform recipe.

## Zeus-side architecture

RADE is **NOT** a `freedv_open` submode — different API, complex IO, FARGAN, 16 kHz.
Already in place (Phase 1, committed):
- `FreeDvSubmode.RadeV1` (byte 5). `FreeDvModem.OpenLocked` short-circuits it to a
  clean passthrough so a classic decoder is never mis-opened on a RADE signal.
- `FreeDvModem.RadeAvailable` / `FreeDvStatusDto.RadeAvailable` (false today).
- Panel: RADEV1 shown (matching freedv-gui), gated with a "decoder not installed"
  notice. Mode list = RADEV1/700D/700E/1600; 700C/800XA stay backend-valid.

To build (the native phase):
- `native/radae/CMakeLists.txt` + `VENDORING.md` (mirror `native/codec2/`).
- `Zeus.Dsp.FreeDv/RadeNativeMethods.cs` + `RadeNativeLoader.cs` (own
  `SetDllImportResolver` for `rade`, mirror the codec2 ones) — P/Invoke `rade_api.h`
  + the `fargan_*` synth.
- `Zeus.Dsp.FreeDv/RadeModem.cs` — the streaming decode (48k→8k, real→complex,
  `rade_rx`→features→FARGAN→16k, 16k→48k). Same realtime discipline as
  `FreeDvModem` (lock-free hot path, Dekker-fenced handle handoff).
- `FreeDvResampler`: add 48k↔16k (3:1) + a real→complex helper.
- Route `FreeDvSubmode.RadeV1` in `FreeDvService`/`FreeDvModem` to `RadeModem`;
  flip `RadeAvailable` to a real `RadeNativeLoader.TryProbe()`.
- `FreeDvNativeInstaller`: add the RADE model (and lib, if not bundled) to the
  download/stage path so RADEV1 lights up live like codec2 does.

## Phases & status

| Phase | Work | Status |
|------|------|--------|
| 0 | Diagnose (RADE on-air, classic modes fail), scope, research API/build | ✅ done |
| 1 | UI/contract groundwork: `RadeV1` submode, gated panel, `RadeAvailable` | ✅ done (committed) |
| 2a | Prove the dependency-free decoder (build radae_nopy, decode off-air sample) | ✅ done (WSL, 2026-06-23) |
| 2b | **`zeus_rade` shim** — one C lib: complex IQ → `rade_rx` → FARGAN → 16 kHz PCM | ✅ done + validated (WSL: 2628 synced ticks, 14 dB, 5:14 speech out) |
| 2c | `native/radae/` vendoring CMake: fetch radae_nopy + build Opus/FARGAN + shim into one shared lib, cross-platform (Win MinGW+autotools, arm `--disable-rtcd`), CI binaries | ⏳ next (the hard one) |
| 4 | `RadeModem` P/Invoke the shim (`zeus_rade_*`) + 48k↔16k resample + real→complex; flip `RadeAvailable` | ⏳ (no model files; weights compiled in) |

### zeus_rade shim (PROVEN 2026-06-23)
`native/radae/shim/{zeus_rade.h,zeus_rade.c,zeus_rade_test.c,build_test_wsl.sh}` —
a thin C wrapper giving Zeus a single P/Invoke surface instead of binding both
`rade_api.h` and Opus's FARGAN. Verified by compiling against the radae_nopy +
Opus build and decoding `FDV_offair.wav` through `zeus_rade_rx()` alone:
`pcm_samples=5033280, synced=2628/2717, last_snr=14 dB`, 5:14 of real speech.

Concrete dims learned at runtime: `rade_n_features_in_out=432` (= 12 × 36-float
frames per `rade_rx`), `Nmf=960`, `n_eoo_bits=180`. FARGAN: prime 5 frames
(`fargan_cont`) then `fargan_synthesize` 160 samples/frame; weights built in.
Known issue: EOO callsign decode returns garbage — revisit (likely the EOO
soft-bit handoff). Both RADE and FARGAN weights are compiled in → **no model
files to ship**. The shim re-primes FARGAN on each sync re-acquire.
| 3 | Model weights export + bundling/install | ⏳ |
| 4 | `RadeModem` + P/Invoke + complex IO + FARGAN + 48k↔16k resample; flip `RadeAvailable` | ⏳ |
| 5 | On-air validation vs a live RADEV1 station | ⏳ |

## Open questions for Phase 2
- Exact FARGAN C entry points + state struct (Opus `spl_fargan` branch) and how
  freedv-gui drives features→PCM. Read freedv-gui's RADE RX path as the reference.
- Compiled-in weights vs. downloaded blob (size vs. update story). Lean: download
  via `FreeDvNativeInstaller` to keep the repo/binary lean.
- clang-cl + arm build of Opus/FARGAN — expect codec2-style `if(NOT MSVC)` patches.
- Real→complex front end: does `rade_rx` want an analytic (Hilbert) signal or the
  real SSB audio packed as `{re=x, im=0}`? Confirm against freedv-gui.
