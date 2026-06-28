# librnnoise (NR3 / RNNoise) — vendoring notes

This directory holds an in-tree copy of **RNNoise** (xiph), the recurrent-neural
denoiser that WDSP's `rnnr.c` (NR3) links against. NR3 is gated `ON` by default
in `native/wdsp/CMakeLists.txt` (`WDSP_WITH_NR3`); the source below is compiled
as a STATIC sub-target and embedded into `libwdsp`.

## Pinned upstream

- Repo: <https://github.com/xiph/rnnoise> (mirror of
  <https://gitlab.xiph.org/xiph/rnnoise>)
- Commit: **`70f1d256acd4b34a572f999a05c87bf00b67730d`** (branch `main`, the
  modern weights-file / DNN architecture — this is the variant whose API
  (`rnnoise_model_from_filename`, `rnnoise_model_free`) `rnnr.c` calls).
- Model version (for the architecture header below):
  `0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37`
- License: **BSD-3-Clause** (see `COPYING`) — compatible with Zeus's
  GPL-2.0-or-later distribution (BSD → GPL is one-way compatible).

`rnnr.c` itself (the ringbuffer + AGC wrapper around RNNoise) is MW0LGE's Thetis
code, already in `native/wdsp/rnnr.c`. Only the RNNoise library proper is
vendored here.

## What is vendored (and what is deliberately not)

`native/rnnoise/`
- `include/rnnoise.h` — public API.
- `src/*.c` (+ `src/*.h`) — the **library** source set only (per upstream's
  `Makefile.am` `RNNOISE_SOURCES`): `celt_lpc denoise kiss_fft nnet nnet_default
  parse_lpcnet_weights pitch rnn rnnoise_tables`, plus the special
  `rnnoise_data.c` described below.
- `src/x86/*.h` — headers only (referenced by `nnet`'s arch dispatch).
- `COPYING`.

Deliberately **excluded**:
- Upstream's standalone tools (`dump_features.c`, `dump_rnnoise_tables.c`,
  `write_weights.c`) — they carry their own `main()`.
- `src/x86/*.c` (SSE4.1/AVX2/RTCD) — `RNN_ENABLE_X86_RTCD` is left undefined, so
  `rnn_select_arch()` resolves to the portable C path on every platform
  (`src/cpu_support.h`). Keeps the build cross-platform-clean with no per-file
  `-march` juggling.

### `rnnoise_data.{c,h}` — the no-bundled-model trick

Zeus ships **no** RNNoise model. NR3 is inert (clean pass-through) until the
operator installs a weights file from the DSP menu, which calls `RNNRloadModel`
→ `rnnoise_model_from_filename`.

Upstream couples three things in the model:
- `src/rnnoise_data.h` (~1 KB) — the network **architecture** (layer dimensions
  like `CONV1_STATE_SIZE`, the `RNNoise` struct, the `init_rnnoise` prototype).
  `rnn.h`/`rnn.c` `#include` it **unconditionally**, so it is required to
  *compile*, even in weights-file mode.
- `src/rnnoise_data.c` (~78 MB) — the default weight arrays **and** the
  `init_rnnoise()` function. The weight arrays are all inside
  `#ifndef USE_WEIGHTS_FILE`; `init_rnnoise()` is not (it binds weights by name
  from the runtime-loaded list, so it is independent of the baked weights).

So we cannot simply drop `rnnoise_data.c` (we'd lose `init_rnnoise`, which
`denoise.c` calls), nor compile it whole (78 MB of weights would ship). Instead:

- We vendor the small **architecture header** `src/rnnoise_data.h` verbatim.
- We vendor a **minimal `src/rnnoise_data.c`** containing only the
  `init_rnnoise()` function (extracted verbatim from upstream) — exactly what the
  full file preprocesses to under `-DUSE_WEIGHTS_FILE`. The repo carries
  kilobytes, not 78 MB.
- The CMake block defines `USE_WEIGHTS_FILE`, so `rnnoise_create(NULL)` yields an
  inert denoiser instead of referencing any default weights.

A runtime model loaded via the DSP menu must match this architecture (i.e. a
standard RNNoise `main`-branch model at the pinned `model_version`).

## Re-vendoring

