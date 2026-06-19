# Windows on ARM (win-arm64)

Load-bearing context for anyone touching the native WDSP build, the
`build-native-libs.yml` matrix, the release pipeline, or the Inno Setup
scripts. Windows-on-ARM is the **third** Windows target Zeus ships
alongside `win-x64` and the macOS / Linux RIDs — the wiring is small
but easy to undo if you don't know why each piece is shaped the way it
is.

## TL;DR

- Native `wdsp.dll` cross-compiles cleanly under VS 2022's ARM64
  toolset once the SSE FTZ guard at `native/wdsp/channel.c:101` is
  widened to also exclude `_M_ARM64` / `__aarch64__`.
- vcpkg triplet is `arm64-windows-static` — FFTW3 and the CRT are
  statically linked into `wdsp.dll`, so a fresh Windows ARM64 install does
  not need the ARM64 VC++ Redistributable or side-by-side FFTW DLLs.
- Both native and managed builds run on `windows-latest` (x64 host,
  MSVC ARM64 cross-tools). We do **not** use the `windows-11-arm`
  runner.
- Photino + WebView2 work on Win11 ARM64 with no code change. Audio
  (NAudio / WASAPI) is expected to "just work" but is **not yet
  hardware-verified** on a real Surface Pro X / Snapdragon device.

## The SSE FTZ guard

`native/wdsp/channel.c:101-111` sets denormals-to-zero on the MXCSR:

```c
#if !defined(linux) && !defined(__APPLE__) && !defined(_M_ARM64) && !defined(__aarch64__)
  _MM_SET_FLUSH_ZERO_MODE (_MM_FLUSH_ZERO_ON);
#endif
```

`_MM_SET_FLUSH_ZERO_MODE` is an **x86 SSE-only** intrinsic. The header
`<xmmintrin.h>` doesn't even exist on the MSVC ARM64 toolset (clang-cl,
gcc, clang aarch64), so the older guard `!defined(linux) &&
!defined(__APPLE__)` produced a hard compile error on `windows-arm64`.
Widening to also gate `_M_ARM64` / `__aarch64__` is a one-line
behavioural no-op:

- FTZ is a **perf hint** (subnormals are slow on Pentium-era pipelines);
  WDSP / HL2 do not depend on it for correctness — every linux and
  Apple Silicon build has been running without it for the lifetime of
  the project.
- ARM64 cores have an analogous `FZ` bit in the FPCR. We deliberately
  leave it at the OS default rather than introducing an
  architecture-specific intrinsic — none of the WDSP hot paths produce
  enough subnormals for it to matter, and adding NEON code to a vendor
  source we otherwise do not patch is exactly the kind of drift the
  "don't fork upstream WDSP" rule exists to prevent.

If you're tempted to add a NEON FTZ equivalent, **don't** — read
`docs/lessons/native-lib-requirements.md` and the gating comment at
`channel.c:101-109` first. There is no measured win, and we'd be
re-patching channel.c on every WDSP refresh.

The same grep that produced the original deferral comment confirmed
that this is the **only** SSE-intrinsic site in `native/wdsp/`. There
is no `<xmmintrin.h>` / `<emmintrin.h>` / `<immintrin.h>` include
anywhere else in the tree, no `_mm_*` calls, and no inline asm.
re-grep before adding any new SSE code, or `windows-arm64` will silently
break.

## vcpkg triplet — `arm64-windows-static`

The Windows native build uses vcpkg for FFTW3:

```yaml
vcpkg install fftw3:arm64-windows-static
```

`static` = FFTW3 and the CRT are both linked into `wdsp.dll` (`/MT`).
The workflow passes `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` for the
ARM64 CMake configure so WDSP and vcpkg agree on the CRT model. The
resulting ARM64 PE imports only Windows-shipped DLLs (`KERNEL32.dll` and
`AVRT.dll` in the current audit), which keeps the installer usable on a
fresh Surface Pro X / Snapdragon system.

Do **not** switch to `arm64-windows` (dynamic FFTW) — that ships extra
FFTW DLLs that the installer would need to bundle. Do **not** switch to
`arm64-windows-static-md` unless the installer also starts carrying the
ARM64 VC++ Redistributable; otherwise `WdspNativeLoader` fails before it
can open a channel on clean Windows ARM64 machines.

