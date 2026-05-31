# Deploying Zeus on a Raspberry Pi 4

Zeus runs on a Raspberry Pi 4 (arm64) either as a headless radio server
(browser UI over LAN) or as a full desktop app via the AppImage.

Verified on: **Raspberry Pi 4 (4 GB), Debian 13 (trixie) arm64**, kernel
6.12, Ethernet, HL2 connected on the same LAN segment.

---

## One-command deploy (from your Mac/Linux dev machine)

```bash
# Full build + deploy to the Pi
bash installers/deploy-rpi.sh emu-agon.local

# Build locally only (produces publish-rpi/ in the repo root)
bash installers/deploy-rpi.sh emu-agon.local --build-only

# Re-deploy an existing bundle (skip publish step)
bash installers/deploy-rpi.sh emu-agon.local --deploy-only
```

The script:
1. Builds the frontend into `wwwroot` (`npm run build`)
2. Runs `dotnet publish -r linux-arm64 --self-contained`
3. Checks / installs `libfftw3-double3` on the Pi via SSH
4. `rsync`s the bundle to `~/zeus-rpi/` on the Pi

The bundle is **self-contained** — no `.NET` install needed on the Pi.

---

## Starting Zeus on the Pi

```bash
ssh rampa@emu-agon.local
ZEUS_PORT=6060 ~/zeus-rpi/OpenhpsdrZeus
```

Then open `http://<pi-ip>:6060` in any browser on the LAN.

First launch regenerates the WDSP FFTW wisdom cache (`~/.local/share/Zeus/`).
This takes a few minutes; subsequent starts are instant.

---

## Pi OS requirements

- **64-bit OS mandatory** — `linux-arm64` does not run on 32-bit Raspberry
  Pi OS. Use Raspberry Pi OS 64-bit or Debian arm64 (trixie or later).
- **`libfftw3-double3`** — the only runtime dependency not bundled.
  On Debian 13 it is usually pre-installed; the deploy script installs it
  if missing.

```bash
sudo apt-get install -y libfftw3-double3
```

---

## HL2 discovery with multiple network interfaces (load-bearing)

HPSDR Protocol 1 discovery sends a **UDP broadcast** to find the HL2.
When the Pi has **both WiFi and Ethernet active**, the broadcast goes out
whichever interface owns the default route — usually WiFi — and never
reaches the HL2 if it's on the Ethernet segment.

**Symptom:** the radio does not appear in Zeus's discovery list. Connecting
manually (entering the HL2's IP directly) works because unicast routes
correctly regardless of interface.

**Fix — disable WiFi while using Ethernet:**

```bash
sudo ip link set wlan0 down    # discovery now goes out eth0
```

To restore:

```bash
sudo ip link set wlan0 up
```

To make it permanent across reboots, add to `/etc/network/interfaces` or
disable WiFi via `raspi-config` → System Options → Wireless LAN → disable.

---

## Desktop (Photino) mode via AppImage

The arm64 AppImage (`OpenhpsdrZeus-<ver>-linux-aarch64.AppImage`) runs
the full Photino native window on the Pi. Verified working on Debian 13
arm64 with a local display and via SSH X forwarding to macOS XQuartz.

**Requirements:**
```bash
sudo apt-get install -y libwebkit2gtk-4.1-0
```

**Local display (monitor connected to Pi):**
```bash
./OpenhpsdrZeus-<ver>-linux-aarch64.AppImage --appimage-extract-and-run
```

**SSH X forwarding (window appears on your Mac/Linux machine):**
```bash
# On your local machine:
ssh -X rampa@<pi-ip>
# Then on the Pi:
./OpenhpsdrZeus-<ver>-linux-aarch64.AppImage --appimage-extract-and-run
```

If the window doesn't open over X forwarding, WebKitGTK GPU acceleration
may be failing. Add these env vars to force software rendering:
```bash
WEBKIT_DISABLE_DMABUF_RENDERER=1 \
WEBKIT_DISABLE_COMPOSITING_MODE=1 \
LIBGL_ALWAYS_SOFTWARE=1 \
GDK_BACKEND=x11 \
./OpenhpsdrZeus-<ver>-linux-aarch64.AppImage --appimage-extract-and-run
```

**Default audio device** — miniaudio picks the ALSA default (usually the
first card, often HDMI). To point it at a USB audio device (card 2):
```bash
cat > ~/.asoundrc << 'EOF'
pcm.!default { type hw; card 2 }
ctl.!default { type hw; card 2 }
EOF
```
Check card numbers with `aplay -l`.

---

## Performance notes (RPi 4 4 GB)

| Scenario | CPU (observed) |
|---|---|
| Idle, no radio connected | < 5 % |
| RX 48 kHz, 1 DDC, WDSP | ~15–25 % |
| RX 192 kHz | ~30–40 % (estimate) |

NR4 (libspecbleach / SBNR) is included in the prebuilt `libwdsp.so`.
PureSignal at 192 kHz is untested on RPi 4 — likely marginal.
