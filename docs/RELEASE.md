# Release Process

This document describes how to create a new Zeus release.

## Prerequisites

- Write access to the repository
- All tests passing on `main` or `develop` branch
- Changelog/release notes prepared

## Release Steps

### 1. Prepare the Release

1. Ensure all changes for the release are merged to `develop` (or `main`)
2. Update version references in documentation if needed
3. Verify all tests pass:
   ```bash
   dotnet test Zeus.slnx
   npm --prefix zeus-web test
   ```

### 2. Create and Push the Version Tag

Tags **must** follow the pattern `v<major>.<minor>.<patch>` (e.g., `v0.1.0`, `v1.2.3`).

```bash
# Example for version 0.1.0
VERSION="0.1.0"
git tag -a "v${VERSION}" -m "Release v${VERSION}"
git push origin "v${VERSION}"
```

The GitHub Actions release workflow will automatically trigger when the tag is pushed.

### 3. Monitor the Release Workflow

1. Go to the [Actions tab](https://github.com/Kb2uka/openhpsdr-zeus/actions)
2. Watch the "Release" workflow
3. The workflow will:
   - Build the frontend
   - Publish .NET apps for all platforms (Windows x64, Linux x64, macOS x64/arm64)
   - Create Windows installer (Inno Setup)
   - Create macOS DMGs
   - Create Linux tarball
   - Create a GitHub Release with all artifacts attached

The workflow takes approximately 15-30 minutes to complete (building on all platforms in parallel).

### 4. Verify the Release

Once the workflow completes:

1. Go to the [Releases page](https://github.com/Kb2uka/openhpsdr-zeus/releases)
2. Find your new release (e.g., "Zeus v0.1.0")
3. Verify all artifacts are present:
   - `Zeus-X.Y.Z-win-x64-setup.exe`
   - `Zeus-X.Y.Z-macos-arm64.dmg`
   - `Zeus-X.Y.Z-macos-x64.dmg`
   - `zeus-X.Y.Z-linux-x64.tar.gz`
4. Check the release notes are correctly generated

### 5. (Optional) Edit Release Notes

The release notes are auto-generated but may need customization:

1. Click "Edit release" on the release page
2. Add specific changes, bug fixes, new features
3. Highlight any breaking changes
4. Add upgrade instructions if needed
5. Save the edited release

### 6. Announce the Release

Consider announcing the release through:
- Project README update (if major release)
- Social media / community channels
- Email to testers/beta users

---

## Manual Release (Fallback)

If the automated workflow fails or you need to build manually:

### Build All Platforms

```bash
VERSION="0.1.0"

# Build frontend (once)
cd zeus-web
npm ci
npm run build
cd ..

# Publish for each platform
dotnet publish Zeus.Server/Zeus.Server.csproj -c Release -r win-x64 \
  --self-contained true -p:VersionPrefix=$VERSION

dotnet publish Zeus.Server/Zeus.Server.csproj -c Release -r linux-x64 \
  --self-contained true -p:VersionPrefix=$VERSION

dotnet publish Zeus.Server/Zeus.Server.csproj -c Release -r osx-x64 \
  --self-contained true -p:VersionPrefix=$VERSION

dotnet publish Zeus.Server/Zeus.Server.csproj -c Release -r osx-arm64 \
  --self-contained true -p:VersionPrefix=$VERSION
```

### Create Installers

**Windows** (requires Windows + Inno Setup):
```powershell
iscc /DMyAppVersion="$VERSION" installers\zeus-windows.iss
```

**macOS**:
```bash
installers/create-macos-app.sh $VERSION arm64
installers/create-macos-app.sh $VERSION x64
```

**Linux**:
```bash
installers/create-linux-package.sh $VERSION
```

### Create Release Manually

1. Go to [Releases > Draft a new release](https://github.com/Kb2uka/openhpsdr-zeus/releases/new)
2. Tag: `v0.1.0` (create new tag)
3. Title: `Zeus v0.1.0`
4. Drag and drop all installer files
5. Fill in release notes
6. Publish

---

## Version Numbering

Zeus follows [Semantic Versioning](https://semver.org/):

- **Major** (X.0.0): Breaking changes, major features
- **Minor** (0.X.0): New features, backwards compatible
- **Patch** (0.0.X): Bug fixes, minor improvements

Example progression:
- `v0.1.0` - First public release
- `v0.1.1` - Bug fix release
- `v0.2.0` - New feature (e.g., Protocol-2 support)
- `v1.0.0` - First stable release

---

## Troubleshooting

### Release workflow fails

1. Check the workflow logs in the Actions tab
2. Common issues:
   - .NET SDK version mismatch - update `.github/workflows/release.yml`
   - Node.js version issue - update `setup-node` action
   - Missing dependencies on build runners

### Artifacts not attached to release

1. Check the "Download all artifacts" and "Create or Update Release" steps in workflow logs
2. Verify artifact upload succeeded in the per-platform build jobs
3. Re-run the workflow if needed

### Version mismatch in built artifacts

The version is controlled by:
1. Git tag name (e.g., `v0.1.0`)
2. Passed to dotnet publish via `-p:VersionPrefix=X.Y.Z`
3. Rendered in the app via `Directory.Build.props` and `/api/version`

If the version doesn't match:
- Verify the tag format is exactly `vX.Y.Z`
- Check `Directory.Build.props` version logic
- Rebuild with explicit `-p:VersionPrefix=X.Y.Z`

### macOS app won't run after download

This is expected for unsigned apps. Ensure the release notes include:
```bash
xattr -cr /Applications/Zeus.app
```

Users on macOS must run this command after installation.

---

## Release Checklist

Use this checklist for each release:

- [ ] All changes merged to release branch
- [ ] Tests passing (`dotnet test`, `npm test`)
- [ ] Version number decided (semver)
- [ ] Tag created and pushed (`v{version}`)
- [ ] Release workflow completed successfully
- [ ] All 4 artifacts present on release page
- [ ] Release notes reviewed and accurate
- [ ] Installation instructions in release notes
- [ ] macOS `xattr` warning present in release notes
- [ ] Release published (not draft)
- [ ] Announcement posted (if applicable)

---

## Post-Release

After a successful release:

1. Monitor issues for installation problems
2. Test the update checker in the About panel
3. Verify installers on each platform if possible
4. Update project roadmap/milestones

---

## Contact

For questions about the release process, open an issue or contact the maintainer (Brian, EI6LF).
