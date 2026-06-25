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

## Decode quality / deep-decode roadmap

Single-pass baseline (matches stock ft8_lib) is **~73 % of the WSJT-X answer
key across the bundled corpus** — clean signals decode fully; weak/marginal
and stronger-masked signals on crowded slots are missed. Closing that gap is
the **deep multi-pass decode** work (subtract-and-redecode):

1. decode pass *n* → for each decode, re-encode + synthesise the GFSK waveform
   at the decoded freq/dt/amplitude and subtract it from the slot audio;
2. re-run the monitor on the residual → decode again (now-unmasked signals
   emerge); repeat for `passes` iterations.

The `passes` parameter is already in the ABI; the subtraction step is the
follow-up. SNR is currently approximated from the sync score and is a
refinement target.

## Re-vendoring ft8_lib

Vendored sources under `vendor/` are unmodified upstream. To re-pin: replace
`vendor/ft8`, `vendor/common/{monitor,wave}.{c,h}`, `vendor/common/common.h`,
and `vendor/fft` from a fresh ft8_lib checkout, keep `vendor/LICENSE`, and
re-run the self-test. Do not edit vendored files — all Zeus-specific logic
lives in `zeus_ft8.c`.
