# Excalibur 10 MHz reference clock board (docs only) — upstream source mirror

- **Radios covered:** Excalibur
- **Protocol:** n/a
- **Upstream:** https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Excalibur
- **Snapshot:** 2026-06-27
- **License:** GPL (per-file)
- **Mirror size:** 4.0K (1 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> Oscillator board — no FPGA gateware exists. Documentation only.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
