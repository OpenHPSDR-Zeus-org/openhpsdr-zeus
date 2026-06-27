# Hermes-Lite 2 (HL2, board id 0x06) — upstream source mirror

- **Radios covered:** Hermes-Lite 2; SquareSDR/SquareSDR2 derive from this gateware
- **Protocol:** P1
- **Upstream:** https://github.com/softerhardware/Hermes-Lite2 — gateware/ Verilog + project wiki (Protocol docs)
- **Snapshot:** 2026-06-27
- **License:** GPL-derived (per-file; gateware descends from openHPSDR Hermes)
- **Mirror size:** 3.8M (414 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> PCB/Gerber hardware and release photos stripped; gateware + firmware + wiki text kept. Canonical HL2 protocol reference also at ../../protocol-1/hermes-lite2-protocol.md.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
