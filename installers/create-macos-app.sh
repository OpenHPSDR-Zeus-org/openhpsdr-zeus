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
APP_NAME="OpenHPSDR Zeus.app"
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

# --- Codesign-friendly payload layout (issue gh-389 / zeus-z98) ----------
#
# codesign without the deprecated --deep treats EVERY file under
# Contents/MacOS/ as a code subcomponent that must be individually signed,
# and aborts on the first non-Mach-O it walks ("code object is not signed at
# all / In subcomponent: <dll|json|…>"). A .NET self-contained publish drops
# ~360 such files there (managed .dll, .pdb, .deps.json, runtimeconfig.json,
# wwwroot/, BandPlans/, …), so the old script was forced into --deep.
#
# Files under Contents/Resources/, by contrast, are sealed as ordinary
# resources (hashed into CodeResources) and do NOT each need a signature.
# So we put the entire publish payload in Contents/Resources/app/ and leave
# Contents/MacOS/ holding only the launch.sh stub (the CFBundleExecutable).
# The bundle then signs inside-out with no --deep: we sign just the nested
# Mach-O (the apphost + the ~17 dylibs) for the hardened-runtime requirement,
# and codesign seals everything else as resources.
#
# Because the apphost still runs with its payload directory as the working
# dir (launch.sh cd's into Resources/app before exec), AppContext.BaseDirectory
# resolves to Resources/app/ and wwwroot/, BandPlans/, zetaHat.bin, calculus
# and runtimes/ all sit next to the binary exactly as the backend expects —
# no relocation, no flattening. The launcher additionally exports
# ZEUS_WEBROOT / ZEUS_BANDPLANS_DIR (honoured by ZeusHost / BandPlanStore) as
# an explicit belt-and-suspenders pin.
APP_PAYLOAD="${APP_BUNDLE}/Contents/Resources/app"
mkdir -p "${APP_PAYLOAD}"
echo "Copying published files into Contents/Resources/app ..."
cp -r "${PUBLISH_DIR}"/* "${APP_PAYLOAD}/"

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

# Info.plist. CFBundleExecutable points at a COMPILED launcher (zeus-launch),
# not a shell script: a /bin/sh CFBundleExecutable makes macOS TCC attribute the
# microphone request to the shell interpreter, so the permission prompt never
# appears and native TX-mic capture is silently denied on signed builds. A
# compiled Mach-O launcher carries the bundle's code identity, so TCC prompts and
# grants correctly. The launcher still pins DYLD_LIBRARY_PATH before the .NET
# runtime loads libwdsp.dylib. CFBundleIdentifier reuses the historical
# service-mode ID
# (com.ei6lf.zeus) so existing service-mode .app installs upgrade in
# place. The older "com.ei6lf.zeus.desktop" bundle ID is detected and
# moved to the Trash by launch.sh on next start — see the cleanup block
# in the launcher below.
cat > "${APP_BUNDLE}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>zeus-launch</string>
    <key>CFBundleIconFile</key>
    <string>Zeus</string>
    <key>CFBundleIdentifier</key>
    <string>com.ei6lf.zeus</string>
    <key>CFBundleName</key>
    <string>OpenHPSDR Zeus</string>
    <key>CFBundleDisplayName</key>
    <string>OpenHPSDR Zeus</string>
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
chmod +x "${APP_PAYLOAD}/OpenhpsdrZeus"

# Launcher (Contents/MacOS/zeus-launch): a COMPILED Mach-O, NOT a shell script.
# A /bin/sh CFBundleExecutable makes macOS TCC attribute the microphone request
# to the shell interpreter, so the permission prompt never appears and native
# TX-mic capture is silently denied on signed builds — the long-standing Mac
# "can't use the mic" bug. A compiled launcher carries the app bundle's code
# identity, so TCC prompts and grants correctly, then exec's the apphost (which
# is itself signed with the audio-input entitlement). The launcher still pins
# DYLD_LIBRARY_PATH (bundled libwdsp.dylib must win over any /usr/local or
# Homebrew copy) and cd's into Contents/Resources/app (the payload lives there so
# the bundle codesigns without --deep; cwd = app dir so AppContext.BaseDirectory
# and WDSP's bare-relative fopen of zetaHat.bin/calculus resolve).
LAUNCH_SRC="$(mktemp -t zeus-launch).c"
cat > "${LAUNCH_SRC}" << 'EOF'
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <limits.h>
#include <libgen.h>
#include <mach-o/dyld.h>

int main(int argc, char *argv[]) {
    char exe[PATH_MAX]; uint32_t sz = sizeof(exe);
    if (_NSGetExecutablePath(exe, &sz) != 0) return 71;
    char resolved[PATH_MAX];
    if (!realpath(exe, resolved)) return 71;            /* <App>/Contents/MacOS/zeus-launch */
    char macos[PATH_MAX]; snprintf(macos, sizeof(macos), "%s", dirname(resolved));
    char appraw[PATH_MAX]; snprintf(appraw, sizeof(appraw), "%s/../Resources/app", macos);
    char app[PATH_MAX];
    if (!realpath(appraw, app)) return 71;              /* <App>/Contents/Resources/app */
    if (chdir(app) != 0) return 72;

    char dyld[3 * PATH_MAX];
    const char *prev = getenv("DYLD_LIBRARY_PATH");
    snprintf(dyld, sizeof(dyld), "%s/runtimes/osx-arm64/native:%s/runtimes/osx-x64/native%s%s",
             app, app, prev ? ":" : "", prev ? prev : "");
    setenv("DYLD_LIBRARY_PATH", dyld, 1);

    char p[PATH_MAX];
    snprintf(p, sizeof(p), "%s/wwwroot", app);   setenv("ZEUS_WEBROOT", p, 1);
    snprintf(p, sizeof(p), "%s/BandPlans", app); setenv("ZEUS_BANDPLANS_DIR", p, 1);

    char bin[PATH_MAX]; snprintf(bin, sizeof(bin), "%s/OpenhpsdrZeus", app);
    char **av = (char **)calloc((size_t)argc + 2, sizeof(char *));
    av[0] = bin; av[1] = "--desktop";
    for (int i = 1; i < argc; i++) av[i + 1] = argv[i];
    av[argc + 1] = NULL;
    execv(bin, av);
    perror("zeus-launch: execv OpenhpsdrZeus failed");
    return 127;
}
EOF
LAUNCH_ARCH="arm64"; [ "${ARCH}" = "x64" ] && LAUNCH_ARCH="x86_64"
cc -arch "${LAUNCH_ARCH}" -O2 -Wall -o "${APP_BUNDLE}/Contents/MacOS/zeus-launch" "${LAUNCH_SRC}"
rm -f "${LAUNCH_SRC}"
chmod +x "${APP_BUNDLE}/Contents/MacOS/zeus-launch"