```sh
RNNOISE_REF=70f1d256acd4b34a572f999a05c87bf00b67730d   # record the new ref here
git clone https://github.com/xiph/rnnoise /tmp/rnnoise
git -C /tmp/rnnoise checkout "$RNNOISE_REF"
# Architecture header (needed to compile) comes from the model download:
( cd /tmp/rnnoise && ./download_model.sh )             # fetches src/rnnoise_data.{c,h}

rm -rf native/rnnoise/src native/rnnoise/include
mkdir -p native/rnnoise/src/x86 native/rnnoise/include

# Library sources only — NOT dump_features.c / dump_rnnoise_tables.c /
# write_weights.c, and NOT src/x86/*.c.
for f in celt_lpc denoise kiss_fft nnet nnet_default parse_lpcnet_weights \
         pitch rnn rnnoise_tables; do
  cp "/tmp/rnnoise/src/$f.c" native/rnnoise/src/
done
cp /tmp/rnnoise/src/*.h        native/rnnoise/src/
cp /tmp/rnnoise/src/x86/*.h    native/rnnoise/src/x86/
cp /tmp/rnnoise/src/rnnoise_data.h native/rnnoise/src/      # architecture header
cp /tmp/rnnoise/include/rnnoise.h  native/rnnoise/include/
cp /tmp/rnnoise/COPYING            native/rnnoise/COPYING

# Minimal rnnoise_data.c = just the init_rnnoise() function (the part of
# upstream's rnnoise_data.c that survives -DUSE_WEIGHTS_FILE). Extract the
# function body verbatim and prepend `#include "rnnoise_data.h"` + a header
# comment. If upstream's architecture changes, re-extract this alongside the
# header so the two stay in lockstep.
```

## Acceptance check (run locally before pushing)

Compile the vendored library set with `-DUSE_WEIGHTS_FILE` and link a tiny driver
that calls `rnnoise_create(NULL)` / `rnnoise_process_frame` / `rnnoise_destroy`.
It must link with **no undefined `rnnoise_*` symbols** and, with no model loaded,
behave as a clean pass-through. (Validated on gcc/x86_64 during this vendor;
`frame_size` is 480.)

## CI

`-DWDSP_WITH_NR3=ON` is already set in `.github/workflows/build-native-libs.yml`
and `release.yml` for every `libwdsp` job (macOS arm64, Linux x64/arm64, Windows
x64/arm64), alongside `WDSP_WITH_NR4=ON`. Each job has a **Verify RNNR exports**
step asserting the 3 exports (`SetRXARNNRRun`, `SetRXARNNRPosition`,
`RNNRloadModel`) — the analogue of the existing SBNR-export check — so a silently
stub-based build fails CI. The managed side detects these via
`WdspDspEngine.Nr3RnnrAvailable` and reveals NR3 in the UI once a model is also
installed.

Until the per-platform `libwdsp` binaries committed under
`Zeus.Dsp/runtimes/<rid>/native/` are rebuilt by these workflows and committed,
`Nr3RnnrAvailable` is `false` and NR3 stays hidden — by design, not a regression.

## Models for operators

Zeus ships a **bundled default model**
(`Zeus.Server.Hosting/nr3-data/rnnoise-default.bin`, copied next to the app and
resolved by `Nr3ModelStore` via `AppContext.BaseDirectory`) so NR3 works out of
the box. It is a standard xiph/rnnoise model in DNNw weights-file format,
matching the vendored architecture's `init_rnnoise()` (43 arrays: `conv1`,
`conv2`, `gru1..3`, `dense_out`, `vad_dense`). BSD-3-Clause, attributed in
`ATTRIBUTIONS.md`.

Operators may override the default by installing their own RNNoise weights file
(a community HF-tuned model, or one trained per the upstream pipeline at the
same architecture) from the DSP menu (upload or URL). Removing the operator
model reverts to the bundled default.

> **Architecture note:** the model that matches Zeus's vendored
> `src/rnnoise_data.h` is the one whose `init_rnnoise()` dimensions line up
> (`conv1` 195→128, `conv2` 384→384, the three GRUs 384→1152, plus `dense_out`
> and `vad_dense`). A model from a *different* upstream `model_version` can parse
> (`rnnoise_model_from_filename` returns non-NULL) yet fail to instantiate
> (`rnnoise_create` returns NULL) when a required array is missing or
> differently sized — verify a candidate with a `rnnoise_create()` smoke test
> before bundling or recommending it.
