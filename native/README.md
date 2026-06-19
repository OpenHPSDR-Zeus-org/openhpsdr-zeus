# native/ — WDSP cross-platform build

This directory vendors the WDSP DSP engine (Warren Pratt, GPLv3) and builds it
as a shared library that `Zeus.Dsp` loads via P/Invoke.

Source baseline: **upstream WDSP 1.29** (Warren Pratt) plus a
`linux_port.{c,h}` portability shim and `#ifdef _WIN32` guards to get WDSP off
MSVC. Thetis's own WDSP tree is MSVC-only and is **not** suitable as an
upstream.

Layout:

```
native/
  wdsp/                  # vendored upstream WDSP 1.29 .c/.h
  wdsp/stubs/nr3/        # no-op rnnr_stub.c + rnnoise.h (used when WDSP_WITH_NR3=OFF)
  wdsp/stubs/nr4/        # no-op sbnr_stub.c + specbleach_adenoiser.h (used when WDSP_WITH_NR4=OFF)
  wdsp/wdsp_export.h     # WDSP_EXPORT visibility macro (replaces PORT)
  wdsp/CMakeLists.txt    # the real build
  libspecbleach/         # vendored libspecbleach for NR4 (Phase 1a of #162)
  build.sh               # convenience wrapper -> stages .dylib into Zeus.Dsp
  build/                 # generated CMake cache (gitignored)
```

## NR3 / NR4 build flags

- `WDSP_WITH_NR3` — RNNoise (NR3) support. **OFF by default**; librnnoise is
  not yet vendored. When OFF, `stubs/nr3/rnnr_stub.c` is compiled in place of
  `rnnr.c`, leaving `rnnr.p->run` at 0 so the NR3 branch never executes.
- `WDSP_WITH_NR4` — libspecbleach / SBNR support. **ON by default** since
  libspecbleach is vendored at `native/libspecbleach/`. When OFF,
  `stubs/nr4/sbnr_stub.c` is compiled instead.

libspecbleach is built as a `STATIC` sub-target with hidden symbol visibility
and embedded into `libwdsp.{so,dylib,dll}` — no extra runtime library to ship.
See `native/libspecbleach/VENDORING.md` for re-vendoring notes.

## Build on macOS (arm64 / x86_64)

```sh
brew install fftw cmake
./native/build.sh                # Release, output -> Zeus.Dsp/runtimes/<rid>/native/
./native/build.sh Debug          # optional: Debug build
```

The script auto-detects `osx-arm64` vs `osx-x64` and stages `libwdsp.dylib`
into the matching `Zeus.Dsp/runtimes/<rid>/native/` directory. .NET's default
native library resolution picks it up with no extra configuration.

## Build on Linux (x86_64 / arm64)

`libfftw3-dev` ships both double (`fftw3`) and single-precision (`fftw3f`)
variants in the same package — both are needed: `fftw3` for WDSP itself, `fftw3f`
for libspecbleach (NR4).

```sh
sudo apt install libfftw3-dev cmake build-essential pkg-config     # Debian/Ubuntu
sudo dnf install fftw-devel cmake gcc pkgconf                      # Fedora/RHEL
./native/build.sh
```

Produces `Zeus.Dsp/runtimes/linux-x64/native/libwdsp.so` (or `linux-arm64`).

## Build on Windows (x64 / arm64)

Windows native libraries are built automatically via GitHub Actions (see
`.github/workflows/build-native-libs.yml`). The workflow uses vcpkg to install
FFTW3 and builds for both x64 and arm64.

For local development:

```powershell
# Install dependencies
vcpkg install fftw3:x64-windows-static-md
# or for ARM64: vcpkg install fftw3:arm64-windows-static

# Configure (x64)
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A x64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake" `
  -DVCPKG_TARGET_TRIPLET=x64-windows-static-md `
  -DWDSP_WITH_NR3=OFF `
  -DWDSP_WITH_NR4=ON

# Configure (ARM64)
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A ARM64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake" `
  -DVCPKG_TARGET_TRIPLET=arm64-windows-static `
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded `
  -DWDSP_WITH_NR3=OFF `
  -DWDSP_WITH_NR4=ON

# Build
cmake --build native\build --config Release

# Stage
copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-x64\native\
# or for ARM64: copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-arm64\native\
```

