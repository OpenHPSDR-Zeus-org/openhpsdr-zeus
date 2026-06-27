#!/usr/bin/env bash
# acquire-firmware-sources.sh
# Clone/download every recommended SOURCE-ONLY HPSDR firmware/gateware mirror into
# a per-board tree:  $DEST/<board>/  (mirrors docs/references/radios/<board>/).
# Built bitstreams (.rbf/.sof/.jic/.pof/.bin/.bit/.jam/.dcp/.tbz) are excluded;
# Quartus project archives (.qar) are kept where they are the source-of-record.
# Idempotent: a board dir that already has content is skipped. Re-run to fill gaps.
# Deps: bash, git (>=2.27 for partial+sparse), curl, unzip.  No Quartus/Vivado needed.
set -u

DEST="${1:-./firmware-mirror}"
CACHE="$DEST/_cache"            # shared monorepos cloned once, copied per board
SNAP="$(date +%F)"
mkdir -p "$DEST" "$CACHE"

# ---- helpers ---------------------------------------------------------------

have() { command -v "$1" >/dev/null 2>&1; }
for t in git curl unzip; do have "$t" || { echo "FATAL: '$t' not found on PATH"; exit 1; }; done

# non-empty dir?  -> treat as already-done
done_dir() { [ -d "$1" ] && [ -n "$(ls -A "$1" 2>/dev/null)" ]; }

# strip built bitstreams + heavy Quartus build databases (keeps .qar source archives)
prune() {
  find "$1" -type f \( \
       -iname '*.rbf'  -o -iname '*.sof'  -o -iname '*.jic'  -o -iname '*.pof' \
    -o -iname '*.jam'  -o -iname '*.bin'  -o -iname '*.bit'  -o -iname '*.dcp' \
    -o -iname '*.tbz'  -o -iname '*.tbz2' -o -iname '*.rpt'  -o -iname '*.cdb' \
    -o -iname '*.hdb'  -o -iname '*.smsg' -o -iname '*.qarlog' -o -iname '*.bak' \
    -o -iname '*.done' -o -iname '*.msi'  -o -iname '*.exe'  -o -iname '*.dll' \
    -o -iname '*.pdb'  -o -iname '*.vo'   -o -iname '*.sdo' \
    -o -iname '*.qar'  -o -iname '*.qarlog' -o -iname '*.zip' -o -iname '*.rar' \
    -o -iname '*.qdb'  -o -iname '*.db'   -o -iname '*.bak2' \
  \) -delete 2>/dev/null || true
}

# write the repo-convention SOURCE.md stub
# args: dir  title  upstream-url  license  archive-root  restore-cmd
write_source() {
  mkdir -p "$1"
  {
    echo "# $2 — upstream source mirror"
    echo
    echo "- **Upstream:** $3"
    echo "- **Snapshot date:** $SNAP"
    echo "- **Archive root:** $5"
    echo "- **Authors / copyright / license:** $4"
    echo "- **Restore / refresh:** \`$6\`"
    echo
    echo "Source-only mirror: built FPGA bitstreams"
    echo "(.rbf/.sof/.jic/.pof/.jam/.bin/.bit/.dcp/.tbz) and heavy Quartus build"
    echo "databases are excluded. Quartus project archives (.qar) are EXCLUDED from this mirror (large/opaque);"
    echo "restore the buildable project from upstream and regenerate bitstreams with Quartus/Vivado"
    echo "(\`quartus_sh --archive -restore <file>.qar\`)."
  } > "$1/SOURCE.md"
  echo "  wrote $1/SOURCE.md"
}

# copy a sub-path out of a cached monorepo into a board dir
# args: cache-root  rel-path  dest-dir
copy_subtree() {
  if [ -d "$1/$2" ]; then
    mkdir -p "$3"
    cp -R "$1/$2" "$3/"
    echo "  copied: $2 -> $3"
  else
    echo "  WARN: missing in cache: $2 (sparse path may have changed upstream)"
  fi
}

# ---- shared monorepo caches -----------------------------------------------

