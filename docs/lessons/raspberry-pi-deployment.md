# Deploying Zeus headless on a Raspberry Pi 4

Zeus runs on a Raspberry Pi 4 (arm64) as a headless radio server — .NET
backend on one end, HL2 on the other, browser UI served over the LAN.
The `libwdsp.so` ARM64 binary already ships in the repo
(`Zeus.Dsp/runtimes/linux-arm64/native/`); no native compilation is needed.

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

## Desktop (Photino) mode

Photino on `linux-arm64` requires `libwebkit2gtk` and a display. On a
headless Pi this is impractical. **Always run without the `--desktop` flag**
(web/service mode). The browser is the UI.

---

## Performance notes (RPi 4 4 GB)

| Scenario | CPU (observed) |
|---|---|
| Idle, no radio connected | < 5 % |
| RX 48 kHz, 1 DDC, WDSP | ~15–25 % |
| RX 192 kHz | ~30–40 % (estimate) |

NR4 (libspecbleach / SBNR) is included in the prebuilt `libwdsp.so`.
PureSignal at 192 kHz is untested on RPi 4 — likely marginal.
