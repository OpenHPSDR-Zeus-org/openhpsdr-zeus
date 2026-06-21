#!/bin/bash
# zeus-preflight.sh — Linux runtime-dependency check for the GUI (Photino) modes.
#
# Sourced by the desktop / server launchers (tarball) and by the AppImage AppRun.
# Photino's webview backend on Linux is WebKitGTK (libwebkit2gtk). Without it the
# native window cannot be created and Zeus exits immediately with no window — the
# same "flashes and closes, nothing happens" failure that a missing WebView2
# runtime causes on Windows. This is the Linux analogue of the Windows
# installer's WebView2/VC-redist checks: detect the dependency, offer to install
# it when we have a terminal, and otherwise guide the operator and fall back to
# browser (service) mode so Zeus is never left silently dead.
#
# All functions are best-effort and must never abort the launcher with set -e
# semantics — callers decide what to do with the return value.

# True (0) when libwebkit2gtk (4.1 or 4.0) is resolvable by the dynamic linker.
zeus_have_webkit() {
    if command -v ldconfig >/dev/null 2>&1; then
        ldconfig -p 2>/dev/null | grep -Eq 'libwebkit2gtk-4\.[01]' && return 0
    fi
    local d
    for d in /usr/lib /usr/lib64 /usr/local/lib \
             /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu; do
        ls "${d}"/libwebkit2gtk-4.* >/dev/null 2>&1 && return 0
    done
    return 1
}

# Echo the distro-appropriate install command for WebKitGTK, or empty when the
# package manager isn't recognised. Package names track the per-distro names
# documented in the tarball/AppImage READMEs.
zeus_webkit_install_cmd() {
    if command -v apt-get >/dev/null 2>&1; then
        echo "sudo apt-get install -y libwebkit2gtk-4.1-0"
    elif command -v dnf >/dev/null 2>&1; then
        echo "sudo dnf install -y webkit2gtk4.1"
    elif command -v pacman >/dev/null 2>&1; then
        echo "sudo pacman -S --needed --noconfirm webkit2gtk-4.1"
    elif command -v zypper >/dev/null 2>&1; then
        echo "sudo zypper install -y libwebkit2gtk-4_1-0"
    else
        echo ""
    fi
}

# Best-effort desktop notification for the no-terminal (double-click) case.
zeus_notify() {
    local text="$1"
    if command -v zenity >/dev/null 2>&1; then
        zenity --warning --no-wrap --title="OpenHPSDR Zeus" --text="${text}" >/dev/null 2>&1 || true
    elif command -v kdialog >/dev/null 2>&1; then
        kdialog --title "OpenHPSDR Zeus" --sorry "${text}" >/dev/null 2>&1 || true
    elif command -v notify-send >/dev/null 2>&1; then
        notify-send "OpenHPSDR Zeus" "${text}" || true
    fi
}

# Ensure WebKitGTK is present for GUI (Photino) modes.
#   return 0 → proceed with the requested GUI mode
#   return 1 → caller should fall back to browser/service mode
# When run from a terminal, offers to install the dependency (real fulfilment);
# from a GUI launch it pops a dialog with the exact command. Either way it never
# leaves the operator with a silent dead launch.
zeus_ensure_webkit() {
    zeus_have_webkit && return 0

    local cmd msg ans
    cmd="$(zeus_webkit_install_cmd)"
    msg="OpenHPSDR Zeus needs the WebKitGTK library (libwebkit2gtk) for its native window, but it is not installed."

    if [ -t 0 ] && [ -t 1 ]; then
        echo "${msg}" >&2
        if [ -n "${cmd}" ]; then
            echo "" >&2
            echo "  Install command: ${cmd}" >&2
            printf 'Install it now? [Y/n] ' >&2
            read -r ans
            case "${ans}" in
                [Nn]*) ;;
                *) eval "${cmd}" || echo "Install failed — run the command above manually." >&2 ;;
            esac
        else
            echo "Could not detect your package manager — install libwebkit2gtk-4.1 with your distro's tools." >&2
        fi
        zeus_have_webkit && return 0
        echo "WebKitGTK still missing — falling back to browser (service) mode." >&2
        return 1
    fi

    # No controlling terminal (GUI double-click): we can't prompt or sudo, so
    # show the install command and fall back to the browser UI.
    local detail="${msg}"
    if [ -n "${cmd}" ]; then
        detail="${msg}

Install it from a terminal with:
    ${cmd}

Zeus will open in your web browser for now."
    fi
    zeus_notify "${detail}"
    return 1
}

# Browser/service-mode fallback: run the headless backend and open the default
# browser at the local URL, so a missing GUI dependency still yields a working
# Zeus. Expects the current directory to contain ./OpenhpsdrZeus (the launchers
# cd there before sourcing this file). Passes through any extra args.
zeus_run_service_with_browser() {
    echo "Starting OpenHPSDR Zeus in browser (service) mode on http://localhost:6060" >&2
    ./OpenhpsdrZeus "$@" &
    local pid=$!
    trap 'kill -TERM "${pid}" 2>/dev/null || true' EXIT INT TERM
    sleep 2
    local opener
    for opener in xdg-open gnome-open kde-open; do
        if command -v "${opener}" >/dev/null 2>&1; then
            "${opener}" http://localhost:6060 >/dev/null 2>&1 &
            break
        fi
    done
    zeus_notify "OpenHPSDR Zeus is running in your web browser at http://localhost:6060"
    wait "${pid}"
}