cache_tapr() {
  local d="$CACHE/OpenHPSDR-Firmware"
  if [ -d "$d/.git" ]; then echo "[cache] TAPR/OpenHPSDR-Firmware present, skipping"; return; fi
  echo "[cache] cloning TAPR/OpenHPSDR-Firmware (blob:none, source paths only)..."
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/TAPR/OpenHPSDR-Firmware.git "$d" || { echo "  clone FAILED"; return; }
  ( cd "$d" \
    && git sparse-checkout init --no-cone \
    && git sparse-checkout set --no-cone \
        "Protocol 1/Hermes" "Protocol 1/Mercury" "Protocol 1/Metis" "Protocol 1/Penelope" \
        "Protocol 1/ANAN-10 & 100" "Protocol 1/ANAN-10E & 100B" "Protocol 1/ANAN-100D" \
        "Protocol 1/ANAN-200D" "Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda" "Protocol 1/ANAN-G2E" \
        "Protocol 2/Hermes (ANAN-10 and 100)" "Protocol 2/ANAN-10E & 100B" "Protocol 2/Angelia (ANAN-100D)" \
        "Protocol 2/Orion (ANAN-200D)" "Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)" \
        "Protocol 2/HermesC10 (ANAN-G2E)" "Protocol 2/Atlas" \
        "!*.rbf" "!*.sof" "!*.jic" "!*.pof" "!*.tbz" "!*.tbz2" "!*.qar" "!*.zip" "!*.rar" \
    && git checkout master ) && echo "  TAPR cache ready"
}

cache_svn() {
  local d="$CACHE/OpenHPSDR-SVN"
  if [ -d "$d/.git" ]; then echo "[cache] TAPR/OpenHPSDR-SVN present, skipping"; return; fi
  echo "[cache] cloning TAPR/OpenHPSDR-SVN (blob:none, brick/codec/clock/alex paths only)..."
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/TAPR/OpenHPSDR-SVN.git "$d" || { echo "  clone FAILED"; return; }
  ( cd "$d" \
    && git sparse-checkout init --no-cone \
    && git sparse-checkout set --no-cone \
        "Hermes" "Metis" "Mercury" "Penelope" "Excalibur" "Janus" \
        "Alex Filter Test Board" "Angelia_new_protocol" \
        "!*.rbf" "!*.sof" "!*.jic" "!*.pof" "!*.msi" "!*.exe" "!*.dll" "!*.qar" "!*.zip" "!*.rar" \
        "!*.pdb" "!*.gz" "!*.vo" "!*.sdo" \
    && git checkout master ) && echo "  SVN cache ready"
}

cache_saturn() {
  local d="$CACHE/Saturn"
  if [ -d "$d/.git" ]; then echo "[cache] laurencebarker/Saturn present, skipping"; return; fi
  echo "[cache] cloning laurencebarker/Saturn (blob:none, no *.bin/*.bit/*.dcp)..."
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/laurencebarker/Saturn.git "$d" || { echo "  clone FAILED"; return; }
  ( cd "$d" \
    && git sparse-checkout init --no-cone \
    && git sparse-checkout set --no-cone '/*' '!*.bin' '!*.bit' '!*.dcp' \
    && git checkout main ) && echo "  Saturn cache ready"
}

# ---- per-board: TAPR Firmware (+ optional SVN historical) -------------------
# args: slug  title  "P1 rel"  "P2 rel"  [svn-rel]
tapr_board() {
  local slug="$1" title="$2" p1="$3" p2="$4" svn="${5:-}"
  local dir="$DEST/$slug"
  if done_dir "$dir"; then echo "[$slug] already populated, skipping"; return; fi
  echo "[$slug] $title"
  local fw="$CACHE/OpenHPSDR-Firmware"
  [ -n "$p1" ] && copy_subtree "$fw" "$p1" "$dir/firmware-P1"
  [ -n "$p2" ] && copy_subtree "$fw" "$p2" "$dir/firmware-P2"
  if [ -n "$svn" ]; then copy_subtree "$CACHE/OpenHPSDR-SVN" "$svn" "$dir/SVN"; fi
  prune "$dir"
  write_source "$dir" "$title" \
    "https://github.com/TAPR/OpenHPSDR-Firmware (+ TAPR/OpenHPSDR-SVN for brick history)" \
    "GPL-3.0 (Firmware repo LICENSE); SVN per-file headers" \
    "$slug/firmware-P1, $slug/firmware-P2, $slug/SVN" \
    "git -C $CACHE/OpenHPSDR-Firmware pull --ff-only && re-run this script"
}

