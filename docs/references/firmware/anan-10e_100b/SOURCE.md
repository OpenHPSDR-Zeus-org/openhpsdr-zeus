# ANAN-10E / ANAN-100B (HermesII-class) — upstream source mirror

- **Radios covered:** ANAN-10E (0x02 orig + 0x06 revised), ANAN-100B
- **Protocol:** P1+P2
- **Upstream:** https://github.com/TAPR/OpenHPSDR-Firmware — Protocol 1/ANAN-10E & 100B, Protocol 2/ANAN-10E & 100B
- **Snapshot:** 2026-06-27
- **License:** GPL-3.0
- **Mirror size:** 3.1M (362 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> The 0x02->0x06 board-id reclass of the revised 10E is host-side (Zeus HpsdrBoardKind / Thetis), not a separate gateware build — this single 10E source covers both.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
