# SPDX-License-Identifier: GPL-2.0-or-later
#
# Create a scoped Zeus feature/fix worktree from develop.
#
# Examples:
#   pwsh scripts/start-work.ps1 feature "logbook search" -Issue zeus-123
#   pwsh scripts/start-work.ps1 fix "rx suite persistence"

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('feature', 'feat', 'fix', 'bug', 'bugfix', 'chore', 'docs')]
    [string]$Kind,

    [Parameter(Mandatory = $true, Position = 1)]
    [string]$Name,

    [string]$Issue,
    [string]$BaseRemote = 'OpenHPSDR-Zeus-org',
    [string]$BaseBranch = 'develop',
    [string]$WorktreeRoot,
    [switch]$NoFetch,
    [switch]$DryRun
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

function ConvertTo-Slug {
    param([string]$Text)

    $slug = $Text.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $slug = $slug.Trim('-') -replace '-{2,}', '-'
    if ([string]::IsNullOrWhiteSpace($slug)) {
        throw "Cannot derive a branch slug from '$Text'."
    }

    return $slug
}

function Test-GitRef {
    param([string]$Ref)

    & git show-ref --verify --quiet $Ref
    return $LASTEXITCODE -eq 0
}

function Get-PrimaryWorktree {
    $lines = Invoke-GitCapture @('worktree', 'list', '--porcelain')
    foreach ($line in $lines) {
        if ($line.StartsWith('worktree ')) {
            return $line.Substring('worktree '.Length)
        }
    }

    return (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
}

function Get-WorktreeForBranch {
    param([string]$Branch)

    $currentPath = $null
    $lines = Invoke-GitCapture @('worktree', 'list', '--porcelain')
    foreach ($line in $lines) {
        if ($line.StartsWith('worktree ')) {
            $currentPath = $line.Substring('worktree '.Length)
            continue
        }

        if ($line -eq "branch refs/heads/$Branch") {
            return $currentPath
        }
    }

    return $null
}

function Get-BranchPrefix {
    param([string]$RequestedKind)

    switch ($RequestedKind) {
        'feat' { return 'feature' }
        'bug' { return 'fix' }
        'bugfix' { return 'fix' }
        default { return $RequestedKind }
    }
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot
if (-not $DryRun) {
    Set-GitHooksPath
}

$prefix = Get-BranchPrefix $Kind
$nameSlug = ConvertTo-Slug $Name
if ($Issue) {
    $issueSlug = ConvertTo-Slug $Issue
    $nameSlug = "$issueSlug-$nameSlug"
}

$branch = "$prefix/$nameSlug"
$worktreeName = $branch -replace '[\\/:-]+', '_'

if (-not $WorktreeRoot) {
    $primary = Resolve-Path -LiteralPath (Get-PrimaryWorktree)
    $primaryPath = $primary.ProviderPath
    $WorktreeRoot = Join-Path (Split-Path -Parent $primaryPath) "$(Split-Path -Leaf $primaryPath).Worktrees"
}

$worktreePath = Join-Path $WorktreeRoot $worktreeName
$existingWorktree = Get-WorktreeForBranch $branch
$branchExists = Test-GitRef "refs/heads/$branch"

Write-Host "==> Branch: $branch" -ForegroundColor Cyan
Write-Host "==> Worktree: $worktreePath" -ForegroundColor Cyan

if ($existingWorktree) {
    Write-Host "Worktree already exists for ${branch}:" -ForegroundColor Yellow
    Write-Host "  $existingWorktree"
    Write-Host "Next:"
    Write-Host "  Set-Location '$existingWorktree'"
    exit 0
}

if (Test-Path -LiteralPath $worktreePath) {
    throw "Worktree path already exists: $worktreePath"
}

if ($DryRun) {
    if (-not $NoFetch) {
        Write-Host "DRY RUN: git fetch $BaseRemote $BaseBranch"
    }

    if ($branchExists) {
        Write-Host "DRY RUN: git worktree add '$worktreePath' '$branch'"
    }
    else {
        Write-Host "DRY RUN: git worktree add -b '$branch' '$worktreePath' '$BaseRemote/$BaseBranch'"
    }

    if ($Issue) {
        Write-Host "DRY RUN: bd update $Issue --claim"
    }

    exit 0
}

if (-not $NoFetch) {
    Invoke-Native "Fetch $BaseRemote/$BaseBranch" 'git' @('fetch', $BaseRemote, $BaseBranch)
}

$baseRef = "$BaseRemote/$BaseBranch"
if (-not (Test-GitRef "refs/remotes/$baseRef")) {
    throw "Base ref '$baseRef' is not available. Check the remote name or run git fetch."
}

New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null

if ($branchExists) {
    Invoke-Native "Create worktree for existing $branch" 'git' @('worktree', 'add', $worktreePath, $branch)
}
else {
    Invoke-Native "Create worktree from $baseRef" 'git' @('worktree', 'add', '-b', $branch, $worktreePath, $baseRef)
}

if ($Issue -and (Get-Command bd -ErrorAction SilentlyContinue)) {
    try {
        Invoke-Native "Claim beads issue $Issue" 'bd' @('update', $Issue, '--claim')
    }
    catch {
        Write-Warning "Could not claim beads issue '$Issue': $($_.Exception.Message)"
    }
}

Write-Host "==> Ready" -ForegroundColor Green
Write-Host "  Set-Location '$worktreePath'"
Write-Host "  pwsh scripts/finish-work.ps1 -Message '<commit subject>'$(if ($Issue) { " -Issue $Issue" })"
