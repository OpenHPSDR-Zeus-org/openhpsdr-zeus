# Alex filter (BPF/LPF) test board (Gerbers/report) — upstream source mirror

- **Radios covered:** Alex filter
- **Protocol:** n/a
- **Upstream:** https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Alex%20Filter%20Test%20Board
- **Snapshot:** 2026-06-27
- **License:** GPL (per-file)
- **Mirror size:** 228K (9 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> No standalone FPGA; Alex band/relay control logic lives inside the Hermes/Metis/Penelope gateware.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