# ============================================================================
echo "=== HPSDR firmware/gateware source-only mirror -> $DEST ==="

cache_tapr
cache_svn
cache_saturn

# --- Original Atlas-bus bricks ---
tapr_board metis    "Metis (Ethernet gateway brick)"      "Protocol 1/Metis"     "Protocol 2/Atlas/Metis"     "Metis"
tapr_board mercury  "Mercury (receiver brick)"            "Protocol 1/Mercury"   "Protocol 2/Atlas/Mercury"   "Mercury"
tapr_board penelope "Penelope / PennyLane (exciter brick)" "Protocol 1/Penelope" "Protocol 2/Atlas/Penelope"  "Penelope"
tapr_board hermes   "Hermes (SoC brick / ANAN-10/100)"    "Protocol 1/Hermes"    "Protocol 2/Hermes (ANAN-10 and 100)" "Hermes"

# --- ANAN P1/P2 ---
tapr_board anan-10        "ANAN-10 (Hermes-class)"   "Protocol 1/ANAN-10 & 100"   "Protocol 2/Hermes (ANAN-10 and 100)"
tapr_board anan-100       "ANAN-100 (Hermes-class)"  "Protocol 1/ANAN-10 & 100"   "Protocol 2/Hermes (ANAN-10 and 100)"
tapr_board anan-10e       "ANAN-10E (HermesII; host reclass 0x02->0x06)" "Protocol 1/ANAN-10E & 100B" "Protocol 2/ANAN-10E & 100B"
tapr_board anan-100b      "ANAN-100B (Hermes-class)" "Protocol 1/ANAN-10E & 100B" "Protocol 2/ANAN-10E & 100B"
tapr_board anan-100d      "ANAN-100D / Angelia"      "Protocol 1/ANAN-100D"       "Protocol 2/Angelia (ANAN-100D)"  "Angelia_new_protocol"
tapr_board anan-200d      "ANAN-200D / Orion"        "Protocol 1/ANAN-200D"       "Protocol 2/Orion (ANAN-200D)"

# --- Orion MkII family (0x0A) ---
tapr_board orion-mkii          "OrionMkII original"   "Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda" "Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)"
tapr_board anan-7000dle        "ANAN-7000DLE"         "Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda" "Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)"
tapr_board anan-7000dle-mkii   "ANAN-7000DLE MkII"    "Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda" "Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)"
tapr_board anan-8000dle        "ANAN-8000DLE / Andromeda" "Protocol 1/ANAN-7000DLE_ANAN-8000DLE-Andromeda" "Protocol 2/Orion_MkII (ANAN-7000DLE-8000DLE)"

# --- ANAN-G2E (HermesC10) ---
tapr_board hermesc10 "HermesC10 / ANAN-G2E (Cyclone-10)" "Protocol 1/ANAN-G2E" "Protocol 2/HermesC10 (ANAN-G2E)"

# --- ANAN-8000DLE front-panel LCD MCU (companion, not gateware) ---
lcd="$DEST/anan-8000dle/LCD-Controller-Firmware"
if done_dir "$lcd"; then echo "[anan-8000dle] LCD MCU already present"; else
  echo "[anan-8000dle] front-panel LCD MCU firmware (TAPR/OpenHPSDR-8000DLE_LCD_Firmware)"
  git clone --depth 1 https://github.com/TAPR/OpenHPSDR-8000DLE_LCD_Firmware.git "$lcd" \
    && write_source "$lcd" "ANAN-8000DLE front-panel LCD MCU firmware" \
       "https://github.com/TAPR/OpenHPSDR-8000DLE_LCD_Firmware" \
       "no LICENSE declared (treat as GPL/TAPR)" "anan-8000dle/LCD-Controller-Firmware" \
       "git -C $lcd pull --ff-only"
fi

