# native/ft8 — Zeus FT8/FT4 decode + encode core

Native digital-mode core for Zeus's built-in FT8 client. Wraps the vendored
[kgoba/ft8_lib](https://github.com/kgoba/ft8_lib) (MIT) in a small, stable C
ABI (`zeus_ft8.h`) that the managed `Zeus.Dsp.Ft8` layer binds against via
P/Invoke.

Builds to `libzeus_ft8.{dylib,so}` / `zeus_ft8.dll`, staged (like the other
native libs) under `Zeus.Dsp/runtimes/<rid>/native/` by CI
(`.github/workflows/build-native-libs.yml`).

## Layout

```
zeus_ft8.h            stable C ABI (P/Invoke surface)
zeus_ft8.c            the shim: per-RX context, thread-safe callsign hash,
                      monitor → find_candidates → decode → dedup → unpack
CMakeLists.txt        shared-lib build + the decode-correctness self-test
test/
  zeus_ft8_selftest.c decodes the reference corpus, compares to answer keys
test-vectors/wav/     bundled WAVs + WSJT-X .txt answer keys (the CI gate)
vendor/               unmodified ft8_lib (ft8/ common/monitor common/wave fft/)
  LICENSE             ft8_lib MIT licence
```

## Build & test (local)

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
( cd build && ctest --output-on-failure )   # runs ft8_corpus_decode
```

The self-test prints per-slot `decoded / expected` and a corpus total. It
fails only on **zero** decodes; the numeric decode rate is tracked as the
deep-decode quality metric (see below), not a hard pass/fail, so a marginal
platform FFT difference doesn't red CI.

## ABI design notes

- **Per-RX context.** All mutable state (the callsign hash table used to
  resolve `<...>` hashed calls) lives in a caller-owned `zeus_ft8_ctx`. Create
  one per receiver slice so multiple bands can decode concurrently without a
  shared lock. The ft8_lib hash interface has no user-data pointer, so the
  active context is published in a `_Thread_local` for the duration of a
  decode call — one context is only ever driven by one decode worker.
- **No leaked types.** The ABI is flat C structs / arrays only; no ft8_lib
  type crosses the boundary, so the vendored library can be re-pinned without
  breaking the managed P/Invoke signatures.
- **Hidden visibility.** Only the six `zeus_ft8_*` symbols are exported.

## Decode quality

Measured against the bundled WSJT-X answer keys (`ctest`):

| Config | corpus decodes | rate |
|---|---|---|
| stock ft8_lib (time_osr=2, single) | 265 / 362 | 73 % |
| **default (time_osr=4, single)** | 275 / 362 | 76 % |
| **default + multi-pass (passes=3)** | 276 / 362 | 76 % |

Two levers were evaluated empirically (see the sweep in git history):

1. **Time oversampling (`time_osr`)** — the real win. `time_osr=4` gives finer
   sync resolution and +10 decodes over stock for ~2× compute (well within
   budget for a once-per-15 s decode). `time_osr=8` and `freq_osr=4` both
   *regressed* the corpus, so the defaults are `time_osr=4`, `freq_osr=2`.
2. **Multi-pass subtract-and-redecode (`passes`)** — implemented and correct:
   each decoded FT8 signal's GFSK waveform is reconstructed (carrier
   fine-searched ±2.5 Hz, amplitude+phase fit by 2D least squares) and
   subtracted from the slot audio before re-decoding. It is **safe** (CRC-14 +
   LDPC reject any garbage from imperfect subtraction; the self-test asserts
   multi-pass never regresses below single-pass) but yields only **+1** on this
   corpus: individual FT8 signals are ~0.01 amplitude in a crowded passband, so
   removing them barely shifts the decode landscape. Subtraction helps only
   genuinely-overlapping signals, a minority here.

**The remaining gap to WSJT-X (~95 %) is decoder *sensitivity*, not masking.**
WSJT-X's extra decodes come from **ordered-statistics decoding (OSD)** of the
LDPC code plus a-priori decoding — algorithms ft8_lib does not implement.
Adding OSD is the next decode-quality phase and is where the bulk of the gap
closes; the multi-pass framework stays as the right architecture for it (and
for true overlapping-signal cases).

Tuning knobs (env overrides, for per-platform tuning — e.g. lower `time_osr`
on a Pi): `ZF_TOSR`, `ZF_FOSR`, `ZF_MINSCORE`, `ZF_LDPCIT`, and `ZF_DEBUG=1`
for per-pass/subtraction diagnostics. SNR is approximated from the sync score
and is a refinement target.

## Re-vendoring ft8_lib

Vendored sources under `vendor/` are unmodified upstream. To re-pin: replace
`vendor/ft8`, `vendor/common/{monitor,wave}.{c,h}`, `vendor/common/common.h`,
and `vendor/fft` from a fresh ft8_lib checkout, keep `vendor/LICENSE`, and
re-run the self-test. Do not edit vendored files — all Zeus-specific logic
lives in `zeus_ft8.c`.
