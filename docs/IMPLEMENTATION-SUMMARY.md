# Native Installers Implementation Summary

## Overview

This implementation adds comprehensive native installer support and automated release management for Zeus across all desktop platforms (Windows, macOS, Linux).

## What Was Implemented

### 1. Version Management

**File: `Directory.Build.props`**
- Added centralized version properties that apply to all .NET projects
- Version defaults to `0.0.0-dev` for local development builds
- Reads version from git tags during CI builds (e.g., tag `v0.1.0` → version `0.1.0`)
- Properties set: `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`

**File: `OpenhpsdrZeus/Program.cs`**
- Added `/api/version` endpoint that returns the current version from assembly attributes
- Example response: `{"version": "0.1.0"}` or `{"version": "0.0.0-dev"}`

**Files: `zeus-web/src/components/AboutPanel.tsx`, `zeus-web/src/components/SettingsMenu.tsx`**
- Created "About" panel in Settings menu
- Displays current version
- Includes "Check for Updates" button
- Fetches latest release from GitHub API
- Compares versions and notifies user if update available
- Provides download link to latest release

### 2. .NET Publish Profiles

**Directory: `OpenhpsdrZeus/Properties/PublishProfiles/`**

Created publish profiles for each platform:
- `win-x64.pubxml` - Windows 64-bit
- `linux-x64.pubxml` - Linux 64-bit
- `osx-x64.pubxml` - macOS Intel
- `osx-arm64.pubxml` - macOS Apple Silicon

Each profile configures:
- Self-contained deployment (includes .NET runtime)
- Ready-To-Run (R2R) compilation for faster startup
- Embedded debug symbols
- No trimming (keeps full WDSP P/Invoke compatibility)

### 3. Native Installers

#### Windows (Inno Setup)

**File: `installers/zeus-windows.iss`**
- Creates professional Windows installer (`.exe`)
- Includes all files from publish output
- Creates Start Menu shortcuts and optional desktop icon
- Requires 64-bit Windows
- Accepts version via command-line parameter
- Output: `Zeus-X.Y.Z-win-x64-setup.exe`

#### macOS

**File: `installers/create-macos-app.sh`**
- Creates `.app` bundle with proper structure
- Supports both arm64 (Apple Silicon) and x64 (Intel)
- Includes `Info.plist` with version and bundle metadata
- Creates `.dmg` disk image for distribution
- Includes README with `xattr -cr` instructions (unsigned app)
- Output: `Zeus-X.Y.Z-macos-{arm64|x64}.dmg`

#### Linux

**File: `installers/create-linux-package.sh`**
- Creates `.tar.gz` archive
- Includes launch script (`zeus`) with browser auto-open
- Supports both GUI and headless environments
- Includes README with installation instructions
- Output: `zeus-X.Y.Z-linux-x64.tar.gz`

### 4. GitHub Actions Workflows

#### Build and Test Workflow

**File: `.github/workflows/build-test.yml`**
- Triggers on push to main/develop, pull requests, and manual dispatch
- Matrix build across ubuntu, windows, macos runners
- Runs .NET build and tests
- Runs frontend lint, typecheck, tests, and build
- Verifies frontend output is produced correctly

#### Release Workflow

**File: `.github/workflows/release.yml`**
- Triggers automatically on version tags (e.g., `v0.1.0`)
- Also supports manual workflow_dispatch with version input
- Matrix build for all 4 platforms:
  - Windows x64 (Inno Setup installer)
  - Linux x64 (tarball)
  - macOS x64 Intel (DMG)
  - macOS arm64 Apple Silicon (DMG)
- Each platform job:
  1. Checks out code
  2. Builds frontend
  3. Publishes .NET app for target platform
  4. Creates platform-specific installer
  5. Uploads artifact
- Final job creates GitHub Release with:
  - All 4 installer artifacts attached
  - Auto-generated release notes
  - Platform-specific installation instructions
  - macOS `xattr -cr` warning

### 5. Documentation

#### Installation Guide

**File: `docs/INSTALLATION.md`**
- Complete installation instructions for all platforms
- Windows: run installer, SmartScreen handling
- macOS: drag to Applications, **mandatory** `xattr -cr` command
- Linux: extract tarball, optional desktop launcher
- First-run WDSP wisdom initialization explained
- Configuration file locations for each OS
- Update procedure for each platform
- Troubleshooting section

#### Release Process

**File: `docs/RELEASE.md`**
- Step-by-step release process for maintainers
- Tag creation and push instructions
- Monitoring workflow execution
- Manual release fallback procedures
- Semantic versioning guidelines
- Troubleshooting common release issues
- Release checklist

### 6. Other Changes

**File: `.gitignore`**
- Added `installers/output/` to ignore built installers
- Added `installers/dmg_temp/` to ignore temporary DMG build files

## How to Use

### For Developers

**Local development** - version shows as `0.0.0-dev`:
```bash
dotnet run --project OpenhpsdrZeus
```

**Test publish** for a specific platform:
```bash
dotnet publish OpenhpsdrZeus/OpenhpsdrZeus.csproj -p:PublishProfile=linux-x64 -p:VersionPrefix=0.1.0
```

### For Maintainers

