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
    [string]$LocalBranch = 'local/develop',
    [string]$WorktreeRoot,
    [switch]$Draft,
    [switch]$SkipTests,
    [switch]$NoPr,
    [switch]$NoBdPush,
    [switch]$PromoteLocal,
    [switch]$RemoveWorktree,
    [switch]$TakeNext,
    [ValidateRange(1, 32)]
    [int]$NextCount = 1
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
    & $File @Arguments | ForEach-Object { Write-Host $_ }
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

function Test-GitRef {
    param([string]$Ref)

    & git show-ref --verify --quiet $Ref
    return $LASTEXITCODE -eq 0
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

function Invoke-TakeNextIfRequested {
    param([string]$Root = $repoRoot)

    if (-not $TakeNext) {
        return
    }

    $takeWorkScript = Join-Path $Root 'scripts/take-work.ps1'
    if (-not (Test-Path -LiteralPath $takeWorkScript)) {
        throw "Cannot take next work; missing script: $takeWorkScript"
    }

    Invoke-Native 'Take next ready work' 'pwsh' @($takeWorkScript, '-Count', [string]$NextCount)
}

function Update-RequiredSubmodulesIfNeeded {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.gitmodules'))) {
        return
    }

    $deepCwModel = Join-Path $repoRoot 'zeus-web/external/deepcw-engine/model.onnx'
    if (-not (Test-Path -LiteralPath $deepCwModel)) {
        Invoke-Native 'Update DeepCW model submodule' 'git' @(
            'submodule',
            'update',
            '--init',
            '--',
            'zeus-web/external/deepcw-engine'
        )
    }
}

function Invoke-PromoteLocalIfRequested {
    if (-not $PromoteLocal) {
        return $repoRoot
    }

    $primary = Resolve-Path -LiteralPath (Get-PrimaryWorktree)
    $primaryPath = $primary.ProviderPath
    $primaryStatus = @(git -C $primaryPath status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$primaryPath' status failed with exit code $LASTEXITCODE"
    }

    if ($primaryStatus.Count -gt 0) {
        throw "Primary checkout has uncommitted changes; cannot promote into $LocalBranch`: $primaryPath"
    }

    Invoke-Native "Fetch $BaseRemote/$BaseBranch for local trunk" 'git' @('-C', $primaryPath, 'fetch', $BaseRemote, $BaseBranch)
    $baseRef = "$BaseRemote/$BaseBranch"

    if (Test-GitRef "refs/heads/$LocalBranch") {
        Invoke-Native "Switch primary checkout to $LocalBranch" 'git' @('-C', $primaryPath, 'switch', $LocalBranch)
        Invoke-Native "Rebase $LocalBranch onto $baseRef" 'git' @('-C', $primaryPath, 'rebase', $baseRef)
    }
    else {
        Invoke-Native "Create $LocalBranch from $baseRef" 'git' @('-C', $primaryPath, 'switch', '-c', $LocalBranch, $baseRef)
    }

    Invoke-Native "Merge $branch into $LocalBranch" 'git' @('-C', $primaryPath, 'merge', '--no-edit', $branch)
    return $primaryPath
}

function Remove-CurrentWorktreeIfRequested {
    param([string]$PostActionRoot)

    if (-not $RemoveWorktree) {
        return $PostActionRoot
    }

    $primary = Resolve-Path -LiteralPath (Get-PrimaryWorktree)
    $primaryPath = $primary.ProviderPath
    if ([System.IO.Path]::GetFullPath($primaryPath) -eq [System.IO.Path]::GetFullPath($repoRoot)) {
        throw "Refusing to remove the primary checkout as a worktree."
    }

    $rootForWorktrees = $WorktreeRoot
    if (-not $rootForWorktrees) {
        $rootForWorktrees = Join-Path (Split-Path -Parent $primaryPath) "$(Split-Path -Leaf $primaryPath).Worktrees"
    }

    if (-not (Test-PathInside -Path $repoRoot -Parent $rootForWorktrees)) {
        throw "Refusing to remove worktree outside expected root '$rootForWorktrees': $repoRoot"
    }

    Set-Location $primaryPath
    Invoke-Native "Remove finished worktree $branch" 'git' @('worktree', 'remove', '--force', $repoRoot)
    return $primaryPath
}

function Invoke-PostPrSuccess {
    $postActionRoot = Invoke-PromoteLocalIfRequested
    $postActionRoot = Remove-CurrentWorktreeIfRequested -PostActionRoot $postActionRoot
    Invoke-TakeNextIfRequested -Root $postActionRoot
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot
Set-GitHooksPath

$branch = (Invoke-GitCapture @('branch', '--show-current') | Select-Object -First 1)
Assert-FeatureBranch $branch

Write-Host "==> Finishing $branch" -ForegroundColor Cyan

if ($NoPr -and ($TakeNext -or $PromoteLocal -or $RemoveWorktree)) {
    throw "-TakeNext, -PromoteLocal, and -RemoveWorktree require PR creation; remove -NoPr or run the follow-up step separately."
}

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
    Update-RequiredSubmodulesIfNeeded
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
    Invoke-TakeNextIfRequested
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    if ($TakeNext -or $PromoteLocal -or $RemoveWorktree) {
        throw "Cannot run post-PR actions because GitHub CLI 'gh' is not installed or not on PATH; PR was not created."
    }

    Write-Warning "GitHub CLI 'gh' is not installed or not on PATH. Branch is pushed; create the PR manually into $BaseBranch."
    exit 0
}

$owner = Get-RemoteOwner $PushRemote
$head = if ($owner) { "$owner`:$branch" } else { $branch }
$existingPr = & gh pr list --repo $PrRepo --head $branch --json url --jq '.[0].url' 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingPr)) {
    Write-Host "==> PR already exists: $existingPr" -ForegroundColor Green
    Invoke-PostPrSuccess
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
Invoke-PostPrSuccess