# Server-mode wrapper (Contents/Resources/openhpsdr-zeus-server): same
# DYLD pin, but exec's OpenhpsdrZeus without --desktop. Operators run this
# from Terminal when they want the LAN-bound HTTP service. Lives under
# Resources/ (not MacOS/) so it doesn't get launched by accident through
# LaunchServices — it's a CLI tool, not the app's main executable.
cat > "${APP_BUNDLE}/Contents/Resources/openhpsdr-zeus-server" << 'EOF'
#!/bin/bash
# Run OpenHPSDR Zeus in server mode (LAN-bound HTTP on :6060).
# Usage: "/Applications/OpenHPSDR Zeus.app/Contents/Resources/openhpsdr-zeus-server"
# The .NET payload lives in the sibling app/ directory (Contents/Resources/app).
APP_DIR="$(cd "$(dirname "$0")/app" && pwd)"
cd "${APP_DIR}"
export DYLD_LIBRARY_PATH="${APP_DIR}/runtimes/osx-arm64/native:${APP_DIR}/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"
export ZEUS_WEBROOT="${APP_DIR}/wwwroot"
export ZEUS_BANDPLANS_DIR="${APP_DIR}/BandPlans"
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
SERVER_APP_NAME="OpenHPSDR Zeus Server.app"
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
    <string>OpenHPSDR Zeus Server</string>
    <key>CFBundleDisplayName</key>
    <string>OpenHPSDR Zeus Server</string>
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
ZEUS_APP="/Applications/OpenHPSDR Zeus.app"
if [ ! -d "${ZEUS_APP}" ]; then
    osascript -e 'display dialog "OpenHPSDR Zeus.app must be installed in /Applications first. Drag OpenHPSDR Zeus.app from the DMG to your Applications folder, then try OpenHPSDR Zeus Server again." with title "OpenHPSDR Zeus Server" with icon caution buttons {"OK"} default button "OK"' >/dev/null 2>&1
    exit 1
