# HPSDR Firmware & Gateware Source Mirror

Source-only mirror of the **FPGA gateware and microcontroller firmware** for every
HPSDR Protocol-1 / Protocol-2 radio Zeus supports. Use it to read what a board
*actually does on the wire* when fixing a protocol/DSP bug or adding a feature, without
owning the hardware. The reference implementation for host behaviour is still Thetis;
**this tree is the ground truth for the radio side.**

> **Why it's here:** Zeus targets ~29 radios across two protocols and we can't bench
> most of them. Mirroring the firmware/gateware sources lets any contributor (or agent)
> grep the real RTL/C for a board instead of guessing. See per-board `SOURCE.md` for
> upstream URL, license, and snapshot date.

## What's in here (and what isn't)

- **Source-only.** Built FPGA bitstreams (`.rbf/.sof/.jic/.pof`), Quartus/Vivado build
  databases, PCB/Gerber fab files, 3D models, and binary images are **excluded**.
- **`.qar`-extracted.** Several TAPR boards (e.g. ANAN-G2E/HermesC10, the P2 Atlas
  bricks) ship source *only* as Quartus `.qar` archives. Those were unpacked to loose
  `.v/.vhd` with `_tools/qar_extract.py`; the `.qar` blobs themselves are not committed.
- **Shared builds are not duplicated by radio.** Boards that share one gateware build
  share one directory (e.g. the whole Orion-MkII family → `orion-mkii_7000-8000dle/`,
  all three G2 variants → `anan-g2_saturn/`). The coverage map below resolves each
  radio to its directory.

## Coverage map — every Zeus-supported radio

| Radio | Protocol | Source directory |
|-------|:--------:|------------------|
| Metis | P1+P2 | `metis/` |
| Mercury | P1+P2 | `mercury/` |
| Penelope / PennyLane | P1+P2 | `penelope/` |
| Hermes | P1+P2 | `hermes/` |
| ANAN-10 | P1+P2 | `anan-10_100/` |
| ANAN-100 | P1+P2 | `anan-10_100/` |
| ANAN-10E (0x02 orig) | P1+P2 | `anan-10e_100b/` |
| ANAN-10E (0x06 revised) | P1+P2 | `anan-10e_100b/` |
| ANAN-100B | P1+P2 | `anan-10e_100b/` |
| ANAN-100D | P1+P2 | `anan-100d_angelia/` |
| Angelia | P1+P2 | `anan-100d_angelia/` |
| ANAN-200D | P1+P2 | `anan-200d_orion/` |
| Orion | P1+P2 | `anan-200d_orion/` |
| OrionMkII | P1+P2 | `orion-mkii_7000-8000dle/` |
| ANAN-7000DLE | P1+P2 | `orion-mkii_7000-8000dle/` |
| ANAN-7000DLE MkII | P1+P2 | `orion-mkii_7000-8000dle/` |
| ANAN-8000DLE | P1+P2 | `orion-mkii_7000-8000dle/` |
| ANVELINA-PRO3 | P2 | `anvelina-pro3/` |
| Red Pitaya | P1 | `red-pitaya/` |
| ANAN-G2 | P1+P2 | `anan-g2_saturn/` |
| ANAN-G2 MkII | P1+P2 | `anan-g2_saturn/` |
| ANAN-G2-1K | P1+P2 | `anan-g2_saturn/` |
| HermesC10 / ANAN-G2E | P1+P2 | `hermesc10_anan-g2e/` |
| Hermes-Lite | P1 | `hermes-lite-1/` |
| Hermes-Lite 2 | P1 | `hermes-lite-2/` |
| SquareSDR 2 | P1 | `square-sdr-2/` |
| SquareSDR (v1) | P1 | `— (not redistributable; local only)` |
| Excalibur clock | n/a — docs only | `_bricks-svn/excalibur/` |
| Alex filter | n/a — logic in Hermes/Metis FPGA | `_bricks-svn/alex-filter/` |
| Janus codec | n/a — CPLD | `_bricks-svn/janus/` |

✅ **All 29 radios resolve to an obtainable source.** No coverage gaps.

## Source directories

