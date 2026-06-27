#!/usr/bin/env pwsh
# SPDX-License-Identifier: GPL-2.0-or-later
#
# check-against-develop.ps1
# -------------------------------------------------------------------------
# Classify the CURRENT working tree's changes against origin/develop so you
# can tell, at a glance, which "uncommitted changes" are actually ALREADY
# MERGED (stale-branch noise) versus genuinely outstanding work.
#
# Why this exists: feature work gets PR'd and merged to develop, but a local
# branch left behind keeps showing the pre-merge state as "modified". That
# makes `git status` lie about what's left to commit. This script compares
# each changed file's content to develop directly, so already-merged files
# are called out explicitly.
#
# Usage:
#   pwsh scripts/check-against-develop.ps1            # report only (fetches develop)
#   pwsh scripts/check-against-develop.ps1 -NoFetch   # skip the network fetch
#   pwsh scripts/check-against-develop.ps1 -Clean     # DRY-RUN: show what -Clean would remove
#   pwsh scripts/check-against-develop.ps1 -Clean -Force   # actually discard already-merged noise
#   pwsh scripts/check-against-develop.ps1 -Branch main    # compare against a different base
#
# -Clean removes ONLY changes whose content already equals develop:
#   * tracked files          -> `git restore` (revert to HEAD; content == develop)
#   * untracked identical    -> deleted
# It never touches genuinely-new or still-differing work. Without -Force it
# only prints what it WOULD do.
[CmdletBinding()]
param(
  [string]$Remote = 'origin',
  [string]$Branch = 'develop',
  [switch]$NoFetch,
  [switch]$Clean,
  [switch]$Force,
  # Exit non-zero if any already-merged-but-uncommitted noise is found.
  # Handy for wiring into a pre-commit/pre-push hook or CI guard.
  [switch]$FailIfStale
)

$ErrorActionPreference = 'Stop'
# This script relies on git's exit codes as DATA: `git diff --quiet` exits 1 to
# signal "differs", `git rev-parse --verify --quiet` exits 1 for a missing ref.
# Under PowerShell 7.4+ a non-zero native exit would otherwise throw (because of
# ErrorActionPreference=Stop) and abort the run, so opt out for native commands.
$PSNativeCommandUseErrorActionPreference = $false

# Resolve the real git executable up front. A wrapper named `Git` calling `git`
# would recurse forever (PowerShell command lookup is case-insensitive), so bind
# the Application path explicitly.
$GitExe = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
function Git { & $GitExe @args }

$root = (Git rev-parse --show-toplevel 2>$null)
if (-not $root) { Write-Error 'Not inside a git repository.'; exit 1 }
Set-Location $root

$ref = "$Remote/$Branch"

if (-not $NoFetch) {
  Write-Host "Fetching $ref ..." -ForegroundColor DarkGray
  Git fetch $Remote $Branch --quiet
}

Git rev-parse --verify --quiet "$ref^{commit}" > $null
if ($LASTEXITCODE -ne 0) {
  Write-Error "Cannot resolve '$ref'. Is the remote/branch correct? (try without -NoFetch)"
  exit 1
}

$status = Git status --porcelain=v1
if (-not $status) {
  Write-Host "Working tree clean - nothing to classify." -ForegroundColor Green
  exit 0
}

$mergedTracked    = [System.Collections.Generic.List[string]]::new()  # content == develop -> discardable
$pendingTracked   = [System.Collections.Generic.List[string]]::new()  # real outstanding work
$untrackedSame    = [System.Collections.Generic.List[string]]::new()  # untracked, identical to develop -> deletable
$untrackedDiffers = [System.Collections.Generic.List[string]]::new()  # untracked, develop has this path but differs -> review
$untrackedNew     = [System.Collections.Generic.List[string]]::new()  # genuinely new file

function Resolve-PorcelainPath([string]$p) {
  if ($p.StartsWith('"') -and $p.EndsWith('"')) { $p = $p.Substring(1, $p.Length - 2) }
  if ($p -match ' -> ') { $p = ($p -split ' -> ')[-1] }
  return $p
}

