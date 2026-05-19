#!/bin/bash
# Build Zeus.app + Zeus Server.app (two-icon install) and a drag-to-install
# DMG for macOS.
# Usage: ./create-macos-app.sh <version> <arch>
# Example: ./create-macos-app.sh 0.4.1 arm64
#
# Two desktop icons ship in the DMG:
#   - Zeus.app           → Photino native window (--desktop). Full app.
#   - Zeus Server.app    → service mode + small Photino status window
#                          (--server). Thin wrapper that exec's Zeus.app's
#                          OpenhpsdrZeus binary; requires Zeus.app in
#                          /Applications. Headless Terminal users can still
#                          invoke Zeus.app's binary with no flags or the
#                          openhpsdr-zeus-server helper.
#
# CI calls this after `dotnet publish OpenhpsdrZeus/...` has already
# populated PUBLISH_DIR. If invoked locally with no prior publish (e.g. a
# fresh checkout), the script falls back to doing its own publish so a
# developer can run it standalone.

set -e

VERSION="${1:-0.0.0}"
ARCH="${2:-arm64}"  # arm64 or x64

if [ "$ARCH" != "arm64" ] && [ "$ARCH" != "x64" ]; then
    echo "Error: ARCH must be 'arm64' or 'x64'"
    exit 1
fi

echo "Creating Zeus.app for macOS ${ARCH} v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/OpenhpsdrZeus/bin/Release/net10.0/osx-${ARCH}/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
APP_NAME="Zeus.app"
APP_BUNDLE="${OUTPUT_DIR}/${APP_NAME}"
ICON_SOURCE="${REPO_ROOT}/docs/pics/zeus.png"

# Self-contained publish fallback for local-dev use: only runs if PUBLISH_DIR
# is missing or empty. CI's release.yml runs a single shared `dotnet publish`
# step before any installer-script invocation, so this fallback is skipped
# there. PublishSingleFile=false keeps each managed dll separate; Photino's
# native libs and libwdsp.dylib need to load from runtimes/osx-* anyway, so
# single-file packaging would only save a few hundred KB at the cost of
# harder symbol resolution.
if [ ! -d "${PUBLISH_DIR}" ] || [ -z "$(ls -A "${PUBLISH_DIR}" 2>/dev/null)" ]; then
    echo "PUBLISH_DIR is missing — falling back to local publish for osx-${ARCH}..."
    dotnet publish "${REPO_ROOT}/OpenhpsdrZeus/OpenhpsdrZeus.csproj" \
        -c Release \
        -r "osx-${ARCH}" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:UseAppHost=true \
        -o "${PUBLISH_DIR}"
fi

# Clean and create output directory
rm -rf "${APP_BUNDLE}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

