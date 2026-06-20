#!/usr/bin/env bash
# Build the Zeus Operator's Manual PDF from the chapter sources in chapters/.
# Usage: ./build.sh [output.pdf]   (default: build/Zeus-Operator-Manual.pdf)
# Requires: node + npm, and Google Chrome (headless print engine). macOS/Linux.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUTPDF="${1:-$HERE/build/Zeus-Operator-Manual.pdf}"
mkdir -p "$HERE/build"

# Markdown engine (kept out of git; see .gitignore).
( cd "$HERE" && [ -d node_modules/marked ] || npm install --silent )

node "$HERE/assemble.mjs"

# Locate a Chromium-family browser for the print engine.
CHROME=""
for c in \
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
  "/Applications/Chromium.app/Contents/MacOS/Chromium" \
  "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" \
  "$(command -v google-chrome 2>/dev/null || true)" \
  "$(command -v chromium 2>/dev/null || true)"; do
  if [ -n "$c" ] && [ -x "$c" ]; then CHROME="$c"; break; fi
done
if [ -z "$CHROME" ]; then echo "ERROR: no Chrome/Chromium found for PDF printing." >&2; exit 1; fi

"$CHROME" --headless --disable-gpu --no-pdf-header-footer --virtual-time-budget=4000 \
  --print-to-pdf="$OUTPDF" "file://$HERE/build/Zeus-Operator-Manual.html"

echo "Zeus Operator's Manual -> $OUTPDF"
