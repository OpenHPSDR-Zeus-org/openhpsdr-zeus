# SPDX-License-Identifier: GPL-2.0-or-later
#
# Update the primary checkout from develop and remove clean local worktrees
# whose branches have already merged into develop.
#
# Example:
#   pwsh scripts/cleanup-merged-worktrees.ps1

[CmdletBinding()]
param(
    [string]$BaseRemote = 'OpenHPSDR-Zeus-org',
    [string]$BaseBranch = 'develop',
    [string]$WorktreeRoot,
    [switch]$DryRun,
    [switch]$SkipPrimaryUpdate
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

function Get-WorktreeRecords {
    $records = @()
    $current = $null
    $lines = Invoke-GitCapture @('worktree', 'list', '--porcelain')

    foreach ($line in $lines) {
        if ($line.StartsWith('worktree ')) {
            if ($current) {
                $records += [pscustomobject]$current
            }

            $current = [ordered]@{
                Path = $line.Substring('worktree '.Length)
                Branch = $null
            }
            continue
        }

        if ($current -and $line.StartsWith('branch refs/heads/')) {
            $current.Branch = $line.Substring('branch refs/heads/'.Length)
        }
    }

    if ($current) {
        $records += [pscustomobject]$current
    }

    return $records
}

function Get-PrimaryWorktree {
    $records = Get-WorktreeRecords
    if ($records.Count -eq 0) {
        return (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
    }

    return $records[0].Path
}

function Test-BranchPrefix {
    param([string]$Branch)

    foreach ($prefix in @('feature/', 'feat/', 'fix/', 'bug/', 'bugfix/', 'chore/', 'docs/')) {
        if ($Branch.StartsWith($prefix)) {
            return $true
        }
    }

    return $false
}

function Test-PathInside {
    param(
        [string]$Path,
        [string]$Parent
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $resolvedPath.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot

$primary = Resolve-Path -LiteralPath (Get-PrimaryWorktree)
$primaryPath = $primary.ProviderPath

if (-not $WorktreeRoot) {
    $WorktreeRoot = Join-Path (Split-Path -Parent $primaryPath) "$(Split-Path -Leaf $primaryPath).Worktrees"
}

Invoke-Native "Fetch $BaseRemote/$BaseBranch" 'git' @('fetch', $BaseRemote, $BaseBranch)
$baseRef = "$BaseRemote/$BaseBranch"

if (-not $SkipPrimaryUpdate) {
    $primaryStatus = @(git -C $primaryPath status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$primaryPath' status failed with exit code $LASTEXITCODE"
    }

    if ($primaryStatus.Count -gt 0) {
        Write-Warning "Primary checkout has uncommitted changes; skipping develop update: $primaryPath"
    }
    elseif ($DryRun) {
        Write-Host "DRY RUN: git -C '$primaryPath' switch $BaseBranch"
        Write-Host "DRY RUN: git -C '$primaryPath' merge --ff-only $baseRef"
    }
    else {
        Invoke-Native "Switch primary checkout to $BaseBranch" 'git' @('-C', $primaryPath, 'switch', $BaseBranch)
        Invoke-Native "Fast-forward primary checkout" 'git' @('-C', $primaryPath, 'merge', '--ff-only', $baseRef)
    }
}

$mergedBranches = @(Invoke-GitCapture @('branch', '--format=%(refname:short)', '--merged', $baseRef))
$records = Get-WorktreeRecords

foreach ($record in $records) {
    if (-not $record.Branch) {
        continue
    }

    $branch = [string]$record.Branch
    if (-not (Test-BranchPrefix $branch)) {
        continue
    }

    if ($mergedBranches -notcontains $branch) {
        continue
    }

    if (-not (Test-PathInside -Path $record.Path -Parent $WorktreeRoot)) {
        continue
    }

    $status = @(git -C $record.Path status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$($record.Path)' status failed with exit code $LASTEXITCODE"
    }

    if ($status.Count -gt 0) {
        Write-Warning "Skipping dirty merged worktree for $branch`: $($record.Path)"
        continue
    }

    if ($DryRun) {
        Write-Host "DRY RUN: git worktree remove '$($record.Path)'"
        Write-Host "DRY RUN: git branch -d '$branch'"
        continue
    }

    Invoke-Native "Remove merged worktree $branch" 'git' @('worktree', 'remove', $record.Path)
    Invoke-Native "Delete merged branch $branch" 'git' @('branch', '-d', $branch)
}

Write-Host "==> Cleanup complete" -ForegroundColor Green
