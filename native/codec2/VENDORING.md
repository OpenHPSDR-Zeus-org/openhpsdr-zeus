# codec2 — vendoring notes

This directory does **not** vendor any codec2 source. It carries a single
`CMakeLists.txt` that fetches and builds **libcodec2** (the Codec 2 / FreeDV
modem library, David Rowe / drowe67) straight from the upstream Git repo via
CMake `FetchContent`. Zeus P/Invokes the resulting shared library for FreeDV
digital voice.

## Provenance / pinned ref

Fetched from upstream:

```
https://github.com/drowe67/codec2.git
```

Pinned to a specific release tag so updates are always a deliberate ref bump,
never a silent moving-HEAD change:

| Field        | Value                                      |
|--------------|--------------------------------------------|
| `GIT_TAG`    | `1.2.0`                                     |
| commit       | `06d4c11e699b0351765f10398abb4f663a984f36`  |

The pin lives in `CMakeLists.txt` as the `CODEC2_GIT_TAG` cache variable
(default `"1.2.0"`).

`1.2.0` is the latest stable release tag at time of writing; verified against
the upstream tag list (`GET /repos/drowe67/codec2/git/refs/tags/1.2.0`
resolves to the commit above).

## Why no LPCNet / 2020

The build forces `LPCNET=OFF` and never sets `LPCNET_BUILD_DIR`. This is
intentional:

* **Dependency-free.** Classic codec2 needs no external libraries (no FFTW, no
  neural-net runtime, no model files). That keeps the cross-platform build
  trivial on macOS / Windows / Linux x64+arm64 / Raspberry Pi.
* **Scope.** The ML-based **2020 / 2020B** modes require LPCNet and large model
  blobs and are out of scope for Zeus. Enabling them would pull a heavy,
  platform-sensitive dependency chain into a library that is otherwise clean.

## Modes available

With LPCNet excluded, the library still provides the classic FreeDV / Codec 2
modes Zeus targets:

* **700C**
* **700D**
* **700E**
* **1600**
* **800XA**

(2020 / 2020B are **not** built.)

## Licence posture

codec2 is **LGPL-2.1**. Zeus links it the same way it links WDSP and
libspecbleach: as a separately-built **dynamic shared library** loaded at
runtime via .NET `NativeLibrary`. Dynamic linking against an LGPL-2.1 library
is the LGPL's intended use and is compatible with Zeus's distribution. The
library is rebuilt from unmodified upstream source (no Zeus patches), so the
"relink with a modified library" LGPL provision is satisfied by the documented
rebuild procedure below.

## Build (manual, per platform)

CI wiring lives in `.github/workflows/build-native-libs.yml` (see below); these
are the equivalent manual steps. The output shared library must land in
`Zeus.Dsp/runtimes/{rid}/native/` so the Zeus `NativeLibrary` resolver finds
it, exactly like `libwdsp` / `libminiaudio`.

Build **only** the `codec2` target — the upstream tree also defines CLI tools
(`c2enc`, `freedv_tx`, …) that Zeus does not ship.

### macOS (osx-arm64)

```sh
cmake -S native/codec2 -B native/build-codec2 -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-codec2 --config Release --target codec2 --parallel
# upstream stages the .dylib under the fetched src tree:
find native/build-codec2 -name 'libcodec2.dylib'
cp <found>/libcodec2.dylib Zeus.Dsp/runtimes/osx-arm64/native/libcodec2.dylib
```

### Linux (linux-x64 / linux-arm64 / Raspberry Pi)

```sh
cmake -S native/codec2 -B native/build-codec2 -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-codec2 --config Release --target codec2 --parallel
find native/build-codec2 -name 'libcodec2.so*'
cp <found>/libcodec2.so Zeus.Dsp/runtimes/linux-x64/native/libcodec2.so
```

For arm64 cross-compile, pass the same toolchain flags the wdsp/miniaudio jobs
use (`-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64
-DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc`). codec2 classic modes need no
external libs, so no cross-FFTW is required (unlike wdsp).

### Windows (win-x64) — MinGW, **not** MSVC

> **codec2 cannot be built with MSVC.** Its OFDM/filter modem code is C99
> `_Complex` (`<complex.h>`), which MSVC's C compiler does not support; and the
> MSVC UCRT additionally `#define`s `complex` to its non-standard `_complex`,
> which breaks clang-cl too. codec2 targets **MinGW** on Windows (note its own
> CMake `if(MINGW)` branch). Build the Windows DLL with **MinGW-w64 gcc**
> (UCRT runtime, to match .NET / the system CRT). The CI does this via
> `msys2/setup-msys2` (UCRT64) — see `build-native-libs.yml`.

```sh
# From a UCRT64 MinGW shell (msys2 UCRT64, or a WinLibs UCRT gcc on PATH):
cmake -S native/codec2 -B native/build-codec2 -G Ninja \
  -DCMAKE_C_COMPILER=gcc -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-static-libgcc -static -s"
cmake --build native/build-codec2 --config Release --target codec2 --parallel
# Standalone codec2.dll (deps: KERNEL32 + system UCRT only; libgcc static-linked):
dll=$(find native/build-codec2 -name 'codec2.dll' | head -n1)
cp "$dll" Zeus.Dsp/runtimes/win-x64/native/codec2.dll
objdump -p Zeus.Dsp/runtimes/win-x64/native/codec2.dll | grep 'DLL Name'
```

**win-arm64:** not yet built — aarch64 MinGW (e.g. `llvm-mingw`) is needed.
Until then FreeDV is gracefully unavailable on win-arm64 (the modem reports
`NativeAvailable=false` and passes audio through). Tracked as a follow-up.

### MSVC-compatibility patch (all toolchains)

codec2 1.2.0 sets GCC-only global C flags (`-Wall -Wno-strict-overflow`,
`-g/-O2/-O3`) and links Unix `libm` unconditionally — these break MSVC-style
drivers. `native/codec2/CMakeLists.txt` applies `patch-codec2-msvc.cmake` as a
FetchContent `PATCH_COMMAND` to guard them behind `if(NOT MSVC)`. This is
idempotent and is a no-op for gcc/clang on Unix. It is what lets the Windows
build configure cleanly; the C99 `_Complex` requirement (MinGW) is separate and
inherent to codec2.

## Updating the pin

1. Pick the new upstream release tag from
   <https://github.com/drowe67/codec2/tags>.
2. Bump `CODEC2_GIT_TAG` in `native/codec2/CMakeLists.txt` (and the tables in
   this file).
3. Re-run the build on every platform (or trigger the
   **Build Native Libraries** workflow) and commit the refreshed
   `Zeus.Dsp/runtimes/{rid}/native/` binaries.
4. Confirm LPCNet is still off and the classic modes still link before
   shipping.