| Directory | Radios | Upstream | License | Size |
|-----------|--------|----------|---------|------|
| `anan-g2_saturn/` | ANAN-G2, ANAN-G2 MkII, ANAN-G2-1K (one shared Saturn build) | https://github.com/laurencebarker/Saturn (G8NJJ) | GPL-3.0 | 22M |
| `hermesc10_anan-g2e/` | ANAN-G2E (Thetis HermesC10, board id 0x14) | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 2.4M |
| `orion-mkii_7000-8000dle/` | OrionMkII, ANAN-7000DLE, ANAN-7000DLE MkII, ANAN-8000DLE (one shared Andromeda build) | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 6.0M |
| `anan-200d_orion/` | ANAN-200D, Orion | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 2.8M |
| `anan-100d_angelia/` | ANAN-100D, Angelia | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 4.1M |
| `anan-10e_100b/` | ANAN-10E (0x02 orig + 0x06 revised), ANAN-100B | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 3.1M |
| `anan-10_100/` | ANAN-10, ANAN-100 | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 3.9M |
| `hermes/` | Hermes | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 3.9M |
| `metis/` | Metis | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 11M |
| `mercury/` | Mercury | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 4.5M |
| `penelope/` | Penelope, PennyLane | https://github.com/TAPR/OpenHPSDR-Firmware | GPL-3.0 | 2.2M |
| `hermes-lite-2/` | Hermes-Lite 2; SquareSDR/SquareSDR2 derive from this gateware | https://github.com/softerhardware/Hermes-Lite2 | GPL-derived | 3.8M |
| `hermes-lite-1/` | Hermes-Lite (original) | https://github.com/softerhardware/Hermes-Lite | rtl derived from openHPSDR Hermes | 2.3M |
| `red-pitaya/` | Red Pitaya (DH1KLM) | https://github.com/pavel-demin/red-pitaya-notes (projects/sdr_*_hpsdr*) + DH1KLM fork branch 122-16 | MIT | 7.9M |
| `anvelina-pro3/` | ANVELINA-PRO3 | https://github.com/N1GP/Anvelina_PROIII | GPL-3.0 | 496K |

### Companion microcontroller firmware (`_companion-mcu/`)

| Directory | What | Upstream |
|-----------|------|----------|
| `_companion-mcu/andromeda-front-panel/` | Andromeda (7000DLE/OrionMkII) front-panel MCU | https://github.com/laurencebarker/Andromeda_front_panel |
| `_companion-mcu/anan-8000dle-lcd/` | ANAN-8000DLE front-panel LCD MCU | https://github.com/TAPR/OpenHPSDR-8000DLE_LCD_Firmware |
| `_companion-mcu/saturn-g2-v2-front-panel/` | Saturn G2 V2 front panel | https://github.com/laurencebarker/SaturnG2V2_Front_Panel |
| `_companion-mcu/saturn-g2-v1-front-panel-adapter/` | Saturn G2 V1 front-panel adapter | https://github.com/laurencebarker/SaturnG2V1FrontPanelAdapter |

### Original Atlas-bus bricks (`_bricks-svn/`)

Documentation / CPLD source from `TAPR/OpenHPSDR-SVN` for the brick-era boards whose
FPGA logic isn't a standalone tree.

- **`_bricks-svn/excalibur/`** — Excalibur 10 MHz reference clock board (docs only)
- **`_bricks-svn/alex-filter/`** — Alex filter (BPF/LPF) test board (Gerbers/report)
- **`_bricks-svn/janus/`** — Janus A/D-D/A codec brick (CPLD source)

## Binary-only / manual download (no public source)

Apache Labs ships modern production bitstreams as binaries (`.rbf`) with release-note
PDFs, **not** source, on <https://apache-labs.com/instant-downloads.html>. This is not a
coverage gap — the *source* for those exact boards is the redundant upstream already
mirrored here (TAPR for OrionMkII/8000DLE, `laurencebarker/Saturn` for the G2). Pull the
vendor binary directly only if you need the precise shipped build.

- **SquareSDR v1** is licensed *personal-use-only* upstream → **not redistributed in
  this repo.** `_tools/acquire-firmware-sources.sh` can fetch it to a local-only dir.

- **SquareSDR 2** already lives in this repo at [`../square-sdr-2/`](../square-sdr-2/)
  (HL2-derived; see its `SOURCE.md`).

## Refreshing this mirror

```bash
# from repo root — re-clones source-only upstreams + re-extracts .qar
docs/references/radios/_tools/acquire-firmware-sources.sh /tmp/fw-mirror
```

`_tools/qar_extract.py` is a standalone Quartus-`.qar` unpacker (no Quartus needed):
`python3 _tools/qar_extract.py file.qar out_dir/`.

Per-board provenance, snapshot date, and license are in each directory's `SOURCE.md`.
