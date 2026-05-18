# Native Library Requirements for WDSP

## Issue

On Windows and Linux, Zeus requires platform-specific native libraries (`wdsp.dll` on Windows, `libwdsp.so` on Linux) to perform DSP operations, including wisdom file generation. Without these libraries, the application will fail during startup when trying to initialize WDSP.

The original repository only shipped with macOS ARM64 native libraries (`Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib`), causing failures on Windows and Linux platforms.

## Root Cause

The WDSP DSP engine is a C library that must be compiled for each target platform and architecture. .NET's P/Invoke mechanism requires these native libraries to be present in the correct runtime directory structure:

```
Zeus.Dsp/runtimes/
  ├── osx-arm64/native/libwdsp.dylib
  ├── osx-x64/native/libwdsp.dylib
  ├── linux-x64/native/libwdsp.so
  ├── linux-arm64/native/libwdsp.so
  ├── win-x64/native/wdsp.dll
  └── win-arm64/native/wdsp.dll
```

The `WdspNativeLoader.cs` resolver looks for these libraries in this exact structure. If the library for the current platform is missing, WDSP initialization fails.

## Solution

A GitHub Actions workflow (`.github/workflows/build-native-libs.yml`) now automatically builds native libraries for all supported platforms:

1. **Windows (x64, arm64)**: Uses MSVC with vcpkg-provided FFTW3
2. **Linux (x64, arm64)**: Uses GCC with system FFTW3 (native) or cross-compilation (arm64)
3. **macOS (arm64, x64)**: Built locally using `native/build.sh`

The workflow can be triggered:
- Manually via GitHub Actions UI (workflow_dispatch)
- Automatically on changes to `native/` directory
- Automatically on changes to the workflow file itself

## How to Build Locally

### Windows
```powershell
vcpkg install fftw3:x64-windows
cmake -S native\wdsp -B native\build -G "Visual Studio 17 2022" -A x64 `
  -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_INSTALLATION_ROOT\scripts\buildsystems\vcpkg.cmake"
cmake --build native\build --config Release
copy native\build\Release\wdsp.dll Zeus.Dsp\runtimes\win-x64\native\
```

### Linux
```bash
sudo apt install libfftw3-dev cmake build-essential pkg-config
./native/build.sh
```

### macOS
```bash
brew install fftw cmake
./native/build.sh
```

## Fallback Behavior

If WDSP native libraries are unavailable, Zeus falls back to a synthetic DSP engine (`SyntheticDspEngine`). This allows the UI to function for development and testing, but no actual signal processing occurs. The synthetic engine:

- Returns mock spectrum data (sine wave pattern)
- Returns zero for all meters
- Processes audio through a pass-through path

For production use and actual radio operation, WDSP native libraries are required.

## Related Files

- `Zeus.Dsp/Wdsp/WdspNativeLoader.cs` — Native library resolution logic
- `Zeus.Dsp/Wdsp/WdspWisdomInitializer.cs` — Wisdom file initialization
- `Zeus.Server.Hosting/WisdomBootstrapService.cs` — Kicks off wisdom generation at startup
- `native/wdsp/CMakeLists.txt` — Cross-platform WDSP build configuration
- `.github/workflows/build-native-libs.yml` — Automated build workflow

## Prevention

To prevent this issue in the future:

1. Always ensure native libraries are built and committed for all target platforms before release
2. The GitHub Actions workflow should run on every push to `native/` to keep libraries up-to-date
3. CI/CD should verify native libraries exist for all platforms before deployment
4. Development documentation should clearly state the need to build native libraries for the target platform

## Testing

To verify native libraries are correctly deployed:

```bash
# Check all platforms have their native libraries
ls -lR Zeus.Dsp/runtimes/

# On Windows
Test-Path Zeus.Dsp\runtimes\win-x64\native\wdsp.dll

# On Linux
test -f Zeus.Dsp/runtimes/linux-x64/native/libwdsp.so

# On macOS
test -f Zeus.Dsp/runtimes/osx-arm64/native/libwdsp.dylib
```

Run the application and check logs for successful WDSP wisdom initialization:
```
[Information] wdsp.wisdom initialising dir=<path>
[Information] wdsp.wisdom ready result=0 (loaded) status=Wisdom already existed
```