# True when the working-tree file is byte-identical to develop's version after
# git's own line-ending normalization (hash-object applies the same clean filter
# as develop's stored blob, so CRLF/LF differences do not cause false mismatches).
function Same-AsDevelop([string]$path) {
  $devHash = Git rev-parse --verify --quiet "${ref}:${path}"
  if ($LASTEXITCODE -ne 0 -or -not $devHash) { return $false }
  $wtHash = Git hash-object -- $path 2>$null
  return ($wtHash -and ($wtHash -eq $devHash))
}

foreach ($line in $status) {
  if ($line.Length -lt 4) { continue }
  $code = $line.Substring(0, 2)
  $path = Resolve-PorcelainPath $line.Substring(3)

  if ($code -eq '??') {
    $devHash = Git rev-parse --verify --quiet "${ref}:${path}"
    if ($LASTEXITCODE -ne 0 -or -not $devHash) { $untrackedNew.Add($path); continue }
    if (Same-AsDevelop $path) { $untrackedSame.Add($path) } else { $untrackedDiffers.Add($path) }
  }
  else {
    # Tracked & changed. Whitespace/CRLF-insensitive compare to develop.
    Git diff --quiet --ignore-all-space --ignore-cr-at-eol "$ref" -- "$path" 2>$null
    if ($LASTEXITCODE -eq 0) { $mergedTracked.Add($path) } else { $pendingTracked.Add($path) }
  }
}

function Write-Group([string]$title, $items, [string]$color) {
  Write-Host ""
  Write-Host ("{0} ({1})" -f $title, $items.Count) -ForegroundColor $color
  foreach ($i in $items) { Write-Host "    $i" }
}

Write-Host ""
Write-Host "=== Working tree vs $ref ===" -ForegroundColor Cyan

Write-Group "ALREADY ON DEVELOP - tracked, safe to discard" $mergedTracked 'Yellow'
Write-Group "ALREADY ON DEVELOP - untracked & identical, safe to delete" $untrackedSame 'Yellow'
Write-Group "REVIEW - untracked, develop has this path but content differs" $untrackedDiffers 'Magenta'
Write-Group "OUTSTANDING - tracked changes not on develop" $pendingTracked 'Green'
Write-Group "OUTSTANDING - genuinely new files" $untrackedNew 'Green'

$staleCount = $mergedTracked.Count + $untrackedSame.Count
Write-Host ""
Write-Host ("Summary: {0} already-merged, {1} to review, {2} outstanding." -f `
    $staleCount, $untrackedDiffers.Count, ($pendingTracked.Count + $untrackedNew.Count)) -ForegroundColor Cyan

if ($Clean) {
  if ($staleCount -eq 0) {
    Write-Host "Nothing already-merged to clean." -ForegroundColor Green
  }
  elseif (-not $Force) {
    Write-Host ""
    Write-Host "DRY-RUN (-Clean without -Force). Would discard the $staleCount already-merged item(s) above." -ForegroundColor DarkYellow
    Write-Host "Re-run with -Clean -Force to actually do it." -ForegroundColor DarkYellow
  }
  else {
    Write-Host ""
    foreach ($f in $mergedTracked) { Git restore --worktree --staged -- "$f"; Write-Host "  restored  $f" -ForegroundColor DarkGray }
    foreach ($f in $untrackedSame) { Remove-Item -LiteralPath $f -Force; Write-Host "  deleted   $f" -ForegroundColor DarkGray }
    Write-Host "Cleaned $staleCount already-merged item(s). Outstanding work left untouched." -ForegroundColor Green
  }
}
else {
  Write-Host "Tip: re-run with -Clean to preview discarding the already-merged noise (-Clean -Force to apply)." -ForegroundColor DarkGray
}

if ($FailIfStale -and $staleCount -gt 0) { exit 2 }
exit 0
