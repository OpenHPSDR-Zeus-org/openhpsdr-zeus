# SPDX-License-Identifier: GPL-2.0-or-later
#
# Claim ready beads work and prepare one or more isolated Zeus worktrees.
#
# Examples:
#   pwsh scripts/take-work.ps1 -Count 3 -Unassigned
#   pwsh scripts/take-work.ps1 -Issue zeus-123
#   pwsh scripts/take-work.ps1 -Type bug -Priority 1

[CmdletBinding()]
param(
    [ValidateRange(1, 32)]
    [int]$Count = 1,

    [string[]]$Issue,

    [ValidateSet('auto', 'feature', 'feat', 'fix', 'bug', 'bugfix', 'chore', 'docs')]
    [string]$Kind = 'auto',

    [string]$Type,
    [int]$Priority = -1,
    [string[]]$Label,
    [string[]]$ExcludeLabel,
    [string[]]$ExcludeType,
    [switch]$Unassigned,

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

function Invoke-NativeCapture {
    param(
        [string]$File,
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & $File @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$File $($Arguments -join ' ') failed with exit code $exitCode"
    }

    return [pscustomobject]@{
        Output = @($output)
        ExitCode = $exitCode
    }
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

function Get-BranchName {
    param(
        [string]$BranchKind,
        [string]$IssueId,
        [string]$Title
    )

    $nameSlug = ConvertTo-Slug $Title
    if ($IssueId) {
        $issueSlug = ConvertTo-Slug $IssueId
        $nameSlug = "$issueSlug-$nameSlug"
    }

    return "$BranchKind/$nameSlug"
}

function Get-WorkName {
    param([string]$Title)

    $slug = ConvertTo-Slug $Title
    if ($slug.Length -le 72) {
        return $slug
    }

    return $slug.Substring(0, 72).Trim('-')
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

function Get-IssueDetails {
    param([string]$IssueId)

    if (-not (Get-Command bd -ErrorAction SilentlyContinue)) {
        throw "bd is required to resolve beads issue '$IssueId'."
    }

    $result = Invoke-NativeCapture 'bd' @('show', $IssueId, '--json')
    return Select-FirstJsonObject (ConvertFrom-JsonOutput $result.Output "bd show $IssueId")
}

function Get-ReadyIssues {
    param(
        [int]$Limit = 1,
        [switch]$Claim
    )

    if (-not (Get-Command bd -ErrorAction SilentlyContinue)) {
        throw "bd is required to inspect ready work."
    }

    $arguments = @('ready')
    if ($Claim) {
        $arguments += '--claim'
    }

    $arguments += @('--json', '--limit', [string]$Limit)
    if ($Type) {
        $arguments += @('--type', $Type)
    }

    if ($Priority -ge 0) {
        $arguments += @('--priority', [string]$Priority)
    }

    if ($Unassigned) {
        $arguments += '--unassigned'
    }

    foreach ($item in @($Label)) {
        if ($item) {
            $arguments += @('--label', $item)
        }
    }

    foreach ($item in @($ExcludeLabel)) {
        if ($item) {
            $arguments += @('--exclude-label', $item)
        }
    }

    foreach ($item in @($ExcludeType)) {
        if ($item) {
            $arguments += @('--exclude-type', $item)
        }
    }

    $result = Invoke-NativeCapture 'bd' $arguments -AllowFailure
    if ($result.ExitCode -ne 0) {
        $details = ($result.Output -join [Environment]::NewLine).Trim()
        if ($details) {
            throw "bd $($arguments -join ' ') failed with exit code $($result.ExitCode): $details"
        }

        throw "bd $($arguments -join ' ') failed with exit code $($result.ExitCode)"
    }

    $json = ConvertFrom-JsonOutput $result.Output "bd $($arguments -join ' ')"
    if ($null -eq $json) {
        return @()
    }

    return @($json)
}

function Start-Lane {
    param(
        $IssueDetails,
        [bool]$AlreadyClaimed
    )

    $issueId = [string]$IssueDetails.id
    $title = [string]$IssueDetails.title
    $issueType = [string]$IssueDetails.issue_type
    if ([string]::IsNullOrWhiteSpace($title)) {
        $title = $issueId
    }

    $workName = Get-WorkName $title
    $branchKind = Resolve-BranchKind -IssueType $issueType -RequestedKind $Kind
    $branch = Get-BranchName -BranchKind $branchKind -IssueId $issueId -Title $workName
    $startScript = Join-Path $repoRoot 'scripts/start-work.ps1'

    $arguments = @(
        $branchKind,
        $workName,
        '-Issue', $issueId,
        '-BaseRemote', $BaseRemote,
        '-BaseBranch', $BaseBranch
    )

    if ($WorktreeRoot) {
        $arguments += @('-WorktreeRoot', $WorktreeRoot)
    }

    if ($NoFetch) {
        $arguments += '-NoFetch'
    }

    if ($DryRun) {
        $arguments += '-DryRun'
    }

    if ($AlreadyClaimed) {
        $arguments += '-NoClaim'
    }

    $pwshArguments = @($startScript) + $arguments
    Invoke-Native "Prepare $issueId" 'pwsh' $pwshArguments

    return [pscustomobject]@{
        Issue = $issueId
        Title = $title
        Type = $issueType
        Branch = $branch
        Worktree = Get-WorktreeForBranch $branch
    }
}

$repoRoot = (Invoke-GitCapture @('rev-parse', '--show-toplevel') | Select-Object -First 1)
Set-Location $repoRoot

$workItems = @()
if ($Issue -and $Issue.Count -gt 0) {
    foreach ($issueId in $Issue) {
        $details = Get-IssueDetails $issueId
        if ($null -eq $details) {
            throw "Issue '$issueId' was not found."
        }

        $workItems += [pscustomobject]@{
            Details = $details
            AlreadyClaimed = $false
        }
    }
}
else {
    if ($DryRun) {
        Write-Host "DRY RUN: bd ready --claim would reserve each selected issue atomically." -ForegroundColor Yellow
        foreach ($readyIssue in @(Get-ReadyIssues -Limit $Count)) {
            $workItems += [pscustomobject]@{
                Details = $readyIssue
                AlreadyClaimed = $true
            }
        }

        if ($workItems.Count -eq 0) {
            Write-Warning "No ready beads work matched the requested filters."
        }
    }
    else {
        for ($i = 0; $i -lt $Count; $i++) {
            $claimed = Select-FirstJsonObject @(Get-ReadyIssues -Limit 1 -Claim)
            if ($null -eq $claimed) {
                Write-Warning "No ready beads work matched the requested filters."
                break
            }

            $workItems += [pscustomobject]@{
                Details = $claimed
                AlreadyClaimed = $true
            }
        }
    }
}

if ($workItems.Count -eq 0) {
    throw "No work was selected."
}

$lanes = @()
foreach ($item in $workItems) {
    $lanes += Start-Lane -IssueDetails $item.Details -AlreadyClaimed $item.AlreadyClaimed
}

Write-Host "==> Ready lanes" -ForegroundColor Green
foreach ($lane in $lanes) {
    Write-Host "  $($lane.Issue): $($lane.Title)"
    Write-Host "    branch:   $($lane.Branch)"
    if ($lane.Worktree) {
        Write-Host "    worktree: $($lane.Worktree)"
        Write-Host "    finish:   pwsh scripts/finish-work.ps1 -Message '<commit subject>' -Issue $($lane.Issue) -TakeNext"
    }
}
