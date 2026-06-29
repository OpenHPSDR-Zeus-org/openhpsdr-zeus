# Firmware-mirror tooling

- **`acquire-firmware-sources.sh`** — re-clones every upstream as a source-only
  partial/sparse checkout (bitstreams + build databases excluded) into a target
  dir. Usage: `./acquire-firmware-sources.sh /tmp/fw-mirror`. Deps: bash, git
  (>=2.27), curl, unzip. No Quartus/Vivado needed.
- **`qar_extract.py`** — standalone unpacker for Quartus `.qar` project archives
  (a `q\x02` + uint16-count container of zlib-compressed entries). Several TAPR
  boards ship source only as `.qar`; this extracts the HDL without Quartus.
  Usage: `python3 qar_extract.py file.qar out_dir/`.

The committed tree was produced by running the acquire script, extracting every
board `.qar` with `qar_extract.py`, and stripping non-source (PCB/Gerber, build
DBs, images, IDE caches). See `../README.md` for the coverage map.