## Why `windows-latest` (x64 host), not `windows-11-arm`

GitHub-hosted ARM64 runners (`windows-11-arm`) exist but we deliberately
do not use them:

1. **Runner availability.** `windows-11-arm` is in public preview at
   the time of writing and queues unpredictably; the x64 windows-latest
   pool is large and well-warmed.
2. **Toolchain parity.** Cross-compiling from x64 → ARM64 with VS
   2022's ARM64 toolset is the same path Microsoft documents for
   line-of-business apps. It produces identical PE output to a native
   ARM64 build, and lets one matrix entry change (`-A ARM64` + the
   triplet) cover all the win-arm64 work — no second runner image, no
   per-runner dependency drift.
3. **Matrix simplicity.** Both `wdsp.dll` (native) and `Zeus.Server` /
   `Zeus.Desktop` (managed) cross-compile from the same x64 host, so
   the release pipeline keeps a single windows row that fans out by
   `rid`. Splitting host arches would force two shellings of Inno
   Setup, two dumpbin invocations, two vcpkg installs — for no
   testing benefit, since neither toolchain is arch-sensitive at the
   point we care about.

`dumpbin.exe` (used to verify `SetRXASBNR*` exports) is a host-arch
tool — the `Hostx64\x64\dumpbin.exe` binary reads ARM64 PEs without
issue. No path change needed for the SBNR check.

## Photino + WebView2 on Win11 ARM64

Zeus.Desktop loads `Photino.NET 4.0.16` which is **AnyCPU** — same
package nupkg satisfies both `win-x64` and `win-arm64` publishes. The
underlying WebView2 runtime ships per-arch on Win11 ARM64 by default
(it's a system component on stock Win11 ARM installs), so:

- `dotnet publish -r win-arm64 --self-contained true` produces an ARM64
  `Zeus.Desktop.exe` that hosts the ARM64 Photino native bridge against
  the OS-supplied ARM64 WebView2.
- No per-arch package selector. No additional Photino native binary
  to vendor.

Tested on a Win11 ARM64 VM during the windows-arm matrix bring-up;
launches, loads the SPA, talks to the in-process backend, and routes
WebSocket frames identically to the x64 build.

## Audio (NAudio / WASAPI) — untested on hardware

NAudio is **AnyCPU**; WASAPI is the OS-supplied audio API. There is no
architecture-specific code path on the Zeus side. Mic capture for TX,
speaker playback for RX, and the device enumeration used by the audio
device picker should all work without modification on Win11 ARM64.

That said: **this has not been smoke-tested on a real ARM64 Windows
device** as of the win-arm64 ship. The native installers will produce
working binaries; whether the audio device list, the mic capture
pipeline, and the `getUserMedia` Edge / WebView2 path behave
identically on a Surface Pro X or Snapdragon X Elite laptop is an
operator-verification follow-up. If you ship an ARM64 install to a
user with audio quirks, log NAudio device IDs and the WebView2 build
number before assuming the issue is Zeus-side.

## What the artifacts look like

The release pipeline produces, per tagged release:

- `Zeus-<ver>-win-x64-setup.exe` — service mode, x64
- `Zeus-Desktop-<ver>-win-x64-setup.exe` — desktop mode, x64
- `Zeus-<ver>-win-arm64-setup.exe` — service mode, ARM64
- `Zeus-Desktop-<ver>-win-arm64-setup.exe` — desktop mode, ARM64

All four come from the same `release.yml` matrix run on
`windows-latest`, and all four are listed in the release notes. The
Inno Setup `.iss` files are arch-parameterised via `/DArch=arm64` —
see `installers/zeus-windows.iss` and `installers/zeus-desktop-windows.iss`.

## References

- `native/wdsp/channel.c:101-111` — the SSE FTZ guard.
- `.github/workflows/build-native-libs.yml:21-26` — windows arch
  matrix; vcpkg triplet substitution.
- `.github/workflows/release.yml` — the four-artifact windows matrix
  + `create-release` files block.
- `installers/zeus-windows.iss`, `installers/zeus-desktop-windows.iss`
  — arch-parameterised Inno Setup scripts.
- `docs/lessons/native-lib-requirements.md` — broader context on the
  per-RID `Zeus.Dsp/runtimes/<rid>/native/` layout.
