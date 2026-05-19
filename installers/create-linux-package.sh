#!/bin/bash
# Build Openhpsdr Zeus tarball for Linux x64.
# Usage: ./create-linux-package.sh <version>
# Example: ./create-linux-package.sh 0.4.1
#
# Tarball contents:
#   OpenhpsdrZeus              — the single binary (serves three modes)
#   openhpsdr-zeus             — service-mode launcher (LAN HTTP + browser
#                                auto-open, no GUI status window)
#   openhpsdr-zeus-desktop     — desktop-mode launcher (Photino window)
#   openhpsdr-zeus-server      — server-mode launcher (Photino status
#                                window with URLs + Stop button, --server)
#   zeus-desktop.desktop       — XDG entry template for the Photino app
#   zeus-server.desktop        — XDG entry template for the server icon
#   wwwroot/, runtimes/, …     — bundled .NET runtime, native libs, SPA
#
# Operators install the .desktop entries to ~/.local/share/applications/
# (or /usr/share/applications/) to get menu entries / desktop icons; see
# README for the one-liner. The AppImage (create-linux-desktop-appimage.sh)
# ships separately as a single-file desktop launcher.

set -e

VERSION="${1:-0.0.0}"

echo "Creating Openhpsdr Zeus tarball for Linux x64 v${VERSION}..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="${REPO_ROOT}/OpenhpsdrZeus/bin/Release/net10.0/linux-x64/publish"
OUTPUT_DIR="${SCRIPT_DIR}/output"
PACKAGE_NAME="openhpsdr-zeus-${VERSION}-linux-x64"
PACKAGE_DIR="${OUTPUT_DIR}/${PACKAGE_NAME}"

# Clean and create output directory
rm -rf "${PACKAGE_DIR}"
mkdir -p "${OUTPUT_DIR}"
mkdir -p "${PACKAGE_DIR}"

