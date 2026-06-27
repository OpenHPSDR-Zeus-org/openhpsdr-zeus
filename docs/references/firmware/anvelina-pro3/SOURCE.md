# ANVELINA-PRO3 (0x0A OrionMkII alias family) — upstream source mirror

- **Radios covered:** ANVELINA-PRO3
- **Protocol:** P2
- **Upstream:** https://github.com/N1GP/Anvelina_PROIII — Protocol-2 Verilog (+ n1gp/Anvelina_PROIII_Bootloader)
- **Snapshot:** 2026-06-27
- **License:** GPL-3.0
- **Mirror size:** 496K (38 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