fi
APP_DIR="${ZEUS_APP}/Contents/Resources/app"
cd "${APP_DIR}"
export DYLD_LIBRARY_PATH="${APP_DIR}/runtimes/osx-arm64/native:${APP_DIR}/runtimes/osx-x64/native:${DYLD_LIBRARY_PATH}"
export ZEUS_WEBROOT="${APP_DIR}/wwwroot"
export ZEUS_BANDPLANS_DIR="${APP_DIR}/BandPlans"
exec ./OpenhpsdrZeus --server
EOF
chmod +x "${SERVER_APP_BUNDLE}/Contents/MacOS/launch.sh"

echo "Server wrapper bundle created at ${SERVER_APP_BUNDLE}"

# --- Codesigning (opt-in via env) ---------------------------------------
#
# Hardened-runtime, Developer-ID signing for notarization. Triggered when
# CODESIGN_IDENTITY is set in the environment. CI populates it from the
# MACOS_SIGNING_IDENTITY secret; a developer can export it locally for
# off-the-cuff signed builds.
#
#   CODESIGN_IDENTITY   "Developer ID Application: Name (TEAMID)"
#                       (empty/unset → unsigned ad-hoc build, original
#                       behaviour, user clears xattr on first launch.)
#   KEYCHAIN_PATH       Optional path to a temp keychain (CI uses a
#                       per-run keychain). Empty → codesign uses the
#                       default keychain search list (dev-local).
#   ENTITLEMENTS_PATH   Path to the hardened-runtime entitlements plist.
#                       Defaults to installers/zeus-macos.entitlements.
#
# Inside-out signing (no --deep):
#
# Apple deprecated --deep and Apple's own guidance is to sign nested code
# from the inside out. The codesign-friendly relayout above already removed
# the subdirectories that used to force --deep (wwwroot/, BandPlans/,
# runtimes/), so Contents/MacOS/ now holds only flat Mach-O (the apphost,
# createdump, and the flattened .dylibs) plus flat resource files (~300
# managed .dll, .pdb, .json, .png, zetaHat.bin, calculus). codesign seals
# the flat resources automatically; we only have to sign the Mach-O, then
# the bundle, in dependency order:
#   1. every nested .dylib + createdump  → hardened runtime + timestamp
#   2. the apphost (OpenhpsdrZeus)        → + entitlements (the process that
#                                            hosts the mic-using webview)
#   3. the bundle itself                  → + entitlements, seals resources
# Signing inner-first matters: re-signing the outer bundle after a nested
# Mach-O changes would invalidate the outer seal, so the leaves go first.
#
# --options runtime  enables hardened runtime (required for notarization).
# --timestamp        embeds a trusted Apple timestamp so the signature
#                    stays valid after the cert expires AND because
#                    notarization rejects untimestamped signatures.
if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    set -e
    if [ -z "${ENTITLEMENTS_PATH:-}" ]; then
        ENTITLEMENTS_PATH="${SCRIPT_DIR}/zeus-macos.entitlements"
    fi
    if [ ! -f "${ENTITLEMENTS_PATH}" ]; then
        echo "ERROR: ENTITLEMENTS_PATH '${ENTITLEMENTS_PATH}' not found" >&2
        exit 1
    fi
    echo "Codesigning with: ${CODESIGN_IDENTITY}"
    echo "Entitlements:     ${ENTITLEMENTS_PATH}"
    if [ -n "${KEYCHAIN_PATH:-}" ]; then
        echo "Keychain:         ${KEYCHAIN_PATH}"
    fi

    # Pass --keychain only when KEYCHAIN_PATH is set; codesign default
    # search list otherwise. Done via an array so we can splat or skip.
    KEYCHAIN_FLAG=()
    if [ -n "${KEYCHAIN_PATH:-}" ]; then
        KEYCHAIN_FLAG=(--keychain "${KEYCHAIN_PATH}")
    fi

    sign_one() {
        # Sign a single Mach-O (or the bundle). Extra flags (e.g.
        # --entitlements) are passed after the target via "$@".
        local target="$1"; shift
        codesign --force --options runtime --timestamp \
            "${KEYCHAIN_FLAG[@]}" \
            --sign "${CODESIGN_IDENTITY}" \
            "$@" \
            "${target}"
    }

    sign_bundle() {
        local bundle="$1"
        echo "  → ${bundle}"

        # 1. Nested Mach-O leaves first — the payload lives under
        #    Contents/Resources/app/ (dylibs in runtimes/<rid>/native/ plus
        #    createdump). -print0/read -d handles paths with spaces. The
        #    wrapper Server.app has no payload, so the find may match nothing.
        while IFS= read -r -d '' macho; do
            echo "      sign $(basename "${macho}")"
            sign_one "${macho}"
        done < <(find "${bundle}/Contents/Resources/app" \
                    \( -name "*.dylib" -o -name "createdump" \) \
                    -type f -print0 2>/dev/null)

        # 2. The apphost carries the entitlements (hardened runtime + mic).
        #    The wrapper Server.app has no apphost of its own — it exec's the
        #    main app's binary — so only sign it when present.
        if [ -f "${bundle}/Contents/Resources/app/OpenhpsdrZeus" ]; then
            echo "      sign OpenhpsdrZeus (entitlements)"
            sign_one "${bundle}/Contents/Resources/app/OpenhpsdrZeus" \
                --entitlements "${ENTITLEMENTS_PATH}"
        fi

        # 3. The bundle last, sealing the resources. --deep --strict on
        #    *verify* (not sign) walks every nested Mach-O to confirm each got
        #    hardened runtime + a valid signature.
        echo "      sign bundle"
        sign_one "${bundle}" --entitlements "${ENTITLEMENTS_PATH}"
        codesign --verify --deep --strict --verbose=2 "${bundle}"
    }

    sign_bundle "${APP_BUNDLE}"
    sign_bundle "${SERVER_APP_BUNDLE}"
    WILL_SIGN=1