# Copy published files
echo "Copying published files..."
cp -r "${PUBLISH_DIR}"/* "${PACKAGE_DIR}/"

# Make the binary executable
chmod +x "${PACKAGE_DIR}/OpenhpsdrZeus"

# Service-mode launcher — LAN-bound HTTP service + auto-open browser.
cat > "${PACKAGE_DIR}/openhpsdr-zeus" << 'EOF'
#!/bin/bash
# Openhpsdr Zeus launcher — service mode.
# Starts OpenhpsdrZeus as a LAN-bound HTTP service on :6060 and opens the
# default browser. For a native window without a browser tab, run
# ./openhpsdr-zeus-desktop instead.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

# Pin the bundled libwdsp.so so an older system copy in /usr/lib or
# /usr/local/lib (e.g. left by a piHPSDR build) cannot shadow it. Linux
# does NOT search the executable's directory by default; without this
# line, dlopen("libwdsp.so") goes straight to LD_LIBRARY_PATH +
# /etc/ld.so.cache and may bind P/Invoke calls against a stale lib that
# pre-dates symbols Zeus relies on (e.g. SetRXAEMNRpost2*).
export LD_LIBRARY_PATH="${SCRIPT_DIR}/runtimes/linux-x64/native:${SCRIPT_DIR}/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"

# Cleanup handler to terminate the server subprocess on script exit.
# Ensures that Ctrl-C, kill, or terminal close properly stops the server
# and prevents orphaned processes.
cleanup() {
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        echo ""
        echo "Stopping Openhpsdr Zeus server..."
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 5 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM

# Check if running in a display environment
if [ -n "$DISPLAY" ] || [ -n "$WAYLAND_DISPLAY" ]; then
    echo "Starting Openhpsdr Zeus server on http://localhost:6060"
    echo "Opening browser in 2 seconds..."
    ./OpenhpsdrZeus &
    SERVER_PID=$!
    sleep 2

    # Try to open the browser
    if command -v xdg-open > /dev/null; then
        xdg-open http://localhost:6060 2>/dev/null &
    elif command -v gnome-open > /dev/null; then
        gnome-open http://localhost:6060 2>/dev/null &
    elif command -v kde-open > /dev/null; then
        kde-open http://localhost:6060 2>/dev/null &
    else
        echo "Could not automatically open browser."
        echo "Please open http://localhost:6060 in your web browser."
    fi

    echo "Openhpsdr Zeus is running. Press Ctrl-C to stop."
    wait $SERVER_PID
else
    # No display, just run the server
    echo "Starting Openhpsdr Zeus server on http://localhost:6060"
    echo "Open this URL in your web browser to access Zeus."
    ./OpenhpsdrZeus &
    SERVER_PID=$!
    echo "Openhpsdr Zeus is running. Press Ctrl-C to stop."
    wait $SERVER_PID
fi
EOF
chmod +x "${PACKAGE_DIR}/openhpsdr-zeus"

# Desktop-mode launcher — Photino native window, in-process backend.
cat > "${PACKAGE_DIR}/openhpsdr-zeus-desktop" << 'EOF'
#!/bin/bash
# Openhpsdr Zeus launcher — desktop mode.
# Opens a native Photino window. The radio backend runs in-process inside
# the same window; closing the window stops Zeus completely. Requires
# libwebkit2gtk-4.1-0 (Photino's WebView backend on Linux).
#
# For a LAN-bound HTTP service (browser UI, remote / phone access), run
# ./openhpsdr-zeus instead.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

# Same DYLD/LD pin as the service-mode launcher — see openhpsdr-zeus.
export LD_LIBRARY_PATH="${SCRIPT_DIR}/runtimes/linux-x64/native:${SCRIPT_DIR}/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"

exec ./OpenhpsdrZeus --desktop "$@"
EOF
chmod +x "${PACKAGE_DIR}/openhpsdr-zeus-desktop"

# Server-mode launcher — Photino status window with URLs + Stop button.
# Same as the no-flag service mode (LAN bind + HTTPS) but with a small
# GUI so a desktop user has a place to read the LAN URL and a stop
# button. Headless deploys keep using the no-flag service launcher.
cat > "${PACKAGE_DIR}/openhpsdr-zeus-server" << 'EOF'
#!/bin/bash
# Openhpsdr Zeus launcher — server mode (LAN bind + Photino status window).
# Opens a small native window listing the URLs to connect to and a Stop
# Zeus button. The full backend (HTTP + HTTPS LAN bind) runs alongside.
# Requires libwebkit2gtk-4.1-0 (the same Photino dep as desktop mode).
#
# For a fully headless service (systemd, Docker, no GUI), invoke
# ./OpenhpsdrZeus directly with no flag — no Photino, no window.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

export LD_LIBRARY_PATH="${SCRIPT_DIR}/runtimes/linux-x64/native:${SCRIPT_DIR}/runtimes/linux-arm64/native:${LD_LIBRARY_PATH}"

exec ./OpenhpsdrZeus --server "$@"
EOF
chmod +x "${PACKAGE_DIR}/openhpsdr-zeus-server"

# XDG .desktop entry templates. The Exec= path is rewritten by the
# install-icons.sh helper below to the operator's actual install dir, so
# we ship a templated form with a __ZEUS_DIR__ placeholder.
cat > "${PACKAGE_DIR}/zeus-desktop.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=Zeus
Comment=OpenHPSDR Protocol 1 / Protocol 2 SDR client (native window)
Exec=__ZEUS_DIR__/openhpsdr-zeus-desktop
Icon=__ZEUS_DIR__/zeus.png
Terminal=false
Categories=HamRadio;AudioVideo;Audio;
StartupWMClass=Zeus
EOF

cat > "${PACKAGE_DIR}/zeus-server.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=Zeus Server
Comment=OpenHPSDR Zeus backend with status window and Stop button
Exec=__ZEUS_DIR__/openhpsdr-zeus-server
Icon=__ZEUS_DIR__/zeus.png
Terminal=false
Categories=HamRadio;AudioVideo;Audio;Network;
StartupWMClass=Zeus Server
EOF

# install-icons.sh — one-shot helper that materialises both .desktop
# entries into ~/.local/share/applications/ with the right Exec= path.
# Runs once per machine; operators who don't care about menu entries
# just ignore this and invoke the launchers from the terminal.
cat > "${PACKAGE_DIR}/install-icons.sh" << 'EOF'
#!/bin/bash
# Install Zeus + Zeus Server menu entries for the current user.
# Idempotent — safe to re-run after every upgrade.
set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPS_DIR="${HOME}/.local/share/applications"
mkdir -p "${APPS_DIR}"
for f in zeus-desktop.desktop zeus-server.desktop; do
    sed "s|__ZEUS_DIR__|${SCRIPT_DIR}|g" "${SCRIPT_DIR}/${f}" > "${APPS_DIR}/${f}"
    chmod +x "${APPS_DIR}/${f}"
    echo "installed ${APPS_DIR}/${f}"
done
# Refresh the desktop database so the new entries show up immediately.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "${APPS_DIR}" || true
fi
echo ""
echo "Zeus and Zeus Server are now in your application menu."
echo "To remove them later: rm ${APPS_DIR}/zeus-desktop.desktop ${APPS_DIR}/zeus-server.desktop"
EOF
chmod +x "${PACKAGE_DIR}/install-icons.sh"

# Ship the icon next to the launchers so the .desktop entries can resolve
# Icon= relative to the install dir.
if [ -f "${REPO_ROOT}/docs/pics/zeus.png" ]; then
    cp "${REPO_ROOT}/docs/pics/zeus.png" "${PACKAGE_DIR}/zeus.png"
fi

# Create README
cat > "${PACKAGE_DIR}/README.txt" << EOF
Openhpsdr Zeus v${VERSION} for Linux

Installation:
1. Extract this archive to a location of your choice (e.g., ~/zeus or /opt/zeus)
2. (Optional) Add Zeus and Zeus Server to your application menu:
     ./install-icons.sh
   This drops two .desktop entries into ~/.local/share/applications/.
3. Run ONE of:
     ./openhpsdr-zeus           — service mode (LAN HTTP on :6060, auto-opens
                                  browser; no GUI window — close terminal to stop)
     ./openhpsdr-zeus-desktop   — desktop mode (native Photino window, no browser)
                                  Requires libwebkit2gtk-4.1-0.
     ./openhpsdr-zeus-server    — server mode (LAN bind + small Photino status
                                  window with the connect URLs and a Stop Zeus
                                  button). Requires libwebkit2gtk-4.1-0.
     ./OpenhpsdrZeus            — raw binary, headless service mode (no Photino)
     ./OpenhpsdrZeus --desktop  — raw binary, desktop mode
     ./OpenhpsdrZeus --server   — raw binary, server mode (with status window)

Service mode (no flag) is the right choice for:
- Headless servers (Pi, NUC) — no GUI deps required
- systemd / Docker units
- Multi-machine setups where you connect via http://<host>:6060 from elsewhere

Server mode (--server) is the right choice for:
- A desktop Linux user who wants the LAN URL on screen + an obvious Stop button
- Same backend as service mode, plus a small window

Desktop mode (--desktop) is the right choice for:
- Single-machine local use, "just one Zeus window" workflows

Requirements:
- Linux x64 system (glibc-based; no system packages required — FFTW3 is
  statically linked into libwdsp.so and the .NET runtime is bundled)
- Desktop mode additionally requires libwebkit2gtk-4.1-0:
    Debian/Ubuntu:  sudo apt install libwebkit2gtk-4.1-0
    Fedora:         sudo dnf install webkit2gtk4.1
    Arch:           sudo pacman -S webkit2gtk-4.1

For more information:
https://github.com/brianbruff/openhpsdr-zeus

License: GNU GPL v2 or later
Copyright (C) 2025-2026 Brian Keating (EI6LF), Douglas J. Cerrato (KB2UKA), and contributors
EOF

# Copy LICENSE
cp "${REPO_ROOT}/LICENSE" "${PACKAGE_DIR}/" 2>/dev/null || echo "LICENSE file not found, skipping"

# Create tarball
TARBALL_NAME="${PACKAGE_NAME}.tar.gz"
TARBALL_PATH="${OUTPUT_DIR}/${TARBALL_NAME}"
echo "Creating tarball..."
cd "${OUTPUT_DIR}"
tar -czf "${TARBALL_NAME}" "${PACKAGE_NAME}"
cd "${SCRIPT_DIR}"

echo "Package created at ${TARBALL_PATH}"
echo ""
echo "To install:"
echo "  tar -xzf ${TARBALL_NAME}"
echo "  cd ${PACKAGE_NAME}"
echo "  ./openhpsdr-zeus           # service mode (browser UI)"
echo "  ./openhpsdr-zeus-desktop   # desktop mode (Photino window)"
