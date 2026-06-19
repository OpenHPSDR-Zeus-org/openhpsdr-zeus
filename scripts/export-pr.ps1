# SPDX-License-Identifier: GPL-2.0-or-later
#
# Export local trunk commit(s) into a clean PR branch based on org develop.
#
# Examples:
#   pwsh scripts/export-pr.ps1 -Commit HEAD -Name "rx meter guard" -Issue zeus-123
#   pwsh scripts/export-pr.ps1 -Range "HEAD~2..HEAD" -Kind fix -Name "audio underrun probe"

[CmdletBinding()]
param(
    [string]$Commit = 'HEAD',
    [string]$Range,
    [string]$Name,
    [string]$Issue,

    [ValidateSet('auto', 'feature', 'feat', 'fix', 'bug', 'bugfix', 'chore', 'docs')]
    [string]$Kind = 'auto',

    [string]$Message,
    [string]$PushRemote = 'origin',
    [string]$BaseRemote = 'OpenHPSDR-Zeus-org',
    [string]$BaseBranch = 'develop',
    [string]$PrRepo = 'OpenHPSDR-Zeus-org/openhpsdr-zeus',
    [string]$WorktreeRoot,
    [switch]$Draft,
    [switch]$SkipTests,
    [switch]$NoPr,
    [switch]$NoBdPush,
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

function ConvertFrom-JsonOutput {
    param(
        [string[]]$Lines,
        [string]$Context
    )

    $text = ($Lines -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($text) -or $text -eq '[]') {
        return $null
    }

    try {
        return $text | ConvertFrom-Json
    }
    catch {
        throw "Could not parse JSON from $Context`: $($_.Exception.Message)"
    }
}

function Select-FirstJsonObject {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $items = @($Value)
    if ($items.Count -eq 0) {
        return $null
    }

    return $items[0]
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

function Get-WorkName {
    param([string]$Text)

    $slug = ConvertTo-Slug $Text
    if ($slug.Length -le 72) {
        return $slug
    }

    return $slug.Substring(0, 72).Trim('-')
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

function Resolve-BranchKind {
    param(
        [string]$IssueType,
        [string]$RequestedKind
    )

    if ($RequestedKind -ne 'auto') {
        return (Get-BranchPrefix $RequestedKind)
    }

    switch ($IssueType) {
        'bug' { return 'fix' }
        'chore' { return 'chore' }
        default { return 'feature' }
    }
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

function Get-IssueDetails {
    param([string]$IssueId)

    if (-not (Get-Command bd -ErrorAction SilentlyContinue)) {
        return $null
    }

    $output = & bd show $IssueId --json
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return Select-FirstJsonObject (ConvertFrom-JsonOutput @($output) "bd show $IssueId")
}

function Get-CommitList {
    if ($Range) {
        return @(Invoke-GitCapture @('rev-list', '--reverse', $Range))
    }

    return @((Invoke-GitCapture @('rev-parse', '--verify', "$Commit^{commit}") | Select-Object -First 1))
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot

$status = @(Invoke-GitCapture @('status', '--porcelain'))
if ($status.Count -gt 0 -and -not $DryRun) {
    throw "Working tree has uncommitted changes; commit before exporting a PR branch."
}
elseif ($status.Count -gt 0) {
    Write-Warning "Working tree has uncommitted changes; dry run will only describe committed refs."
}

$commits = @(Get-CommitList)
if ($commits.Count -eq 0) {
    throw "No commits matched the requested export range."
}

$firstCommit = $commits[0]
$commitSubject = (Invoke-GitCapture @('log', '-1', '--format=%s', $firstCommit) | Select-Object -First 1)
$issueDetails = if ($Issue) { Get-IssueDetails $Issue } else { $null }
$issueType = if ($issueDetails) { [string]$issueDetails.issue_type } else { '' }
$sourceName = if ($Name) {
    $Name
}
elseif ($issueDetails -and $issueDetails.title) {
    [string]$issueDetails.title
}
else {
    $commitSubject
}

$workName = Get-WorkName $sourceName
$branchKind = Resolve-BranchKind -IssueType $issueType -RequestedKind $Kind
$branchSlug = $workName
if ($Issue) {
    $branchSlug = "$(ConvertTo-Slug $Issue)-$branchSlug"
}

$branch = "$branchKind/$branchSlug"
$worktreeName = $branch -replace '[\\/:-]+', '_'
$baseRef = "$BaseRemote/$BaseBranch"

if (-not $Message) {
    $Message = $commitSubject
}

if (-not $WorktreeRoot) {
    $primary = Resolve-Path -LiteralPath (Get-PrimaryWorktree)
    $primaryPath = $primary.ProviderPath
    $WorktreeRoot = Join-Path (Split-Path -Parent $primaryPath) "$(Split-Path -Leaf $primaryPath).Worktrees"
}

$exportPath = Join-Path $WorktreeRoot $worktreeName

Write-Host "==> Export branch: $branch" -ForegroundColor Cyan
Write-Host "==> Export worktree: $exportPath" -ForegroundColor Cyan
Write-Host "==> Commits: $($commits.Count)" -ForegroundColor Cyan

if ($DryRun) {
    if (-not $NoFetch) {
        Write-Host "DRY RUN: git fetch $BaseRemote $BaseBranch"
    }

    Write-Host "DRY RUN: git worktree add -b '$branch' '$exportPath' '$baseRef'"
    foreach ($sha in $commits) {
        Write-Host "DRY RUN: git -C '$exportPath' cherry-pick $sha"
    }

    Write-Host "DRY RUN: pwsh scripts/finish-work.ps1 -Message '$Message'$(if ($Issue) { " -Issue $Issue" })"
    exit 0
}

if (-not $NoPr -and -not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI 'gh' is required to create the PR. Install gh or pass -NoPr."
}

if (-not $NoFetch) {
    Invoke-Native "Fetch $BaseRemote/$BaseBranch" 'git' @('fetch', $BaseRemote, $BaseBranch)
}

if (-not (Test-GitRef "refs/remotes/$baseRef")) {
    throw "Base ref '$baseRef' is not available. Check the remote name or run git fetch."
}

if (Test-GitRef "refs/heads/$branch") {
    throw "Local branch '$branch' already exists. Pick a different -Name or delete/update the existing export branch."
}

if (Test-Path -LiteralPath $exportPath) {
    throw "Export worktree path already exists: $exportPath"
}

New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null
Invoke-Native "Create export worktree from $baseRef" 'git' @('worktree', 'add', '-b', $branch, $exportPath, $baseRef)

try {
    foreach ($sha in $commits) {
        Invoke-Native "Cherry-pick $sha" 'git' @('-C', $exportPath, 'cherry-pick', $sha)
    }

    $finishScript = Join-Path $exportPath 'scripts/finish-work.ps1'
    $finishArgs = @(
        $finishScript,
        '-Message', $Message,
        '-PushRemote', $PushRemote,
        '-BaseRemote', $BaseRemote,
        '-BaseBranch', $BaseBranch,
        '-PrRepo', $PrRepo
    )

    if ($Issue) {
        $finishArgs += @('-Issue', $Issue)
    }

    if ($Draft) {
        $finishArgs += '-Draft'
    }

    if ($SkipTests) {
        $finishArgs += '-SkipTests'
    }

    if ($NoPr) {
        $finishArgs += '-NoPr'
    }

    if ($NoBdPush) {
        $finishArgs += '-NoBdPush'
    }

    Invoke-Native 'Finish export branch' 'pwsh' $finishArgs
}
catch {
    Write-Warning "Export worktree left for inspection: $exportPath"
    throw
}

Write-Host "==> Export complete" -ForegroundColor Green
Write-Host "  Branch: $branch"
Write-Host "  Worktree: $exportPath"
