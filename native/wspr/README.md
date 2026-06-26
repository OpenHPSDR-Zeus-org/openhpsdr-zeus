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

## Build & test

To be added with the shim + CMake. Until then the vendored source is reference
only and not compiled into any target.