else
    echo "(unsigned — set CODESIGN_IDENTITY to enable codesigning)"
    WILL_SIGN=0
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

# README inside the DMG. Two flavours, picked from WILL_SIGN above:
#   - signed/notarized   → drag-to-Applications + first-run notes only.
#                          No xattr; Gatekeeper accepts the .app directly.
#   - unsigned (dryrun)  → original README with the xattr -cr workaround,
#                          which is the only way to launch an unsigned
#                          .app on modern macOS without right-click +
#                          Open Anyway acrobatics.
if [ "${WILL_SIGN}" -eq 1 ]; then
    cat > "${DMG_TEMP}/README.txt" << 'EOF'
OpenHPSDR Zeus for macOS
========================

INSTALL
  Drag BOTH "OpenHPSDR Zeus.app" and "OpenHPSDR Zeus Server.app" onto
  the Applications shortcut in this window. "OpenHPSDR Zeus Server.app"
  is a small wrapper around "OpenHPSDR Zeus.app" and won't work without
  "OpenHPSDR Zeus.app" installed.

  If you only want the desktop app, dragging "OpenHPSDR Zeus.app" alone
  is fine.

  Then launch from Applications normally — Zeus is signed and notarized,
  so Gatekeeper lets it open without any xattr workaround.

THE TWO ICONS

  OpenHPSDR Zeus         Full native window. The radio backend runs
                         in-process inside the same window. Closing the
                         window stops Zeus completely. This is the right
                         choice for most operators.

  OpenHPSDR Zeus Server  Backend-only mode for LAN / remote / phone
                         access. Opens a small status window showing
                         the URLs to connect to from a browser, with a
                         Stop Zeus button. Connect from this Mac at
                         http://localhost:6060 or from another device
                         at http://<your-mac>:6060. HTTPS uses a
                         self-signed certificate — accept the browser
                         warning on first connect.

HEADLESS / CLI USE
  If you're running Zeus on a headless Mac (no display, e.g. a mac mini
  in a closet), use Terminal:

      "/Applications/OpenHPSDR Zeus.app/Contents/Resources/app/OpenhpsdrZeus"

  This is the no-window service mode. Identical to OpenHPSDR Zeus
  Server's backend but without the status window. Closing the Terminal
  (or Ctrl-C) stops the server.

FIRST RUN — WDSP WISDOM
  The first launch builds an FFTW "wisdom" cache and can take 1-3
  minutes. The window will load, but do NOT click Discover/Connect
  until the wisdom build settles. Subsequent launches are instant.