## Automated Builds via GitHub Actions

Native libraries for Windows and Linux are built by the manual
`.github/workflows/build-native-libs.yml` workflow. This workflow:

- Builds for Windows (x64, arm64) using MSVC and vcpkg
- Builds for Linux (x64, arm64) using GCC/aarch64 cross-compilation
- Verifies NR4/SBNR exports on every WDSP artifact
- Bundles the FFTW side-by-side libraries for dynamically linked RIDs
- Stages the libraries in `Zeus.Dsp/runtimes/<rid>/native/`
- Can be triggered manually via workflow_dispatch

To trigger a manual build, go to Actions → "Build Native Libraries" → "Run workflow".

## MVP API surface

`-fvisibility=hidden` is set at the compiler level, so only the functions
marked `PORT` (→ `WDSP_EXPORT`) in the upstream WDSP headers are exported.
That's ~500 symbols on the current build — a proper superset of the ~20 the
C# wrapper in `Zeus.Dsp/` uses. The wrapper only P/Invokes names that
actually exist.

Verify the MVP surface after a build:

```sh
nm -gU Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib \
  | grep -E 'OpenChannel|CloseChannel|SetRXAMode|XCreateAnalyzer|SetAnalyzer|GetPixels|Spectrum0|fexchange0|DestroyAnalyzer'
```

Note: the symbol is `DestroyAnalyzer` (capital D), not `destroy_analyzer`.
`Spectrum`, `Spectrum0`, and `Spectrum2` are all exported; `Spectrum0` is the
one `fexchange0`-driven callers use.

## Noise-reduction artifact audit

Before claiming packaged current WDSP noise-reduction support for a RID, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-runtime-artifacts.ps1 -FailOnMissingWinX64CurrentNr
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -BinaryPath Zeus.Dsp\runtimes\win-x64\native\wdsp.dll -RequireBinaryExports
```

Release packaging status:

- Release builds rebuild WDSP for `win-x64`, `win-arm64`, `linux-x64`,
  `linux-arm64`, and `osx-arm64`, then fail if the NR4/SBNR exports are
  missing.
- `win-x64`, Linux, and macOS package FFTW side-by-side with WDSP when the
  native binary dynamically links it.
- `win-arm64` is the exception: FFTW and the CRT are statically linked into
  `wdsp.dll`, so no side-by-side FFTW DLLs are expected for that RID.
- Checked-in runtime binaries are local-development conveniences. Refresh
  them through the manual native workflow before committing native artifact
  updates.

## Source modifications vs. upstream

Diff against upstream WDSP 1.29 is intentionally tiny:

1. `comm.h` — replaced `#define PORT __declspec(dllexport)` with an include of
   `wdsp_export.h` and `#define PORT WDSP_EXPORT`. This is the single change
   needed to get proper symbol export on all three OSes.
2. `wdsp_export.h` — new file, holds the cross-platform visibility macro.
3. `stubs/nr3/rnnoise.h` + `stubs/nr4/specbleach_adenoiser.h` — minimal opaque
   types so `rnnr.h` / `sbnr.h` compile without librnnoise / libspecbleach
   available. Each lives in its own subdirectory so the CMake gate can include
   only the stub matching the OFF flag, avoiding header collisions with the
   real library include path.
4. `stubs/nr3/rnnr_stub.c` + `stubs/nr4/sbnr_stub.c` — no-op replacements for
   `rnnr.c` / `sbnr.c`. These provide the entry points RXA.c calls (with
   `run` stuck at 0, the NR branches never execute) so we can build without
   pulling in the upstream noise-reduction libraries.

No other files are modified. `linux_port.{c,h}` does all the Win32 → POSIX
shimming (pthreads, aligned malloc, Sleep, `__declspec`).

## Re-vendoring upstream WDSP

Bumping to a newer WDSP snapshot is mechanical:

```sh
rm native/wdsp/*.c native/wdsp/*.h
cp /path/to/new-wdsp-1.29/*.{c,h} native/wdsp/
# re-apply the comm.h PORT edit (see step 1 above)
./native/build.sh
```

Don't copy upstream `.o` files, `Makefile`, or `COMPILE.*` notes — we own the
build system now.
