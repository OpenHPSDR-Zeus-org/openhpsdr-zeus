#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later
#
# update.sh — fast-forward a Zeus git checkout to the latest upstream and
# rebuild both the .NET host and the web UI (macOS / Linux).
#
# Mirrors the "Update now" button in Settings -> Updates, which only pulls
# source; this script does the rebuild the running app cannot do to itself.
# Run it, then restart Zeus.
#
#   ./scripts/update.sh
#
# It resolves the repo root from its own location, so the working directory
# doesn't matter.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"
echo "==> Zeus update — repo: $repo"

# 1. Refuse to pull over uncommitted work, then fast-forward to upstream.
if [ -n "$(git status --porcelain)" ]; then
  echo "Working tree has uncommitted changes — commit or stash them before updating." >&2
  exit 1
fi
echo "==> git pull --ff-only"
git pull --ff-only

# 2. Rebuild the web UI into Zeus.Server.Hosting/wwwroot. A plain 'dotnet build'
#    only regenerates wwwroot when it's cold, so after a pull we rebuild it
#    explicitly to guarantee the served SPA matches the new source.
echo "==> Building web UI (zeus-web -> wwwroot)"
npm --prefix zeus-web ci
npm --prefix zeus-web run build

# 3. Rebuild the backend / host. wwwroot is fresh from step 2, so this won't
#    redo the npm build.
echo "==> Building backend (Zeus.slnx)"
dotnet build Zeus.slnx -c Debug

echo "==> Update complete. Restart Zeus to apply."
