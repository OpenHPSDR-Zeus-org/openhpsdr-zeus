# Orion MkII family — ANAN-7000DLE / 7000DLE MkII / 8000DLE / OrionMkII — upstream source mirror

- **Radios covered:** OrionMkII, ANAN-7000DLE, ANAN-7000DLE MkII, ANAN-8000DLE (one shared Andromeda build)
- **Protocol:** P1+P2
- **Upstream:** https://github.com/TAPR/OpenHPSDR-Firmware — Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda, Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)
- **Snapshot:** 2026-06-27
- **License:** GPL-3.0
- **Mirror size:** 6.0M (586 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> There is no distinct 7000DLE-MkII gateware tree upstream; the MkII shares the 7000DLE/Andromeda build. Front-panel MCU firmware is under ../_companion-mcu/andromeda-front-panel and ../_companion-mcu/anan-8000dle-lcd.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
