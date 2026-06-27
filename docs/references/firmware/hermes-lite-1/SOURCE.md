# Hermes-Lite 1 (HL1, deprecated) — upstream source mirror

- **Radios covered:** Hermes-Lite (original)
- **Protocol:** P1
- **Upstream:** https://github.com/softerhardware/Hermes-Lite — rtl/ Verilog
- **Snapshot:** 2026-06-27
- **License:** rtl derived from openHPSDR Hermes (GPL family)
- **Mirror size:** 2.3M (234 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