# Copy published files into Contents/MacOS — the bundle's working dir at
# launch is Contents/MacOS, so the relative wwwroot/, appsettings.json,
# zetaHat.bin etc. land where ZeusHost expects.
echo "Copying published files..."
cp -r "${PUBLISH_DIR}"/* "${APP_BUNDLE}/Contents/MacOS/"

# Generate Zeus.icns from docs/pics/zeus.png so Finder, Dock, and Cmd-Tab
# show the Zeus artwork. iconutil + sips ship with Xcode CLT (present on
# every GitHub macos-latest runner and any dev box that has built native
# code on macOS before).
if [ -f "${ICON_SOURCE}" ]; then
    echo "Generating Zeus.icns from ${ICON_SOURCE}..."
    ICONSET_DIR="${OUTPUT_DIR}/Zeus.iconset"
    rm -rf "${ICONSET_DIR}"
    mkdir -p "${ICONSET_DIR}"
    sips -z 16 16     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_16x16.png"     >/dev/null
    sips -z 32 32     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_16x16@2x.png"  >/dev/null
    sips -z 32 32     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_32x32.png"     >/dev/null
    sips -z 64 64     "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_32x32@2x.png"  >/dev/null
    sips -z 128 128   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_128x128.png"   >/dev/null
    sips -z 256 256   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_128x128@2x.png">/dev/null
    sips -z 256 256   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_256x256.png"   >/dev/null
    sips -z 512 512   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_256x256@2x.png">/dev/null
    sips -z 512 512   "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_512x512.png"   >/dev/null
    sips -z 1024 1024 "${ICON_SOURCE}" --out "${ICONSET_DIR}/icon_512x512@2x.png">/dev/null
    iconutil -c icns "${ICONSET_DIR}" -o "${APP_BUNDLE}/Contents/Resources/Zeus.icns"
    rm -rf "${ICONSET_DIR}"
else
    echo "Warning: ${ICON_SOURCE} not found — building Zeus.app without an icon."
fi

# Info.plist. CFBundleExecutable points at launch.sh (not OpenhpsdrZeus
# directly) so we can pin DYLD_LIBRARY_PATH before the .NET runtime loads
# libwdsp.dylib. CFBundleIdentifier reuses the historical service-mode ID
# (com.ei6lf.zeus) so existing service-mode .app installs upgrade in
# place. The older "com.ei6lf.zeus.desktop" bundle ID is dropped — users
# with that .app must Trash it manually; release notes call this out.
cat > "${APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>launch.sh</string>
    <key>CFBundleIconFile</key>
    <string>Zeus</string>
    <key>CFBundleIdentifier</key>
    <string>com.ei6lf.zeus</string>
    <key>CFBundleName</key>
    <string>Zeus</string>
    <key>CFBundleDisplayName</key>
    <string>Zeus</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>ZEUS</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSUIElement</key>
    <false/>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>

    <!-- Privacy descriptors — required because the Photino webview hosted
         inside this .app uses getUserMedia for TX mic uplink. Without
         these, macOS TCC SIGKILLs the app on first launch via
         LaunchServices even before anything is recorded: WKWebView probes
         mic+cam capabilities at page-load. Camera is declared defensively —
         the SPA does not capture video today, but WKWebView's
         enumerateDevices() touches both buckets. Service mode launched
         from Terminal goes through the user's browser (separate TCC
         profile) so these don't apply there, but they're harmless. -->
    <key>NSMicrophoneUsageDescription</key>
    <string>Zeus uses the microphone for SSB / digital-mode TX uplink to your radio when you key MOX.</string>
    <key>NSCameraUsageDescription</key>
    <string>Zeus does not record video. The OS asks because the embedded webview lists media devices.</string>
</dict>
</plist>
EOF

# Make the binary executable (cp -r usually preserves mode but be defensive)
chmod +x "${APP_BUNDLE}/Contents/MacOS/OpenhpsdrZeus"

# Launcher (Contents/MacOS/launch.sh): pins DYLD_LIBRARY_PATH so the
# bundled libwdsp.dylib wins over any older copy in /usr/local/lib or
# /opt/homebrew/lib (e.g. from a piHPSDR / DeskHPSDR install). Then exec's
# OpenhpsdrZeus with --desktop so a normal click on the .app opens the
# Photino window. exec replaces the shell so Cmd-Q / Dock-Quit / Force-Quit
# tear down the right process.
cat > "${APP_BUNDLE}/Contents/MacOS/launch.sh" << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"

# Pin the bundled libwdsp.dylib. macOS dlopen does not search the
# executable's directory by default, so without this line P/Invoke can
# bind against a stale dylib that pre-dates symbols Zeus relies on (e.g.
# SetRXAEMNRpost2*). Both arches are listed so the same launcher works on
# arm64 and x64 builds; the loader silently skips a path that does not
# exist.
export DYLD_LIBRARY_PATH="$(pwd)/runtimes/osx-arm64/native:$(pwd)/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"

exec ./OpenhpsdrZeus --desktop
EOF
chmod +x "${APP_BUNDLE}/Contents/MacOS/launch.sh"

# Server-mode wrapper (Contents/Resources/openhpsdr-zeus-server): same
# DYLD pin, but exec's OpenhpsdrZeus without --desktop. Operators run this
# from Terminal when they want the LAN-bound HTTP service. Lives under
# Resources/ (not MacOS/) so it doesn't get launched by accident through
# LaunchServices — it's a CLI tool, not the app's main executable.
cat > "${APP_BUNDLE}/Contents/Resources/openhpsdr-zeus-server" << 'EOF'
#!/bin/bash
# Run Openhpsdr Zeus in server mode (LAN-bound HTTP on :6060).
# Usage: /Applications/Zeus.app/Contents/Resources/openhpsdr-zeus-server
APP_MACOS_DIR="$(cd "$(dirname "$0")/../MacOS" && pwd)"
cd "${APP_MACOS_DIR}"
export DYLD_LIBRARY_PATH="${APP_MACOS_DIR}/runtimes/osx-arm64/native:${APP_MACOS_DIR}/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"
exec ./OpenhpsdrZeus "$@"
EOF
chmod +x "${APP_BUNDLE}/Contents/Resources/openhpsdr-zeus-server"

echo "App bundle created at ${APP_BUNDLE}"

# --- Zeus Server.app (thin wrapper) -------------------------------------
#
# Second drag-to-install icon that runs the same Zeus binary in --server
# mode (Photino status window + LAN HTTP/HTTPS bind + Stop button). The
# wrapper bundle is tiny — just an Info.plist, the shared icns, and a
# launch.sh that exec's Zeus.app's OpenhpsdrZeus with --server. This
# keeps the DMG roughly the same size as the single-app DMG (only ~100KB
# added) while giving operators a discoverable "Server" icon in
# /Applications. The wrapper requires Zeus.app to live at /Applications/
# Zeus.app — the launch.sh shows an osascript dialog if it isn't there,
# matching the drag-to-install model on the DMG window.
SERVER_APP_NAME="Zeus Server.app"
SERVER_APP_BUNDLE="${OUTPUT_DIR}/${SERVER_APP_NAME}"
rm -rf "${SERVER_APP_BUNDLE}"
mkdir -p "${SERVER_APP_BUNDLE}/Contents/MacOS"
mkdir -p "${SERVER_APP_BUNDLE}/Contents/Resources"

# Reuse the same Zeus.icns so Finder draws the Zeus artwork on both icons.
if [ -f "${APP_BUNDLE}/Contents/Resources/Zeus.icns" ]; then
    cp "${APP_BUNDLE}/Contents/Resources/Zeus.icns" \
       "${SERVER_APP_BUNDLE}/Contents/Resources/Zeus.icns"
fi

# Distinct bundle ID + name so LaunchServices treats this as a separate
# app (independent dock entry, separate Cmd-Tab listing). LSMinimumSystemVersion
# matches Zeus.app since the wrapper's only at-launch dependency is the OS
# osascript + the Zeus.app binary it exec's.
cat > "${SERVER_APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>launch.sh</string>
    <key>CFBundleIconFile</key>
    <string>Zeus</string>
    <key>CFBundleIdentifier</key>
    <string>com.ei6lf.zeus.server</string>
    <key>CFBundleName</key>
    <string>Zeus Server</string>
    <key>CFBundleDisplayName</key>
    <string>Zeus Server</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>ZESV</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSUIElement</key>
    <false/>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>

    <!-- Same TCC descriptors as Zeus.app — the underlying binary is the
         same, so the embedded webview still touches enumerateDevices()
         which probes mic + cam. -->
    <key>NSMicrophoneUsageDescription</key>
    <string>Zeus uses the microphone for SSB / digital-mode TX uplink to your radio when you key MOX.</string>
    <key>NSCameraUsageDescription</key>
    <string>Zeus does not record video. The OS asks because the embedded webview lists media devices.</string>
</dict>
</plist>
EOF

# Wrapper launcher: find Zeus.app in /Applications, exec its bundled
# OpenhpsdrZeus with --server. If Zeus.app is missing, show an osascript
# dialog so the operator knows what to do — this avoids a silent failure
# when the user only dragged Zeus Server.app from the DMG.
cat > "${SERVER_APP_BUNDLE}/Contents/MacOS/launch.sh" << 'EOF'
#!/bin/bash
ZEUS_APP="/Applications/Zeus.app"
if [ ! -d "${ZEUS_APP}" ]; then
    osascript -e 'display dialog "Zeus.app must be installed in /Applications first. Drag Zeus.app from the DMG to your Applications folder, then try Zeus Server again." with title "Zeus Server" with icon caution buttons {"OK"} default button "OK"' >/dev/null 2>&1
    exit 1
fi
APP_MACOS_DIR="${ZEUS_APP}/Contents/MacOS"
cd "${APP_MACOS_DIR}"
export DYLD_LIBRARY_PATH="${APP_MACOS_DIR}/runtimes/osx-arm64/native:${APP_MACOS_DIR}/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"
exec ./OpenhpsdrZeus --server
EOF
chmod +x "${SERVER_APP_BUNDLE}/Contents/MacOS/launch.sh"

echo "Server wrapper bundle created at ${SERVER_APP_BUNDLE}"

# --- Codesigning hook (opt-in) ------------------------------------------
#
# Ad-hoc signing or Developer ID signing happens here. Default behaviour
# is to do nothing — the bundle ships unsigned and the user clears the
# quarantine xattr on first launch (see DMG README).
#
# To produce a Developer ID Application signed bundle:
#   export APPLE_DEVELOPER_ID="Developer ID Application: Brian Keating (TEAMID)"
#   ./create-macos-app.sh 0.4.1 arm64
# Notarisation is a separate step (notarytool submit ... --wait) once the
# Apple ID + app-specific password is configured locally — keep that out
# of the script for now so the unsigned path stays the default and CI
# doesn't trip over missing secrets.
if [ -n "${APPLE_DEVELOPER_ID:-}" ]; then
    echo "Codesigning with: ${APPLE_DEVELOPER_ID}"
    codesign --force --deep --options runtime --timestamp \
        --sign "${APPLE_DEVELOPER_ID}" "${APP_BUNDLE}"
    codesign --verify --verbose=2 "${APP_BUNDLE}"
    # Sign Zeus Server.app too — Gatekeeper otherwise pops a separate
    # "unidentified developer" prompt for the wrapper.
    codesign --force --deep --options runtime --timestamp \
        --sign "${APPLE_DEVELOPER_ID}" "${SERVER_APP_BUNDLE}"
    codesign --verify --verbose=2 "${SERVER_APP_BUNDLE}"
else
    echo "(unsigned — set APPLE_DEVELOPER_ID to enable codesigning)"
fi

# --- DMG ----------------------------------------------------------------

DMG_NAME="OpenhpsdrZeus-${VERSION}-macos-${ARCH}.dmg"
DMG_PATH="${OUTPUT_DIR}/${DMG_NAME}"

echo "Creating DMG..."
rm -f "${DMG_PATH}"

# Stage DMG contents:
#   Zeus.app                — full Photino desktop app
#   Zeus Server.app         — thin wrapper, opens --server status window
#   Applications -> /Applications  — drag-to-install target
#   README.txt              — xattr / first-launch instructions
DMG_TEMP="${OUTPUT_DIR}/dmg_temp"
rm -rf "${DMG_TEMP}"
mkdir -p "${DMG_TEMP}"
cp -R "${APP_BUNDLE}" "${DMG_TEMP}/"
cp -R "${SERVER_APP_BUNDLE}" "${DMG_TEMP}/"
ln -s /Applications "${DMG_TEMP}/Applications"

cat > "${DMG_TEMP}/README.txt" << 'EOF'
Openhpsdr Zeus for macOS
========================

INSTALL
  Drag BOTH Zeus.app and Zeus Server.app onto the Applications shortcut
  in this window. Zeus Server.app is a small wrapper around Zeus.app
  and won't work without Zeus.app installed.

  If you only want the desktop app, dragging Zeus.app alone is fine.

FIRST LAUNCH (important — Zeus is not signed)
  Zeus is distributed without an Apple Developer ID, so macOS Gatekeeper
  will block it on first launch. To clear the quarantine flag, open
  Terminal and run:

      xattr -cr /Applications/Zeus.app
      xattr -cr "/Applications/Zeus Server.app"

  Then launch Zeus from Applications.

  If you still see a security warning, go to:
      System Settings -> Privacy & Security
  and click "Open Anyway".

THE TWO ICONS

  Zeus.app           Full native window. The radio backend runs
                     in-process inside the same window. Closing the
                     window stops Zeus completely. This is the right
                     choice for most operators.

  Zeus Server.app    Backend-only mode for LAN / remote / phone access.
                     Opens a small status window showing the URLs to
                     connect to from a browser, with a Stop Zeus button.
                     Connect from this Mac at http://localhost:6060 or
                     from another device at http://<your-mac>:6060.
                     HTTPS uses a self-signed certificate — accept the
                     browser warning on first connect.

HEADLESS / CLI USE
  If you're running Zeus on a headless Mac (no display, e.g. an mac mini
  in a closet), use Terminal:

      /Applications/Zeus.app/Contents/MacOS/OpenhpsdrZeus

  This is the no-window service mode. Identical to Zeus Server.app's
  backend but without the status window. Closing the Terminal (or
  Ctrl-C) stops the server.

FIRST RUN — WDSP WISDOM
  The first launch builds an FFTW "wisdom" cache and can take 1-3
  minutes. The window will load, but do NOT click Discover/Connect
  until the wisdom build settles. Subsequent launches are instant.

More info: https://github.com/Kb2uka/openhpsdr-zeus
EOF

hdiutil create -volname "Openhpsdr Zeus v${VERSION}" \
    -srcfolder "${DMG_TEMP}" \
    -ov -format UDZO \
    "${DMG_PATH}"

rm -rf "${DMG_TEMP}"

echo "DMG created at ${DMG_PATH}"
echo
echo "NOTE: users must clear the quarantine flag on first launch:"
echo "  xattr -cr /Applications/Zeus.app"
