# native/wspr — Zeus WSPR encode + decode core

Native WSPR (Weak Signal Propagation Reporter) core for Zeus's built-in
digital-mode client. Unlike FT8 (clean-room MIT `ft8_lib`), **there is no
permissively-licensed WSPR decoder** — the canonical K1JT/K9AN demodulator is
the only working one. Zeus is **GPL-2.0-or-later**, so vendoring the GPL-3
decoder is license-clean (KB2UKA-approved). Baked in-process so the operator
needs no separate install.

## Provenance

Vendored under [`vendor/`](vendor/) from **pavel-demin/wsprd** (the minimal
build-able extract of the WSJT-X `wsprd`), pinned at commit
`8aa903085479910c77de95f7e7c178f66a245ed3`.

- `wsprd.c`, `wsprd_utils.c`, `fano.c`, `jelinek.c`, `nhash.c`, `tab.c`,
  `metric_tables.c` — the decoder (sync search + 4-FSK demod + K=32 r=1/2
  convolutional FEC via Fano/Jelinek sequential decoding). **GPL-3**, Copyright
  2001-2018 Joe Taylor (K1JT) & Steven Franke (K9AN).
- `wsprsim_utils.c` — the **encoder**: `get_wspr_channel_symbols(message,
  hashtab, loctab, symbols)` packs the 50-bit message → FEC → interleave →
  merges the sync vector → 162 4-FSK symbols. GPL-3.
- `pffft.c/.h` — Julien Pommier's "pretty fast FFT" (FFTPACK/BSD-style,
  permissive). **No FFTW dependency** — WSPR is self-contained like ft8_lib's
  KISS FFT.

Vendored source stays **pristine** for re-vendoring; all Zeus glue lives in the
(forthcoming) `zeus_wspr.c` shim.

## Mode parameters (for reference)

4-FSK · 1.4648 baud · 1.4648 Hz tone spacing · 162 symbols · ~110.6 s TX inside
a **120 s** UTC slot · 50 data bits (callsign + 4-char grid + power dBm) · K=32
r=1/2 convolutional FEC + interleave + 162-bit sync vector.

## Integration plan (forthcoming, multi-step)

1. **`zeus_wspr.c` C ABI shim** — mirror `zeus_ft8`:
   - `zeus_wspr_encode(message, symbols[162])` → wraps
     `get_wspr_channel_symbols` (no GPL refactor needed; it's already a clean
     function). The tractable, testable first piece.
   - `zeus_wspr_decode(samples, n, dial_freq, out[])` → wraps the decode path.
     `wsprd.c` is a CLI `main()`; refactor its decode body into a callable
     function (compile `wsprd.c` with `main` renamed, or extract the slot-decode
     loop) **without editing vendored source** — e.g. a `-Dmain=wsprd_main_unused`
     shim + a thin re-entry, or a small `zeus_wspr_decode.c` that includes the
     decode statics. TBD during implementation.
2. **CMake** `native/wspr/CMakeLists.txt` → `libzeus_wspr.{so,dll,dylib}`,
   hidden visibility, the same MSVC porting the FT8 lib needed (M_PI, VLAs →
   ClangCL, POSIX shims via a `win_compat.h`). pffft is portable.
3. **`Zeus.Dsp.Ft8` (or new `Zeus.Dsp.Wspr`)** P/Invoke binding +
   `WsprDecoder`.
4. **Service**: WSPR rides `Ft8Service`'s slot machinery with a **120 s** slot
   and its own decode protocol; emits spots (callsign/grid/power/SNR/freq/drift).
5. **Spotting**: a `WsprReporterService` → WSPRnet (clone the FreeDvReporter
   pattern).
6. **Test vectors**: generate known message→symbols with the encoder and a WSPR
   `.wav` + known answer for the decode gate.

## Decoder integration — OPEN DECISION (needs KB2UKA sign-off)

The encoder was a clean function call. The **decoder is not**: `wsprd.c`'s decode
logic lives **inline in `main()`** (a CLI that reads a `.wav`/`.c2` file, runs the
candidate search + `sync_and_demodulate` + Fano/Jelinek decode loop, and prints
results to stdout / `ALL_WSPR.TXT`). There is no callable `wspr_decode()`. Three
ways to make it usable, each a real tradeoff:

- **A — minimal documented patch (RECOMMENDED).** Lightly edit `wsprd.c` to wrap
  the body of `main()` in a `wspr_decode_samples(idat, qdat, np, dialfreq,
  callback)` function that `main()` then calls. wsprd has only ~7 module globals,
  so this is a contained change. Cleanest callable + lets us pass samples
  in-memory (no temp files) and collect decodes via callback. **Cost:** the
  vendored source is no longer byte-pristine — carry the patch as a tracked
  `vendor.patch` + document it here, applied on re-vendor.
- **B — `-Dmain=wsprd_cli_main` + temp WAV + parse stdout.** Keep vendored source
  pristine; write samples to a temp `.wav`, call the renamed main with synthetic
  argv, capture stdout. **Cost:** fragile (stdout parsing, temp files), and
  wsprd's globals make it **non-reentrant → not multi-RX-safe** without a mutex.
- **C — clean-room re-implement the WSPR demod.** Permissive, multi-RX-clean, but
  a large DSP effort (Fano over K=32) with no real upside since the format is
  standardised.

**Recommendation: Option A** — a small, well-documented patch is the standard
vendoring practice and gives the cleanest, testable, no-temp-file integration.
It does edit vendored source, so it wants KB2UKA's explicit OK (the FT8 vendoring
was kept pristine; this one deliberately wouldn't be). Multi-RX WSPR can still
serialise decode (it runs once per 120 s) regardless of approach.

**Validation plan (self-contained, no external vector needed):** round-trip —
`zeus_wspr_encode` a known message → synthesise its 4-FSK audio (wsprsim-style,
~110.6 s at the decoder's rate) → decode → assert the message comes back. This
is the decode-correctness CI gate.

## Build & test

Encoder builds today: `cmake -S native/wspr -B build && cmake --build build &&
(cd build && ctest)` runs the sync-vector self-test. The decoder joins the build
once the integration approach above is chosen.
