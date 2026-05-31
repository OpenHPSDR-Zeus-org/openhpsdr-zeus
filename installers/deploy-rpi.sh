#!/usr/bin/env bash
# deploy-rpi.sh — build Zeus for Raspberry Pi 4 (linux-arm64) and deploy to the Pi.
#
# Usage:
#   ./installers/deploy-rpi.sh <pi-host>             # e.g. emu-agon.local or 192.168.86.24
#   ./installers/deploy-rpi.sh <pi-host> --build-only  # publish locally, skip rsync
#   ./installers/deploy-rpi.sh <pi-host> --deploy-only # skip publish, rsync existing bundle
#
# Requirements:
#   Local: .NET 10 SDK, npm
#   Pi:    Debian/Raspberry Pi OS 64-bit (arm64), libfftw3-double3
#          (installed automatically by this script on first run)
#
# The produced bundle is self-contained — .NET runtime is included, no dotnet
# install needed on the Pi. Zeus runs headless (web mode); open a browser at
# http://<pi-ip>:6060 to connect.
#
# Discovery gotcha: if the Pi has both WiFi and Ethernet active, HPSDR
# UDP broadcast discovery goes out the default-route interface (usually WiFi).
# The HL2 won't be found automatically. Fix:
#   ssh rampa@<pi> "sudo ip link set wlan0 down"
# Connect manually instead, or keep WiFi disabled while using Ethernet.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${REPO_ROOT}/publish-rpi"
PI_HOST="${1:-}"
MODE="full"

if [ -z "${PI_HOST}" ]; then
    echo "Usage: $0 <pi-host> [--build-only|--deploy-only]"
    exit 1
fi

for arg in "${@:2}"; do
    case "${arg}" in
        --build-only)  MODE=build  ;;
        --deploy-only) MODE=deploy ;;
    esac
done

# ── 1. Build frontend + dotnet publish ───────────────────────────────────────
if [ "${MODE}" != "deploy" ]; then
    echo "==> Building frontend..."
    npm --prefix "${REPO_ROOT}/zeus-web" run build

    echo "==> Publishing OpenhpsdrZeus for linux-arm64 (self-contained)..."
    rm -rf "${PUBLISH_DIR}"
    dotnet publish "${REPO_ROOT}/OpenhpsdrZeus" \
        -c Release \
        -r linux-arm64 \
        --self-contained \
        -o "${PUBLISH_DIR}"
    echo "==> Bundle ready at ${PUBLISH_DIR} ($(du -sh "${PUBLISH_DIR}" | cut -f1))"
fi

if [ "${MODE}" = "build" ]; then
    echo "Build-only mode — skipping deploy."
    echo "Copy manually: rsync -az ${PUBLISH_DIR}/ rampa@<pi>:~/zeus-rpi/"
    exit 0
fi

# ── 2. Ensure libfftw3-double3 on the Pi ─────────────────────────────────────
echo "==> Checking Pi dependencies..."
ssh "rampa@${PI_HOST}" \
    "dpkg -s libfftw3-double3 &>/dev/null || sudo apt-get install -y libfftw3-double3" \
    2>/dev/null && echo "    libfftw3-double3 OK"

# ── 3. Rsync bundle to Pi ────────────────────────────────────────────────────
echo "==> Deploying to rampa@${PI_HOST}:~/zeus-rpi/ ..."
rsync -az --delete --progress \
    "${PUBLISH_DIR}/" \
    "rampa@${PI_HOST}:~/zeus-rpi/"

# ── 4. Make executable ───────────────────────────────────────────────────────
ssh "rampa@${PI_HOST}" "chmod +x ~/zeus-rpi/OpenhpsdrZeus"

# ── Done ─────────────────────────────────────────────────────────────────────
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Deployed to ${PI_HOST}"
echo ""
echo "  To start Zeus on the Pi:"
echo "    ssh rampa@${PI_HOST}"
echo "    ZEUS_PORT=6060 ~/zeus-rpi/OpenhpsdrZeus"
echo ""
echo "  Then open: http://${PI_HOST}:6060"
echo ""
echo "  If discovery doesn't find the HL2 (WiFi+Ethernet active):"
echo "    sudo ip link set wlan0 down"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
