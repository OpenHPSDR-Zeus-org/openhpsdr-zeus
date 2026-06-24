# native/radae — RADE V1 (Radio Autoencoder) vendoring

> **STATUS: SCAFFOLD ONLY. THE NATIVE BUILD IS NOT IMPLEMENTED YET.**
> RADEV1 is surfaced + gated in the UI (Phase 1, landed). This directory is the
> starting point for Phase 2 (the native build).
>
> **⚠ VERIFIED 2026-06-23 — do NOT FetchContent `drowe67/radae`.** A real build
> investigation found upstream `librade` embeds CPython + PyTorch at runtime
> (`rade_api.c` unconditionally includes `Python.h`; the OFDM modem/sync run in
> Python; even freedv-gui ships embedded Python + downloads PyTorch). It cannot be
> vendored dependency-free. The viable Python-free base is **`peterbmarks/radae_nopy`**
> (pure C, weights compiled in, BSD-2 — but Linux/macOS only, no Windows/arm, and
> upstream-deprecated in favour of a future `freedv-backend`). Pick the base
> (radae_nopy now vs. waiting for the upstream C port) before writing CMake.
> Full analysis: `docs/designs/rade-v1-integration.md` → "Phase 2 build investigation".

## What this will produce (target)

```
macOS   : librade.dylib   (arm64, x86_64)
Linux   : librade.so      (x86_64, arm64, Raspberry Pi)
Windows : rade.dll        (x64, arm64) — no "lib" prefix
```

…plus the FARGAN vocoder (from Opus's `spl_fargan` branch) and the RADE model
weights, so Zeus can P/Invoke `rade_api.h` + the `fargan_*` synth.

## Upstream

| | |
|---|---|
| Repo | `drowe67/radae` |
| License | BSD-2-clause |
| Pin | **TBD** — pick a release tag, never moving HEAD (cf. codec2's `1.2.0` pin) |
| FARGAN/Opus | Opus `spl_fargan` branch, pulled by radae's `cmake/BuildOpus.cmake` |
| Model | `.pth` checkpoint → C (`rade_enc_data.c`/`rade_dec_data.c`) via repo export, or runtime blob |

## Known build blockers (why this isn't a copy of native/codec2)

1. **Python at configure time.** radae's root `CMakeLists.txt` requires
   `Python3` (Interpreter + Development + NumPy); even the cross-compile path
   wants `-DPython3_ROOT_DIR`. We must drive only the C decoder + FARGAN targets
   and supply pre-exported C weights so Python isn't needed to *build the lib we
   ship*. Reference freedv-gui's build, which does exactly this for distribution.
2. **Custom Opus/FARGAN build.** Not `find_package(Opus)` — radae includes
   `cmake/BuildOpus.cmake` to fetch+configure the `spl_fargan` Opus branch with
   FARGAN. That sub-build must succeed under each toolchain.
3. **MSVC can't compile the C99 `_Complex` DSP** (same as codec2). Windows builds
   via **clang-cl**; expect an `if(NOT MSVC)` patch like
   `native/codec2/patch-codec2-msvc.cmake`, applied to both radae and the Opus
   sub-build.
4. **arm/Pi.** RADE runs on arm (TI AM625, Librem 5) but the AVX option must be
   off there; ensure the Opus/FARGAN SIMD selection is correct per arch.
5. **Reverses `LPCNET=OFF`.** This is the deliberate dependency-free decision in
   `native/codec2/VENDORING.md`. Accepted for RADE (maintainer, 2026-06-23):
   bigger binaries + more CPU. Keep the FARGAN dependency contained to `librade`;
   do NOT let it leak back into the codec2 build.

## Plan (Phase 2)

1. Pin a radae release tag; FetchContent radae + (via its cmake) Opus `spl_fargan`.
2. Force the C decoder path; provide exported model weights as C so no Python is
   needed to build the shipped library.
3. Apply clang-cl / arm patches until `rade` + FARGAN link on win/linux/mac × x64/arm.
4. Name outputs `rade.dll` / `librade.{so,dylib}` (PREFIX "" on WIN32, like codec2).
5. Wire into `build-native-libs.yml`; emit binaries next to `codec2.*`.
6. Then Phase 4: `RadeNativeMethods`/`RadeNativeLoader`/`RadeModem` P/Invoke +
   the 48k↔16k resampler + real→complex front end; flip `RadeAvailable` true.

## Update procedure (once it builds)

Bump the radae pin → rebuild on all platforms via CI → re-export weights if the
model changed → verify `rade_version()` + an on-air decode. Mirror the codec2
VENDORING update discipline.