# --- Excalibur clock (docs only; no gateware exists) ---
exc="$DEST/excalibur"
if done_dir "$exc"; then echo "[excalibur] already present"; else
  echo "[excalibur] 10 MHz reference clock board (docs only)"
  copy_subtree "$CACHE/OpenHPSDR-SVN" "Excalibur" "$exc"
  write_source "$exc" "Excalibur 10 MHz reference clock board" \
    "https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Excalibur" \
    "GPL (per-file headers)" "excalibur/Excalibur (Documentation only)" \
    "git -C $CACHE/OpenHPSDR-SVN pull --ff-only && re-run"
fi

# --- Alex filter (Gerbers/docs; control logic lives inside Hermes/Metis/Penelope FPGA) ---
alex="$DEST/alex-filter"
if done_dir "$alex"; then echo "[alex-filter] already present"; else
  echo "[alex-filter] Alex BPF/LPF board (Gerbers + report; no standalone FPGA)"
  copy_subtree "$CACHE/OpenHPSDR-SVN" "Alex Filter Test Board" "$alex"
  write_source "$alex" "Alex filter (BPF/LPF) test board" \
    "https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Alex%20Filter%20Test%20Board" \
    "GPL (per-file headers)" "alex-filter/Alex Filter Test Board" \
    "git -C $CACHE/OpenHPSDR-SVN pull --ff-only && re-run"
fi

# --- Janus codec brick ---
jan="$DEST/janus"
if done_dir "$jan"; then echo "[janus] already present"; else
  echo "[janus] A/D-D/A codec brick (CPLD source)"
  copy_subtree "$CACHE/OpenHPSDR-SVN" "Janus" "$jan"
  prune "$jan"
  write_source "$jan" "Janus A/D-D/A codec brick" \
    "https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Janus" \
    "GPL (per-file headers)" "janus/Janus" \
    "git -C $CACHE/OpenHPSDR-SVN pull --ff-only && re-run"
fi

# --- Saturn family (ANAN-G2 / G2 MkII / G2-1K) : one repo, full content in anan-g2 ---
g2="$DEST/anan-g2"
if done_dir "$g2"; then echo "[anan-g2] already present"; else
  echo "[anan-g2] Saturn FPGA + RPi host (sw_projects) for the G2 family"
  if [ -d "$CACHE/Saturn/.git" ]; then
    mkdir -p "$g2"; cp -R "$CACHE/Saturn/." "$g2/"; rm -rf "$g2/.git"; prune "$g2"
  fi
  git clone --depth 1 https://github.com/laurencebarker/SaturnG2V1FrontPanelAdapter.git \
    "$g2/FrontPanel-V1-Adapter" 2>/dev/null || true
  write_source "$g2" "ANAN-G2 (Saturn FPGA family)" \
    "https://github.com/laurencebarker/Saturn" "GPL-3.0" \
    "anan-g2/ (gateware FPGA/, host sw_projects/, FrontPanel-V1-Adapter/)" \
    "git -C $CACHE/Saturn pull --ff-only && re-run"
fi
g2m="$DEST/anan-g2-mkii"
if done_dir "$g2m"; then echo "[anan-g2-mkii] already present"; else
  echo "[anan-g2-mkii] shares Saturn gateware; adds Andromeda-style V2 front panel"
  git clone --depth 1 https://github.com/laurencebarker/SaturnG2V2_Front_Panel.git \
    "$g2m/FrontPanel-V2" 2>/dev/null || true
  write_source "$g2m" "ANAN-G2 MkII (Saturn FPGA family)" \
    "https://github.com/laurencebarker/Saturn (gateware, see ../anan-g2) + SaturnG2V2_Front_Panel" \
    "GPL-3.0" "anan-g2-mkii/FrontPanel-V2 ; gateware shared at ../anan-g2" \
    "see ../anan-g2 ; git -C $g2m/FrontPanel-V2 pull --ff-only"
