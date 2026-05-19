#!/bin/bash
# Build Openhpsdr Zeus as TWO Linux AppImages — one per launch mode.
# Usage: ./create-linux-desktop-appimage.sh <version>
# Example: ./create-linux-desktop-appimage.sh 0.4.1
#
# Output:
#   OpenhpsdrZeus-<version>-linux-x86_64.AppImage         — desktop window
#   OpenhpsdrZeus-Server-<version>-linux-x86_64.AppImage  — backend + status window
#
# Both wrap the same OpenhpsdrZeus binary; they differ only in the AppRun
# / .desktop Exec line (--desktop vs --server). Users grab whichever icon
# they want; if they install both into the same dir they get two
# distinct file-manager / launcher entries.
#
# Companion to create-linux-package.sh which packages the same binary
# plus all three launchers (--, --desktop, --server) as a tarball.
#
# AppImage was chosen over .deb / .rpm for v1 because it runs unchanged
# on any glibc 2.31+ distro (Debian 11, Ubuntu 22.04+, Fedora 36+, Arch,
# etc.) and operators don't need root to install it. .deb / .rpm can be
# layered on later if there is demand.
#
# Runtime dependency: libwebkit2gtk-4.1-0 (Photino's WebView2 equivalent
# on Linux). Bundling WebKitGTK in the AppImage would push the artifact
# from ~80 MB to ~250 MB and lock us to a specific WebKit version, so
# we leave it as a system package and document it in the AppDir README.

set -e

VERSION="${1:-0.0.0}"

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "Warning: AppImage build is intended to run on Linux. Continuing anyway"
    echo "         — the dotnet publish step will work, but appimagetool needs"
    echo "         a Linux kernel for the squashfs invocation."
fi

echo "Creating Openhpsdr Zeus AppImage v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/OpenhpsdrZeus/bin/Release/net10.0/linux-x64/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APPDIR="${OUTPUT_DIR}/OpenhpsdrZeus.AppDir"
ICON_SOURCE="${REPO_ROOT}/docs/pics/zeus.png"

# Self-contained publish fallback for local-dev use: only runs if PUBLISH_DIR
# is missing or empty. CI's release.yml runs a single shared `dotnet publish`
# step before any installer-script invocation, so this fallback is skipped
# there.
if [ ! -d "${PUBLISH_DIR}" ] || [ -z "$(ls -A "${PUBLISH_DIR}" 2>/dev/null)" ]; then
    echo "PUBLISH_DIR is missing — falling back to local publish for linux-x64..."
    dotnet publish "${REPO_ROOT}/OpenhpsdrZeus/OpenhpsdrZeus.csproj" \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:UseAppHost=true \
        -o "${PUBLISH_DIR}"
fi

# Build AppDir layout per the AppImage convention
# (https://docs.appimage.org/packaging-guide/manual.html).
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/512x512/apps"

