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
to Windows (clang-cl, exactly like the codec2 MSVC patch) + arm, OR **wait/track**
the upstream C port (`freedv-backend`). Both are real multi-day efforts; the first
builds on a deprecated base, the second isn't available yet. **This is a maintainer
decision** — pick the base before any native CMake work. The original codec2-style
"FetchContent drowe67/radae" approach is a dead end (drags in Python).

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
| 2 | `native/radae/` vendoring: build `rade`+FARGAN cross-platform, CI binaries | ⏳ next (the hard one) |
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