fi
g2k="$DEST/anan-g2-1k"
if done_dir "$g2k"; then echo "[anan-g2-1k] already present"; else
  echo "[anan-g2-1k] 1 kW G2; shares Saturn gateware"
  write_source "$g2k" "ANAN-G2-1K (Saturn FPGA family, 1 kW)" \
    "https://github.com/laurencebarker/Saturn (gateware shared, see ../anan-g2)" \
    "GPL-3.0" "anan-g2-1k/ (pointer; gateware at ../anan-g2)" \
    "see ../anan-g2"
fi

# --- ANAN-7000DLE / OrionMkII Andromeda front panel (companion MCU) ---
andp="$DEST/anan-7000dle/Andromeda-FrontPanel"
if done_dir "$andp"; then echo "[anan-7000dle] front panel already present"; else
  git clone --depth 1 https://github.com/laurencebarker/Andromeda_front_panel.git "$andp" 2>/dev/null \
    && write_source "$andp" "Andromeda (7000DLE/OrionMkII) front-panel MCU" \
       "https://github.com/laurencebarker/Andromeda_front_panel" "see repo LICENSE" \
       "anan-7000dle/Andromeda-FrontPanel" "git -C $andp pull --ff-only" || true
fi

# --- ANVELINA-PRO3 (0x0A) : N1GP P2 gateware + bootloader ---
anv="$DEST/anvelina-pro3"
if done_dir "$anv"; then echo "[anvelina-pro3] already present"; else
  echo "[anvelina-pro3] N1GP Protocol-2 gateware + bootloader"
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/N1GP/Anvelina_PROIII.git "$anv" \
    && ( cd "$anv" && git sparse-checkout init --no-cone \
         && git sparse-checkout set --no-cone '/*' '!Orion.pof' '!output_file.pof' '!Orion.sof' '!Orion.rbf' \
         && git checkout main )
  prune "$anv"
  git clone --depth 1 https://github.com/n1gp/Anvelina_PROIII_Bootloader.git "$anv/Bootloader" 2>/dev/null || true
  prune "$anv/Bootloader"
  write_source "$anv" "ANVELINA-PRO3 (0x0A OrionMkII alias family)" \
    "https://github.com/N1GP/Anvelina_PROIII (+ n1gp/Anvelina_PROIII_Bootloader)" \
    "GPL-3.0" "anvelina-pro3/ (gateware) + anvelina-pro3/Bootloader" \
    "git -C $anv pull --ff-only"
fi

# --- Red Pitaya (0x0A alias) : pavel-demin source + DH1KLM variant ---
rp="$DEST/red-pitaya"
if done_dir "$rp"; then echo "[red-pitaya] already present"; else
  echo "[red-pitaya] pavel-demin HPSDR gateware + DH1KLM 122-16 variant"
  git clone --depth 1 https://github.com/pavel-demin/red-pitaya-notes.git "$rp"
  git clone --depth 1 -b 122-16 https://github.com/DH1KLM/red-pitaya-notes.git "$rp/DH1KLM-122-16-variant" 2>/dev/null || true
  prune "$rp"
  write_source "$rp" "Red Pitaya (HPSDR P1 emulation)" \
    "https://github.com/pavel-demin/red-pitaya-notes (+ DH1KLM fork, branch 122-16)" \
    "MIT" "red-pitaya/ (projects/sdr_*_hpsdr*) + DH1KLM-122-16-variant" \
    "git -C $rp pull --ff-only"
fi

# --- Hermes-Lite 2 (HL2, 0x06) ---
hl2="$DEST/hermes-lite-2"
if done_dir "$hl2"; then echo "[hermes-lite-2] already present"; else
  echo "[hermes-lite-2] softerhardware/Hermes-Lite2 (no gateware/bitfiles)"
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/softerhardware/Hermes-Lite2.git "$hl2" \
    && ( cd "$hl2" && git sparse-checkout init --no-cone \
         && git sparse-checkout set --no-cone '/*' '!/gateware/bitfiles' \
         && git checkout master )
  prune "$hl2"
  git clone --depth 1 https://github.com/softerhardware/Hermes-Lite2.wiki.git "$hl2/wiki" 2>/dev/null || true
  write_source "$hl2" "Hermes-Lite 2 (HL2, board id 0x06)" \
    "https://github.com/softerhardware/Hermes-Lite2 (+ .wiki for protocol docs)" \
    "GPL-derived (per-file; gateware from Hermes)" "hermes-lite-2/ (gateware) + hermes-lite-2/wiki" \
    "git -C $hl2 pull --ff-only"
