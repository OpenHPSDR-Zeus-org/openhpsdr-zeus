# SPDX-License-Identifier: GPL-2.0-or-later
#
# Finish the current Zeus worktree: commit, rebase, test, push, and open a PR.
#
# Example:
#   pwsh scripts/finish-work.ps1 -Message "fix: persist RX suite settings" -Issue zeus-123

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Message,

    [string]$Issue,
    [string]$PushRemote = 'origin',
    [string]$BaseRemote = 'OpenHPSDR-Zeus-org',
    [string]$BaseBranch = 'develop',
    [string]$PrRepo = 'OpenHPSDR-Zeus-org/openhpsdr-zeus',
    [string]$BdRemote = 'kb2uka',
    [switch]$Draft,
    [switch]$SkipTests,
    [switch]$NoPr,
    [switch]$NoBdPush
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

function Assert-FeatureBranch {
    param([string]$Branch)

    if ([string]::IsNullOrWhiteSpace($Branch)) {
        throw "Detached HEAD is not a finishable work branch."
    }

    if ($Branch -in @('main', 'develop')) {
        throw "Refusing to finish from protected branch '$Branch'. Start a feature/fix worktree first."
    }
}

function Get-RemoteOwner {
    param([string]$Remote)

    $remoteUrl = (Invoke-GitCapture @('remote', 'get-url', $Remote) | Select-Object -First 1)
    if ($remoteUrl -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?$') {
        return $Matches.owner
    }

    return $null
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot
Set-GitHooksPath

$branch = (Invoke-GitCapture @('branch', '--show-current') | Select-Object -First 1)
Assert-FeatureBranch $branch

Write-Host "==> Finishing $branch" -ForegroundColor Cyan

$status = @(Invoke-GitCapture @('status', '--porcelain'))
if ($status.Count -gt 0) {
    Invoke-Native 'Stage repo changes' 'git' @(
        'add', '-A', '--',
        ':!/.beads/issues.jsonl',
        ':!/.beads/interactions.jsonl'
    )

    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        Invoke-Native 'Commit changes' 'git' @('commit', '-m', $Message)
    }
    elseif ($LASTEXITCODE -eq 0) {
        Write-Host "==> No non-beads changes staged for commit" -ForegroundColor Yellow
    }
    else {
        throw "git diff --cached --quiet failed with exit code $LASTEXITCODE"
    }
}
else {
    Write-Host "==> Working tree is already clean" -ForegroundColor Cyan
}

Invoke-Native "Fetch $BaseRemote/$BaseBranch" 'git' @('fetch', $BaseRemote, $BaseBranch)
$baseRef = "$BaseRemote/$BaseBranch"
Invoke-Native "Rebase onto $baseRef" 'git' @('-c', 'rebase.autoStash=true', 'rebase', $baseRef)

$ahead = [int](Invoke-GitCapture @('rev-list', '--count', "$baseRef..HEAD") | Select-Object -First 1)
if ($ahead -eq 0) {
    throw "No commits ahead of $baseRef; there is nothing to push or PR."
}

if (-not $SkipTests) {
    Invoke-Native 'Run dotnet tests' 'dotnet' @('test', 'Zeus.slnx')

    if (Test-Path -LiteralPath (Join-Path $repoRoot 'zeus-web/package.json')) {
        Invoke-Native 'Run web tests' 'npm' @('--prefix', 'zeus-web', 'run', 'test')
    }
}
else {
    Write-Host "==> Skipping tests because -SkipTests was supplied" -ForegroundColor Yellow
}

Invoke-Native "Push $branch to $PushRemote" 'git' @('push', '-u', '--force-with-lease', $PushRemote, $branch)

if (-not $NoBdPush -and (Get-Command bd -ErrorAction SilentlyContinue)) {
    try {
        Invoke-Native "Push beads data to $BdRemote" 'bd' @('dolt', 'push', '--remote', $BdRemote)
    }
    catch {
        Write-Warning "bd dolt push failed: $($_.Exception.Message)"
    }
}

if ($NoPr) {
    Write-Host "==> PR creation skipped because -NoPr was supplied" -ForegroundColor Yellow
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Warning "GitHub CLI 'gh' is not installed or not on PATH. Branch is pushed; create the PR manually into $BaseBranch."
    exit 0
}

$owner = Get-RemoteOwner $PushRemote
$head = if ($owner) { "$owner`:$branch" } else { $branch }
$existingPr = & gh pr list --repo $PrRepo --head $branch --json url --jq '.[0].url' 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingPr)) {
    Write-Host "==> PR already exists: $existingPr" -ForegroundColor Green
    exit 0
}

$body = @(
    "Finishes $branch.",
    "",
    "Target: $BaseBranch"
)

if ($Issue) {
    $body += "Beads: $Issue"
}

$prArgs = @(
    'pr', 'create',
    '--repo', $PrRepo,
    '--base', $BaseBranch,
    '--head', $head,
    '--title', $Message,
    '--body', ($body -join [Environment]::NewLine)
)

if ($Draft) {
    $prArgs += '--draft'
}

Invoke-Native 'Create PR' 'gh' $prArgs
