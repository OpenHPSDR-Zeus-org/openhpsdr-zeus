# ANAN-G2 family (Saturn FPGA + Raspberry Pi host) — upstream source mirror

- **Radios covered:** ANAN-G2, ANAN-G2 MkII, ANAN-G2-1K (one shared Saturn build)
- **Protocol:** P1+P2
- **Upstream:** https://github.com/laurencebarker/Saturn (G8NJJ) — Xilinx FPGA/ gateware + sw_projects/ RPi host (p2app)
- **Snapshot:** 2026-06-27
- **License:** GPL-3.0
- **Mirror size:** 22M (1312 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> Doug's primary bench radio. Covers all three G2 variants. Front-panel adapters under ../_companion-mcu/saturn-g2-v1-front-panel-adapter and saturn-g2-v2-front-panel.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
