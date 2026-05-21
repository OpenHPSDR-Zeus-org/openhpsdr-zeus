# Square SDR 2 — vendor source mirror

This directory is a verbatim mirror of the Square SDR 2 hardware reference
archive published by the manufacturer on a public Nextcloud share.

## Upstream

- **URL:** https://web.wideservis.cz/cloud/index.php/s/SquareSDR2
- **Snapshot:** 2026-05-21 (Zeus issue #361)
- **Archive root:** `SQUARE_SDR_2/`
- **Snapshot subdirectories:**
  - `Firmware/SQUARE-SDR2_01/` — STM32G031 peripheral-microcontroller firmware
  - `Gateware/SquareSDR2_V75_52/` — Altera Cyclone IV (EP4CE22) HL2-derived gateware

## Authors / Copyright

- **STM32 firmware** (`Firmware/SQUARE-SDR2_01/`) — © 2024 Rene Siroky. Vendor
  STMicroelectronics HAL/CMSIS code under `Drivers/` retains ST's own license
  headers. The application-specific files under `AppSource/` and `Core/Src/`
  carry the author's copyright with no further license grant in the original
  archive; mirrored here as a reference artifact, defer to upstream for any
  redistribution beyond reference reading.
- **Gateware** (`Gateware/SquareSDR2_V75_52/`) — derived from the Hermes-Lite 2
  gateware (softerhardware/Hermes-Lite2, mi0bot openhpsdr-thetis), GPL-2.0-or-
  later. Individual files carry their original GPL headers. New / modified RTL
  by the Square SDR 2 manufacturer inherits the GPL by license-compatibility.

## Why this snapshot is in the repo

Zeus issue #361 reports that the Band Volts toggle (PR #314, shipped in v0.7.3)
has no effect on Square SDR 2. The board discovers as `HermesLite2` over P1
discovery but is a clone of the HL2 hardware with its own front-end signaling
path. To audit what Zeus must put on the wire for this radio without depending
on a remote download remaining available, the relevant firmware + gateware are
mirrored here.

See `README.md` in this directory for the architectural findings and what they
mean for Zeus.

## Restoring the original archive

```bash
curl -L -o squaresdr2.zip \
  "https://web.wideservis.cz/cloud/index.php/s/SquareSDR2/download"
unzip squaresdr2.zip
```

The original archive is ~100 MB and additionally contains pre-built `.rbf` /
`.sof` bitstreams and vendor binary blobs that are intentionally not mirrored
here. Pull the upstream archive directly when you need those.