**Create a release**:
```bash
git tag -a v0.1.0 -m "Release v0.1.0"
git push origin v0.1.0
```

The GitHub Actions workflow will:
1. Build frontend once
2. Publish .NET for all 4 platforms in parallel (~15-20 min total)
3. Create installers for each platform
4. Create GitHub Release with all artifacts attached
5. Generate release notes

**Manual package creation** (if needed):
```bash
# After dotnet publish...
installers/create-macos-app.sh 0.1.0 arm64
installers/create-linux-package.sh 0.1.0
# Windows requires Inno Setup + iscc command
```

### For Users

**Windows**: Download and run `Zeus-X.Y.Z-win-x64-setup.exe`

**macOS**:
1. Download `Zeus-X.Y.Z-macos-{arm64|x64}.dmg`
2. Drag to Applications
3. **Run**: `xattr -cr /Applications/Zeus.app`

**Linux**:
1. Download `zeus-X.Y.Z-linux-x64.tar.gz`
2. Extract: `tar -xzf zeus-*.tar.gz`
3. Run: `cd zeus-*/ && ./zeus`

**Check for updates**: Open Zeus → Settings → About → "Check for Updates"

## Platform-Specific Notes

### Windows
- Installer is unsigned - Windows Defender SmartScreen will warn users
- Users familiar with installing unsigned software (common in ham radio community)
- Installer requires admin rights to install to Program Files (standard practice)

### macOS
- Apps are **unsigned** - not a registered Apple Developer
- **Critical**: Users MUST run `xattr -cr /Applications/Zeus.app` after installation
- This is documented prominently in:
  - Release notes
  - DMG README.txt
  - Installation documentation
  - Issue comment from maintainer
- Without `xattr -cr`, macOS Gatekeeper blocks the app ("damaged and can't be opened")

### Linux
- Tarball distribution (no .deb or .rpm yet)
- Requires `libfftw3` (usually pre-installed)
- Launch script handles both GUI and headless environments
- Optional desktop launcher instructions provided

## Testing

All workflows and scripts were designed but **not executed** in this implementation (CI environment). Recommended testing:

1. **Publish profiles**: ✅ Tested `linux-x64` profile - works correctly
2. **Build workflow**: Should be tested on next push to develop/main
3. **Release workflow**: Should be tested by creating a test tag (e.g., `v0.0.1-test`)
4. **Installers**: Each platform installer should be built and tested manually:
   - Windows: Build on Windows with Inno Setup installed
   - macOS: Build on Mac (both Intel and Apple Silicon if possible)
   - Linux: Build on any Linux system
5. **Version API**: Works locally, should be verified after deployment
6. **Update checker**: Will work once first release is published

## Future Enhancements (Not Implemented)

These were considered but not implemented to keep changes minimal:

1. **Code signing**:
   - Windows: Authenticode certificate ($)
   - macOS: Apple Developer account + notarization ($99/year)
   - Would eliminate security warnings

2. **Auto-update mechanism**:
   - Currently users manually download new versions
   - Could add Squirrel.Windows, Sparkle (macOS), or custom updater

3. **Linux .deb/.rpm packages**:
   - Currently using tarball
   - Could add debian/SPECS directories and package builds

4. **AOT compilation**:
   - Issue requested AOT if feasible
   - .NET 10 Native AOT has limitations with reflection/P/Invoke
   - WDSP P/Invoke likely incompatible with full AOT
   - Could investigate in future .NET versions

5. **Installer customization**:
   - Custom graphics/branding in Windows installer
   - More polished DMG with background image (macOS)

## Known Limitations

1. **macOS**: Unsigned app requires `xattr -cr` (major friction point)
2. **Windows**: SmartScreen warnings for unsigned installer
3. **No AOT**: Using R2R instead (still fast startup, larger size)
4. **Frontend bundle size**: 676 KB (see Vite warning) - could be code-split
5. **No ARM64 Windows**: WDSP has SSE intrinsics incompatible with ARM64 Windows

## Files Changed/Created

### Created:
- `zeus-web/src/components/AboutPanel.tsx` (version display + update checker)
- `OpenhpsdrZeus/Properties/PublishProfiles/*.pubxml` (4 files)
- `installers/zeus-windows.iss`
- `installers/create-macos-app.sh`
- `installers/create-linux-package.sh`
- `.github/workflows/build-test.yml`
- `.github/workflows/release.yml`
- `docs/INSTALLATION.md`
- `docs/RELEASE.md`

### Modified:
- `Directory.Build.props` (version management)
- `OpenhpsdrZeus/Program.cs` (added /api/version endpoint)
- `zeus-web/src/components/SettingsMenu.tsx` (added About tab)
- `.gitignore` (added installer output dirs)

### Total: 16 files changed, ~1,600 lines added

## Conclusion

This implementation provides **complete automated release infrastructure** for Zeus:
- ✅ Version management (dev builds, release builds from tags)
- ✅ Native installers for all 3 desktop platforms (4 architectures)
- ✅ Automated CI/CD (build, test, release workflows)
- ✅ Update checker in web UI
- ✅ Comprehensive documentation for users and maintainers

**Next step**: Create a test release (e.g., `v0.0.1-alpha`) to verify the full workflow end-to-end.
