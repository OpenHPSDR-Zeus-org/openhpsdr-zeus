# librnnoise (NR3 / RNNoise) — vendoring notes

This directory holds an in-tree copy of **RNNoise** (xiph), the recurrent-neural
denoiser that WDSP's `rnnr.c` (NR3) links against. It is **not yet populated** —
NR3 is gated `OFF` in `native/wdsp/CMakeLists.txt` (`WDSP_WITH_NR3`) until the
source is vendored here. The C#/TypeScript side of NR3 is already wired; this is
the one remaining step to make NR3 functional, and it must run on the CI native
runners (cross-platform) — see "CI" below.

## Provenance

Upstream: <https://gitlab.xiph.org/xiph/rnnoise> (mirror:
<https://github.com/xiph/rnnoise>). License: **BSD-3-Clause** — compatible with
Zeus's GPL-2.0-or-later distribution (BSD → GPL is one-way compatible). Preserve
the upstream `COPYING` / `LICENSE` verbatim when vendoring.

`rnnr.c` itself (the ringbuffer + AGC wrapper around RNNoise) is MW0LGE's Thetis
code, already in `native/wdsp/rnnr.c`. Only the RNNoise library proper is
vendored here.

## Re-vendoring

Pin a specific upstream tag/commit (record it here when you do):

```sh
RNNOISE_REF=<tag-or-commit>      # e.g. a release tag; record it in this file
git clone https://github.com/xiph/rnnoise /tmp/rnnoise
git -C /tmp/rnnoise checkout "$RNNOISE_REF"
rm -rf native/rnnoise/src native/rnnoise/include
mkdir -p native/rnnoise/src native/rnnoise/include
cp /tmp/rnnoise/src/*.c native/rnnoise/src/
cp /tmp/rnnoise/src/*.h native/rnnoise/src/   2>/dev/null || true
cp /tmp/rnnoise/include/rnnoise.h native/rnnoise/include/
cp /tmp/rnnoise/COPYING native/rnnoise/COPYING
# keep this VENDORING.md
```

The CMake glob (`native/wdsp/CMakeLists.txt`, `WDSP_WITH_NR3` block) compiles
`native/rnnoise/src/*.c` as a STATIC sub-target and links it into `libwdsp`.

## No bundled model (deliberate)

Zeus ships **no** RNNoise model. NR3 stays inert until the operator installs a
weights file via the DSP menu (which calls `RNNRloadModel` →
`rnnoise_model_from_filename`). To guarantee that, the library is compiled
**without the default model**, matching xiph's `--disable-default-model`:

- The CMake block **excludes** the default-weights translation unit
  (`*rnnoise_data*.c`) from the source glob.
- It defines `USE_WEIGHTS_FILE`, which gates off the stock-model code path so
  `rnnoise_create(NULL)` returns an inert denoiser (audio passes through) rather
  than referencing the excluded weights.

Verify against the pinned tag: if upstream's macro/filenames differ, adjust the
`EXCLUDE REGEX` and `target_compile_definitions` in the CMake block to match.
The acceptance check is: `libwdsp` links with no undefined `rnnoise_*` symbols,
and with no model loaded NR3 is a clean pass-through.

## Enabling the build

```sh
cmake -S native/wdsp -B build -DWDSP_WITH_NR3=ON     # plus the usual FFTW flags
```

`rnnr.c` is then compiled instead of `stubs/nr3/rnnr_stub.c`, and `libwdsp`
exports `SetRXARNNRRun`, `SetRXARNNRPosition`, and `RNNRloadModel`. The managed
side detects these via `WdspDspEngine.Nr3RnnrAvailable` and reveals NR3 in the UI
once a model is also installed.

## CI

Flip `-DWDSP_WITH_NR3=ON` in `.github/workflows/build-native-libs.yml` and
`release.yml` alongside the existing `WDSP_WITH_NR4=ON`, so the shipped
per-platform `libwdsp` binaries (macOS arm64/x64, Linux x64/arm64, Windows x64)
carry the RNNR exports. Until those binaries are rebuilt, `Nr3RnnrAvailable` is
false and NR3 stays hidden — by design, not a regression.

A loader smoke test mirroring `Zeus.Dsp.Tests/WdspNativeLoaderTests` (which
asserts the NR4/SBNR exports) should assert the RNNR exports once NR3 ships.

## Models for operators

Zeus links to documentation rather than hosting a model. Operators supply their
own RNNoise weights file (e.g. a community HF-tuned model, or one trained per the
upstream training pipeline) and install it from the DSP menu (upload or URL).
