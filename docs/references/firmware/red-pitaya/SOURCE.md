# Red Pitaya (HPSDR P1 emulation) — upstream source mirror

- **Radios covered:** Red Pitaya (DH1KLM)
- **Protocol:** P1
- **Upstream:** https://github.com/pavel-demin/red-pitaya-notes (projects/sdr_*_hpsdr*) + DH1KLM fork branch 122-16
- **Snapshot:** 2026-06-27
- **License:** MIT (pavel-demin)
- **Mirror size:** 7.9M (573 files), source-only
- **Refresh:** re-run docs/references/radios/_tools/acquire-firmware-sources.sh (see ../README.md); .qar archives are extracted with _tools/qar_extract.py

> Only the sdr_*_hpsdr* projects (and shared cores) are mirrored; the ~60 unrelated red-pitaya-notes projects are excluded.

This is a **source-only** mirror: built FPGA bitstreams (`.rbf/.sof/.jic/.pof`),
Quartus/Vivado build databases, PCB/Gerber fab files, and binary images are
excluded. Where upstream shipped Quartus `.qar` project archives, the HDL was
extracted from them (the `.qar` itself is not re-committed). Defer to upstream
for redistribution beyond reference reading.