More info: https://github.com/Kb2uka/openhpsdr-zeus
EOF
else
    cat > "${DMG_TEMP}/README.txt" << 'EOF'
OpenHPSDR Zeus for macOS  (UNSIGNED DEV BUILD)
==============================================

INSTALL
  Drag BOTH "OpenHPSDR Zeus.app" and "OpenHPSDR Zeus Server.app" onto
  the Applications shortcut in this window. "OpenHPSDR Zeus Server.app"
  is a small wrapper around "OpenHPSDR Zeus.app" and won't work without
  "OpenHPSDR Zeus.app" installed.

  If you only want the desktop app, dragging "OpenHPSDR Zeus.app" alone
  is fine.

FIRST LAUNCH (important — this is an unsigned dev build)
  This DMG is a dry-run / preview build and is NOT signed by an Apple
  Developer ID. macOS Gatekeeper will block it on first launch. To clear
  the quarantine flag, open Terminal and run:

      xattr -cr "/Applications/OpenHPSDR Zeus.app"
      xattr -cr "/Applications/OpenHPSDR Zeus Server.app"

  Then launch from Applications.

  If you still see a security warning, go to:
      System Settings -> Privacy & Security
  and click "Open Anyway".

  Tagged release and nightly builds are signed + notarized and do NOT
  need this step.

THE TWO ICONS

  OpenHPSDR Zeus         Full native window. The radio backend runs
                         in-process inside the same window. Closing the
                         window stops Zeus completely. This is the right
                         choice for most operators.

  OpenHPSDR Zeus Server  Backend-only mode for LAN / remote / phone
                         access. Opens a small status window showing
                         the URLs to connect to from a browser, with a
                         Stop Zeus button. Connect from this Mac at
                         http://localhost:6060 or from another device
                         at http://<your-mac>:6060. HTTPS uses a
                         self-signed certificate — accept the browser
                         warning on first connect.

HEADLESS / CLI USE
  If you're running Zeus on a headless Mac (no display, e.g. a mac mini
  in a closet), use Terminal:

      "/Applications/OpenHPSDR Zeus.app/Contents/Resources/app/OpenhpsdrZeus"

  This is the no-window service mode. Identical to OpenHPSDR Zeus
  Server's backend but without the status window. Closing the Terminal
  (or Ctrl-C) stops the server.

FIRST RUN — WDSP WISDOM
  The first launch builds an FFTW "wisdom" cache and can take 1-3
  minutes. The window will load, but do NOT click Discover/Connect
  until the wisdom build settles. Subsequent launches are instant.

More info: https://github.com/Kb2uka/openhpsdr-zeus
EOF
fi

hdiutil create -volname "Openhpsdr Zeus v${VERSION}" \
    -srcfolder "${DMG_TEMP}" \
    -ov -format UDZO \
    "${DMG_PATH}"

rm -rf "${DMG_TEMP}"

# Sign the DMG itself when WILL_SIGN=1. The DMG is a separate signed
# container from the .app bundles inside it — Gatekeeper's "open" assess
# context (mounting a DMG) reads the DMG signature, not the nested app
# signatures. notarize+staple in the CI workflow operate on this signed
# DMG. DMGs don't take entitlements or --options runtime (those are
# Mach-O concepts), just --sign + --timestamp.
if [ "${WILL_SIGN}" -eq 1 ]; then
    KEYCHAIN_FLAG=()
    if [ -n "${KEYCHAIN_PATH:-}" ]; then
        KEYCHAIN_FLAG=(--keychain "${KEYCHAIN_PATH}")
    fi
    echo "Signing DMG..."
    codesign --force --timestamp \
        "${KEYCHAIN_FLAG[@]}" \
        --sign "${CODESIGN_IDENTITY}" \
        "${DMG_PATH}"
    codesign --verify --verbose=2 "${DMG_PATH}"
fi

echo "DMG created at ${DMG_PATH}"
if [ "${WILL_SIGN}" -eq 1 ]; then
    echo "DMG is signed; CI will run notarytool submit + stapler staple as follow-up steps."
else
    echo
    echo "NOTE: this is an unsigned dev build — users must clear the quarantine flag on first launch:"
    echo "  xattr -cr \"/Applications/OpenHPSDR Zeus.app\""
    echo "  xattr -cr \"/Applications/OpenHPSDR Zeus Server.app\""
fi
