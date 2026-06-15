# SPDX-License-Identifier: GPL-2.0-or-later
#
# update.ps1 — fast-forward a Zeus git checkout to the latest upstream and
# rebuild both the .NET host and the web UI (Windows / PowerShell 7+).
#
# Mirrors the "Update now" button in Settings -> Updates, which only pulls
# source; this script does the rebuild the running app cannot do to itself.
# Run it, then restart Zeus.
#
#   pwsh scripts/update.ps1
#
# It resolves the repo root from its own location, so the working directory
# doesn't matter.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
Write-Host "==> Zeus update — repo: $repo" -ForegroundColor Cyan

# 1. Refuse to pull over uncommitted work, then fast-forward to upstream.
if (git status --porcelain) {
    throw "Working tree has uncommitted changes — commit or stash them before updating."
}
Write-Host "==> git pull --ff-only"
git pull --ff-only

# 2. Rebuild the web UI into Zeus.Server.Hosting/wwwroot. A plain 'dotnet build'
#    only regenerates wwwroot when it's cold, so after a pull we rebuild it
#    explicitly to guarantee the served SPA matches the new source.
Write-Host "==> Building web UI (zeus-web -> wwwroot)"
npm --prefix zeus-web ci
npm --prefix zeus-web run build

# 3. Rebuild the backend / host. wwwroot is fresh from step 2, so this won't
#    redo the npm build.
Write-Host "==> Building backend (Zeus.slnx)"
dotnet build Zeus.slnx -c Debug

Write-Host "==> Update complete. Restart Zeus to apply." -ForegroundColor Green
