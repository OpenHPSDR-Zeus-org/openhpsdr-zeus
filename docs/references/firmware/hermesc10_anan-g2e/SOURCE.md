# HermesC10 / ANAN-G2E (Cyclone-10, N1GP lineage) — upstream source mirror

- **Radios covered:** ANAN-G2E (Thetis HermesC10, board id 0x14)
- **Protocol:** P1+P2
- **Upstream:** https://github.com/TAPR/OpenHPSDR-Firmware — Protocol 1/ANAN-G2E, Protocol 2/HermesC10 (ANAN-G2E)
- **Snapshot:** 2026-06-27
- **License:** GPL-3.0
- **Mirror size:** 2.4M (244 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> Upstream ships this board as .qar Quartus archives only (no loose source). The .v/.vhd here were extracted from those archives — see ../README.md.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
