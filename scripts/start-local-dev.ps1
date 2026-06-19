# SPDX-License-Identifier: GPL-2.0-or-later
#
# Switch to a fast local integration branch that tracks the latest org develop.
# Commit locally here, then export logical commits with scripts/export-pr.ps1.
#
# Example:
#   pwsh scripts/start-local-dev.ps1

[CmdletBinding()]
param(
    [string]$Branch = 'local/develop',
    [string]$BaseRemote = 'OpenHPSDR-Zeus-org',
    [string]$BaseBranch = 'develop',
    [switch]$NoFetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GitCapture {
    param([string[]]$Arguments)

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }

    return $output
}

function Invoke-Native {
    param(
        [string]$Label,
        [string]$File,
        [string[]]$Arguments
    )

    Write-Host "==> $Label" -ForegroundColor Cyan
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

function Set-GitHooksPath {
    if (-not (Test-Path -LiteralPath '.githooks')) {
        return
    }

    $configured = & git config --get core.hooksPath
    if ($LASTEXITCODE -ne 0 -or $configured -ne '.githooks') {
        Invoke-Native 'Configure git hooks path' 'git' @('config', 'core.hooksPath', '.githooks')
    }
}

function Test-GitRef {
    param([string]$Ref)

    & git show-ref --verify --quiet $Ref
    return $LASTEXITCODE -eq 0
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot
Set-GitHooksPath

$status = @(Invoke-GitCapture @('status', '--porcelain'))
if ($status.Count -gt 0) {
    throw "Working tree has uncommitted changes; commit or stash before switching local trunks."
}

if (-not $NoFetch) {
    Invoke-Native "Fetch $BaseRemote/$BaseBranch" 'git' @('fetch', $BaseRemote, $BaseBranch)
}

$baseRef = "$BaseRemote/$BaseBranch"
if (-not (Test-GitRef "refs/remotes/$baseRef")) {
    throw "Base ref '$baseRef' is not available. Check the remote name or run git fetch."
}

if (Test-GitRef "refs/heads/$Branch") {
    Invoke-Native "Switch to $Branch" 'git' @('switch', $Branch)
    Invoke-Native "Rebase $Branch onto $baseRef" 'git' @('rebase', $baseRef)
}
else {
    Invoke-Native "Create $Branch from $baseRef" 'git' @('switch', '-c', $Branch, $baseRef)
}

Write-Host "==> Local trunk ready" -ForegroundColor Green
Write-Host "  Commit normally on $Branch."
Write-Host "  Export a PR with: pwsh scripts/export-pr.ps1 -Commit HEAD -Name '<short name>' -Issue <id>"