echo "Staging publish output into AppDir..."
cp -r "${PUBLISH_DIR}"/* "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/OpenhpsdrZeus"

# Icon — top-level zeus.png is what AppImageLauncher / file managers show.
if [ -f "${ICON_SOURCE}" ]; then
    cp "${ICON_SOURCE}" "${APPDIR}/usr/share/icons/hicolor/512x512/apps/zeus.png"
    cp "${ICON_SOURCE}" "${APPDIR}/zeus.png"
else
    echo "Warning: ${ICON_SOURCE} not found — AppImage will ship without an icon."
fi

# Desktop entry. The top-level zeus.desktop is what appimagetool picks up;
# the share copy is what desktop-file integration tools install.
cat > "${APPDIR}/zeus.desktop" << EOF
[Desktop Entry]
Type=Application
Name=Openhpsdr Zeus
GenericName=OpenHPSDR SDR Client
Comment=Cross-platform HPSDR client (Protocol-1 / Protocol-2)
Exec=OpenhpsdrZeus --desktop
Icon=zeus
Categories=AudioVideo;HamRadio;
Terminal=false
StartupWMClass=Zeus
EOF
cp "${APPDIR}/zeus.desktop" "${APPDIR}/usr/share/applications/zeus.desktop"

# AppRun — entry point that AppImage invokes. Pins LD_LIBRARY_PATH so the
# bundled libwdsp.so wins over /usr/lib copies (e.g. from a piHPSDR build),
# same reason as create-linux-package.sh's launcher. Always launches in
# desktop mode (--desktop) — the AppImage is the single-file Photino
# launcher; service mode lives in the tarball.
cat > "${APPDIR}/AppRun" << 'EOF'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}/usr/bin/runtimes/linux-x64/native:${HERE}/usr/bin/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"
cd "${HERE}/usr/bin"
exec ./OpenhpsdrZeus --desktop "$@"
EOF
chmod +x "${APPDIR}/AppRun"

# README inside the AppDir, surfaced as a sibling file in the squashfs.
cat > "${APPDIR}/README.txt" << EOF
Openhpsdr Zeus v${VERSION} for Linux (AppImage)

USAGE
  chmod +x OpenhpsdrZeus-${VERSION}-linux-x86_64.AppImage
  ./OpenhpsdrZeus-${VERSION}-linux-x86_64.AppImage

  Optional: integrate with your desktop:
    sudo apt install appimagelauncher   # Debian/Ubuntu
    # then double-click the .AppImage

REQUIREMENTS
  - Linux x86_64, glibc 2.31+ (Debian 11+, Ubuntu 22.04+, Fedora 36+, Arch, …)
  - libwebkit2gtk-4.1-0 (Photino's webview backend)

      Debian/Ubuntu:  sudo apt install libwebkit2gtk-4.1-0
      Fedora:         sudo dnf install webkit2gtk4.1
      Arch:           sudo pacman -S webkit2gtk-4.1

  WebKitGTK is intentionally NOT bundled — at ~150 MB it would more than
  triple the AppImage size and lock us to a specific WebKit release. As a
  system library it picks up your distro's security patches automatically.

WHAT YOU GET
  A native window. Closing it stops Zeus completely — there is no separate
  server process. For a browser-based / remote-friendly install, see the
  service-mode tarball (openhpsdr-zeus-${VERSION}-linux-x64.tar.gz).

More info: https://github.com/brianbruff/openhpsdr-zeus
License:   GNU GPL v2 or later
EOF

# --- AppImage assembly ---------------------------------------------------

# Locate or download appimagetool. We prefer a version already on PATH or
# in OUTPUT_DIR; otherwise grab the upstream continuous release. CI runs
# with --appimage-extract-and-run so we don't need FUSE2 in the runner.
APPIMAGETOOL=""
if command -v appimagetool &>/dev/null; then
    APPIMAGETOOL="$(command -v appimagetool)"
elif [ -x "${OUTPUT_DIR}/appimagetool-x86_64.AppImage" ]; then
    APPIMAGETOOL="${OUTPUT_DIR}/appimagetool-x86_64.AppImage"
else
    echo "Downloading appimagetool..."
    curl -fsSL -o "${OUTPUT_DIR}/appimagetool-x86_64.AppImage" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "${OUTPUT_DIR}/appimagetool-x86_64.AppImage"
    APPIMAGETOOL="${OUTPUT_DIR}/appimagetool-x86_64.AppImage"
fi

OUTPUT_APPIMAGE="${OUTPUT_DIR}/OpenhpsdrZeus-${VERSION}-linux-x86_64.AppImage"

# --appimage-extract-and-run avoids the FUSE2 dependency that GitHub-hosted
# runners (and most container envs) don't satisfy out of the box.
echo "Building desktop-mode AppImage..."
ARCH=x86_64 "${APPIMAGETOOL}" --appimage-extract-and-run "${APPDIR}" "${OUTPUT_APPIMAGE}"

echo "Desktop AppImage created at ${OUTPUT_APPIMAGE}"

# --- Server-mode AppImage (--server) -----------------------------------
#
# Stage a parallel AppDir whose AppRun + .desktop point at OpenhpsdrZeus
# --server (backend + Photino status window with URLs and Stop button).
# The binary itself is the same; we just swap the launcher.
SERVER_APPDIR="${OUTPUT_DIR}/OpenhpsdrZeusServer.AppDir"
rm -rf "${SERVER_APPDIR}"
cp -r "${APPDIR}" "${SERVER_APPDIR}"
# Overwrite AppRun + .desktop for server mode. The icon stays the same so
# both AppImages render with the Zeus artwork — operators tell them
# apart by filename ("...-Server-...") and by the .desktop Name.
cat > "${SERVER_APPDIR}/AppRun" << 'EOF'
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}/usr/bin/runtimes/linux-x64/native:${HERE}/usr/bin/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"
cd "${HERE}/usr/bin"
exec ./OpenhpsdrZeus --server "$@"
EOF
chmod +x "${SERVER_APPDIR}/AppRun"

cat > "${SERVER_APPDIR}/zeus.desktop" << EOF
[Desktop Entry]
Type=Application
Name=Openhpsdr Zeus Server
GenericName=OpenHPSDR SDR Backend
Comment=LAN-bound HPSDR backend with status window and Stop button
Exec=OpenhpsdrZeus --server
Icon=zeus
Categories=AudioVideo;HamRadio;Network;
Terminal=false
StartupWMClass=Zeus Server
EOF
cp "${SERVER_APPDIR}/zeus.desktop" "${SERVER_APPDIR}/usr/share/applications/zeus.desktop"

OUTPUT_SERVER_APPIMAGE="${OUTPUT_DIR}/OpenhpsdrZeus-Server-${VERSION}-linux-x86_64.AppImage"
echo "Building server-mode AppImage..."
ARCH=x86_64 "${APPIMAGETOOL}" --appimage-extract-and-run "${SERVER_APPDIR}" "${OUTPUT_SERVER_APPIMAGE}"

echo "Server AppImage created at ${OUTPUT_SERVER_APPIMAGE}"
echo
echo "To run:"
echo "  chmod +x ${OUTPUT_APPIMAGE} ${OUTPUT_SERVER_APPIMAGE}"
echo "  ${OUTPUT_APPIMAGE}            # Photino window (--desktop)"
echo "  ${OUTPUT_SERVER_APPIMAGE}     # backend + status window (--server)"