fi

# --- Hermes-Lite 1 (deprecated) ---
hl1="$DEST/hermes-lite"
if done_dir "$hl1"; then echo "[hermes-lite] already present"; else
  echo "[hermes-lite] softerhardware/Hermes-Lite (HL1, deprecated; no rtl/bitfiles)"
  git clone --depth 1 --filter=blob:none --no-checkout \
    https://github.com/softerhardware/Hermes-Lite.git "$hl1" \
    && ( cd "$hl1" && git sparse-checkout init --no-cone \
         && git sparse-checkout set --no-cone '/*' '!/rtl/bitfiles' '!/scripts/spectrumanalysis/data' \
         && git checkout master )
  prune "$hl1"
  write_source "$hl1" "Hermes-Lite 1 (HL1, deprecated)" \
    "https://github.com/softerhardware/Hermes-Lite" \
    "license:null upstream; rtl derived from openHPSDR Hermes (GPL family)" \
    "hermes-lite/ (rtl/, pcb/, docs/)" "git -C $hl1 pull --ff-only"
fi

# --- SquareSDR 2 (HL2-derived) : Nextcloud firmware + gateware zips ---
sq2="$DEST/square-sdr-2"
if done_dir "$sq2"; then echo "[square-sdr-2] already present"; else
  echo "[square-sdr-2] Wide Servis Nextcloud (HL2-derived gateware + STM32 firmware)"
  mkdir -p "$sq2"
  curl -fL 'https://web.wideservis.cz/cloud/index.php/s/SquareSDR2/download?path=%2FGateware' -o "$sq2/gateware.zip" \
    && unzip -q -o "$sq2/gateware.zip" -d "$sq2/Gateware" && rm -f "$sq2/gateware.zip" || echo "  gateware fetch failed"
  curl -fL 'https://web.wideservis.cz/cloud/index.php/s/SquareSDR2/download?path=%2FFirmware' -o "$sq2/firmware.zip" \
    && unzip -q -o "$sq2/firmware.zip" -d "$sq2/Firmware" && rm -f "$sq2/firmware.zip" || echo "  firmware fetch failed"
  prune "$sq2"
  write_source "$sq2" "Square SDR 2 (HL2-derived)" \
    "https://web.wideservis.cz/cloud/index.php/s/SquareSDR2" \
    "Gateware GPL-2.0-or-later; STM32 app (c) Rene Siroky; ST HAL under ST license" \
    "square-sdr-2/Gateware + square-sdr-2/Firmware (Hardware/Mechanical CAD omitted)" \
    "re-run curl on the /download?path=... endpoints"
fi

# --- SquareSDR (v1) : Nextcloud firmware (CAD omitted) ---
sq1="$DEST/square-sdr"
if done_dir "$sq1"; then echo "[square-sdr] already present"; else
  echo "[square-sdr] Wide Servis Nextcloud SQUARE_SDR (v1, STM32 firmware + KiCad)"
  mkdir -p "$sq1"
  curl -fL 'https://web.wideservis.cz/cloud/index.php/s/SQUARE_SDR/download?path=%2F&files=Firmware' -o "$sq1/firmware.zip" \
    && unzip -q -o "$sq1/firmware.zip" -d "$sq1/Firmware" && rm -f "$sq1/firmware.zip" || echo "  firmware fetch failed"
  prune "$sq1"
  write_source "$sq1" "Square SDR (v1, HL2-derived)" \
    "https://web.wideservis.cz/cloud/index.php/s/SQUARE_SDR" \
    "personal use only (per site) — mirror for reference, do not republish" \
    "square-sdr/Firmware (Hardware/Mechanical CAD omitted by default)" \
    "re-run curl on the /download?path=... endpoints"
fi

echo
echo "=== done. Per-board SOURCE.md stubs written under $DEST/<board>/ ==="
echo "Shared monorepo caches live in $CACHE (delete to force a fresh pull)."
find "$DEST" -name SOURCE.md 2>/dev/null | sort