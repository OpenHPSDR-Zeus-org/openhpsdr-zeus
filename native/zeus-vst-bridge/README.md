# zeus-vst-bridge

Native, in-process VST3 host for Openhpsdr-Zeus. Linked as a shared
library and called via P/Invoke from `Zeus.Plugins.Host.Audio.VstBridgeNative`.

## Status

Real VST3 hosting via [Steinberg `vst3sdk`](https://github.com/steinbergmedia/vst3sdk)
(MIT since October 2025), vendored under `third_party/vst3sdk` as a git
submodule: `Module::create(path)` → factory walk → instantiate
`kVstAudioEffectClass` → `initialise` / `setActive` / `setProcessing` /
`ProcessData` (see `src/bridge.cpp`).

The C ABI in `include/zvst.h` is stable; the .NET P/Invoke side is
`Zeus.Plugins.Host.Audio.VstBridgeNative` and is exercised end-to-end
by `VstBridgeNativeRealTests` against the built library.

If the `vst3sdk` submodule is not initialised, CMake falls back to a
**stub** (every load succeeds, every block passes through) so the rest
of the tree still builds without a Steinberg toolchain — see
`CMakeLists.txt`.

CLAP support ([CLAP SDK](https://github.com/free-audio/clap), MIT) is a
possible future addition in the same library. VST2 is **not** in scope
(Steinberg withdrew distribution rights for new hosts in 2024 — see
`docs/proposals/plugin-system-v2.md`).

## Build

Initialise the vendored SDK first (the submodule itself has nested
submodules — `base`, `pluginterfaces`, `public.sdk` — that the hosting
sources need):

```bash
git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk
git -C native/zeus-vst-bridge/third_party/vst3sdk \
    submodule update --init base pluginterfaces public.sdk cmake
```

Then build:

```bash
cd native/zeus-vst-bridge
cmake -B build -DCMAKE_BUILD_TYPE=Release      # Windows: add -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

Output:

- Linux:   `build/libzeus-vst-bridge.so`
- macOS:   `build/libzeus-vst-bridge.dylib`
- Windows: `build/Release/zeus-vst-bridge.dll`

For shipping, stage the per-platform binary into
`Zeus.Plugins.Host/runtimes/<rid>/native/` — `Zeus.Plugins.Host.csproj`
copies `runtimes/**` to the host output, so .NET's native-library
resolver finds it next to `OpenhpsdrZeus.exe`. (For ad-hoc runs you can
instead drop it anywhere on the load path — `PATH`, `LD_LIBRARY_PATH`,
`DYLD_LIBRARY_PATH`.)

## ABI

`include/zvst.h` is the single source of truth. The .NET side checks
`ZVST_ABI` on init via `zvst_init` and refuses to proceed on mismatch.
Bump `ZVST_ABI` in lockstep with any breaking change.

## License

GPL-2.0-or-later (matches Zeus core). Statically linkable against the
MIT-licensed `vst3sdk` and CLAP SDK.
