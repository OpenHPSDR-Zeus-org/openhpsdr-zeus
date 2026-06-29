# Saturn G2 V2 front panel — upstream source mirror

- **Radios covered:** ANAN-G2 MkII front panel
- **Protocol:** n/a
- **Upstream:** https://github.com/laurencebarker/SaturnG2V2_Front_Panel
- **Snapshot:** 2026-06-27
- **License:** see upstream repo
- **Mirror size:** 724K (56 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
