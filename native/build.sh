#!/usr/bin/env bash
# native/build.sh — build libwdsp + libminiaudio + the VST3 host bridge and
# stage them for .NET.
#
# Usage:
#   ./native/build.sh                 # Release, all targets, auto-detect RID
#   ./native/build.sh Debug           # pass build type as arg
#   ./native/build.sh Release wdsp    # build only WDSP
#   ./native/build.sh Release miniaudio # build only miniaudio
#   ./native/build.sh Release vstbridge # build only the VST3 host bridge
#
# Run from the repo root. libwdsp / libminiaudio stage to
# Zeus.Dsp/runtimes/<rid>/native/; the VST3 bridge stages to
# Zeus.Plugins.Host/runtimes/<rid>/native/. NativeLibrary.Load picks each up
# from its RID path automatically (no extra runtime config).

set -euo pipefail

BUILD_TYPE="${1:-Release}"
WHICH="${2:-all}"   # all | wdsp | miniaudio
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Detect platform + arch -> .NET RID + shared-lib filenames.
case "$(uname -s)" in
    Darwin)
        case "$(uname -m)" in
            arm64)  RID="osx-arm64" ;;
            x86_64) RID="osx-x64"   ;;
            *) echo "Unsupported macOS arch: $(uname -m)" >&2; exit 1 ;;
        esac
        WDSP_LIB="libwdsp.dylib"
        MA_LIB="libminiaudio.dylib"
        VST_LIB="libzeus-vst-bridge.dylib"
        ;;
    Linux)
        case "$(uname -m)" in
            aarch64|arm64) RID="linux-arm64" ;;
            x86_64)        RID="linux-x64"   ;;
            *) echo "Unsupported Linux arch: $(uname -m)" >&2; exit 1 ;;
        esac
        WDSP_LIB="libwdsp.so"
        MA_LIB="libminiaudio.so"
        VST_LIB="libzeus-vst-bridge.so"
        ;;
    *)
        echo "Unsupported host OS: $(uname -s). Use cmake directly on Windows." >&2
        exit 1
        ;;
esac

DEST_DIR="${REPO_ROOT}/Zeus.Dsp/runtimes/${RID}/native"
mkdir -p "${DEST_DIR}"

# The VST3 host bridge stages next to the .NET plugin host (its loader,
# VstBridgeNativeLoader, probes Zeus.Plugins.Host/runtimes/<rid>/native first).
VST_DEST_DIR="${REPO_ROOT}/Zeus.Plugins.Host/runtimes/${RID}/native"

build_wdsp() {
    local src="${SCRIPT_DIR}/wdsp"
    local build="${SCRIPT_DIR}/build"
    echo "==> [wdsp] Configuring (${BUILD_TYPE}, ${RID})"
    cmake -S "${src}" -B "${build}" -DCMAKE_BUILD_TYPE="${BUILD_TYPE}"
    echo "==> [wdsp] Building"
    cmake --build "${build}" --config "${BUILD_TYPE}" --parallel
    echo "==> [wdsp] Staging ${WDSP_LIB} -> ${DEST_DIR}"
    cp "${build}/${WDSP_LIB}" "${DEST_DIR}/${WDSP_LIB}"
    ls -lh "${DEST_DIR}/${WDSP_LIB}"
}

build_miniaudio() {
    local src="${SCRIPT_DIR}/miniaudio"
    local build="${SCRIPT_DIR}/build-miniaudio"
    echo "==> [miniaudio] Configuring (${BUILD_TYPE}, ${RID})"
    cmake -S "${src}" -B "${build}" -DCMAKE_BUILD_TYPE="${BUILD_TYPE}"
    echo "==> [miniaudio] Building"
    cmake --build "${build}" --config "${BUILD_TYPE}" --parallel
    echo "==> [miniaudio] Staging ${MA_LIB} -> ${DEST_DIR}"
    cp "${build}/${MA_LIB}" "${DEST_DIR}/${MA_LIB}"
    ls -lh "${DEST_DIR}/${MA_LIB}"
}

build_vstbridge() {
    local src="${SCRIPT_DIR}/zeus-vst-bridge"
    local build="${src}/build"
    if [ ! -f "${src}/third_party/vst3sdk/pluginterfaces/vst/ivstcomponent.h" ]; then
        echo "==> [vst-bridge] NOTE: vst3sdk submodule not initialised — CMake will"
        echo "    build a STUB (loads, but hosts no real VST3). For real hosting run:"
        echo "      git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk"
        echo "      git -C native/zeus-vst-bridge/third_party/vst3sdk \\"
        echo "          submodule update --init base pluginterfaces public.sdk cmake"
    fi
    echo "==> [vst-bridge] Configuring (${BUILD_TYPE}, ${RID})"
    cmake -S "${src}" -B "${build}" -DCMAKE_BUILD_TYPE="${BUILD_TYPE}"
    echo "==> [vst-bridge] Building"
    cmake --build "${build}" --config "${BUILD_TYPE}" --parallel
    echo "==> [vst-bridge] Staging ${VST_LIB} -> ${VST_DEST_DIR}"
    mkdir -p "${VST_DEST_DIR}"
    cp "${build}/${VST_LIB}" "${VST_DEST_DIR}/${VST_LIB}"
    ls -lh "${VST_DEST_DIR}/${VST_LIB}"
}

case "${WHICH}" in
    all)
        build_wdsp
        build_miniaudio
        build_vstbridge
        ;;
    wdsp)
        build_wdsp
        ;;
    miniaudio|ma)
        build_miniaudio
        ;;
    vstbridge|vst)
        build_vstbridge
        ;;
    *)
        echo "Unknown target '${WHICH}'. Use: all | wdsp | miniaudio | vstbridge" >&2
        exit 1
        ;;
esac

echo "==> Done."
