param(
    [string]$RuntimeRoot = "Zeus.Dsp/runtimes",
    [string]$ReportPath = "",
    [switch]$FailOnMissingWinX64Nr5,
    [switch]$JsonOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path (Get-Location).Path $Path
}

function ConvertTo-RelativePath {
    param([string]$Path)
    $root = (Get-Location).Path
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if ($resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $resolved.Substring($root.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    }
    return $resolved
}

function Test-BinaryContainsAscii {
    param(
        [string]$Path,
        [string]$Needle
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    return $text.Contains($Needle)
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ExpectedNativeName {
    param([string]$Rid)
    if ($Rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) { return "wdsp.dll" }
    if ($Rid.StartsWith("linux-", [StringComparison]::OrdinalIgnoreCase)) { return "libwdsp.so" }
    if ($Rid.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) { return "libwdsp.dylib" }
    return "libwdsp"
}

function Get-ExpectedSideBySideDependencies {
    param([string]$Rid)
    if ($Rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        return @("libfftw3-3.dll", "libfftw3f-3.dll")
    }
    if ($Rid.StartsWith("linux-x64", [StringComparison]::OrdinalIgnoreCase)) {
        return @("libfftw3.so.3", "libfftw3f.so.3")
    }
    if ($Rid.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) {
        return @("libfftw3.3.dylib", "libfftw3f.3.dylib")
    }
    return @()
}

$runtimeRootPath = Resolve-RepoPath $RuntimeRoot
if (-not (Test-Path -LiteralPath $runtimeRootPath -PathType Container)) {
    throw "Runtime root not found: $RuntimeRoot"
}

$nr4Symbols = @("SetRXASBNRRun")
$nr5Symbols = @(
    "SetRXASPNRRun",
    "SetRXASPNRPosition",
    "SetRXASPNRAggressiveness",
    "SetRXASPNRAgcRun",
    "SetRXASPNRAgcTarget",
    "GetRXASPNRDiagnostics",
    "GetRXASPNRAdvancedDiagnostics",
    "GetRXASPNRDeepDiagnostics",
    "GetRXASPNRProbabilityDiagnostics",
    "GetRXASPNRPeakDiagnostics",
    "GetRXASPNRAgcDiagnostics",
    "GetRXASPNRMemoryDiagnostics"
)

$artifacts = New-Object System.Collections.Generic.List[object]
foreach ($ridDir in (Get-ChildItem -LiteralPath $runtimeRootPath -Directory | Sort-Object Name)) {
    $rid = $ridDir.Name
    $nativeDir = Join-Path $ridDir.FullName "native"
    $nativeName = Get-ExpectedNativeName $rid
    $nativePath = Join-Path $nativeDir $nativeName
    $nativePresent = Test-Path -LiteralPath $nativePath -PathType Leaf
    $nativeLength = if ($nativePresent) { (Get-Item -LiteralPath $nativePath).Length } else { 0 }
    $nativeSha256 = if ($nativePresent) { Get-FileSha256 $nativePath } else { "" }

    $nr4Present = @{}
    foreach ($symbol in $nr4Symbols) {
        $nr4Present[$symbol] = if ($nativePresent) { Test-BinaryContainsAscii $nativePath $symbol } else { $false }
    }

    $nr5Present = @{}
    foreach ($symbol in $nr5Symbols) {
        $nr5Present[$symbol] = if ($nativePresent) { Test-BinaryContainsAscii $nativePath $symbol } else { $false }
    }

    $expectedDeps = @(Get-ExpectedSideBySideDependencies $rid)
    $dependencyFiles = New-Object System.Collections.Generic.List[object]
    $missingDeps = New-Object System.Collections.Generic.List[string]
    foreach ($dep in $expectedDeps) {
        $depPath = Join-Path $nativeDir $dep
        $present = Test-Path -LiteralPath $depPath -PathType Leaf
        if (-not $present) { $missingDeps.Add($dep) | Out-Null }
        $depLength = if ($present) { (Get-Item -LiteralPath $depPath).Length } else { 0 }
        $depSha256 = if ($present) { Get-FileSha256 $depPath } else { "" }
        $dependencyFiles.Add([ordered]@{
            name = $dep
            present = $present
            path = if ($present) { ConvertTo-RelativePath $depPath } else { $null }
            length = $depLength
            sha256 = $depSha256
        }) | Out-Null
    }

    $nr4Ready = -not ($nr4Present.Values -contains $false)
    $nr5Ready = -not ($nr5Present.Values -contains $false)
    $depsReady = $missingDeps.Count -eq 0
    $status = if (-not $nativePresent) {
        "missing-native-artifact"
    } elseif (-not $nr4Ready) {
        "missing-nr4-symbols"
    } elseif (-not $nr5Ready) {
        "pending-nr5-rebuild"
    } elseif (-not $depsReady) {
        "missing-side-by-side-dependencies"
    } else {
        "ready"
    }

    $artifacts.Add([ordered]@{
        rid = $rid
        nativePath = if ($nativePresent) { ConvertTo-RelativePath $nativePath } else { ConvertTo-RelativePath $nativePath }
        nativePresent = $nativePresent
        nativeLength = $nativeLength
        nativeSha256 = $nativeSha256
        nr4Ready = $nr4Ready
        nr5Ready = $nr5Ready
        sideBySideDependenciesReady = $depsReady
        status = $status
        nr4Symbols = $nr4Present
        nr5Symbols = $nr5Present
        sideBySideDependencies = @($dependencyFiles.ToArray())
        missingSideBySideDependencies = @($missingDeps.ToArray())
    }) | Out-Null
}

$winX64 = @($artifacts.ToArray() | Where-Object { $_.rid -eq "win-x64" } | Select-Object -First 1)
$pending = @($artifacts.ToArray() | Where-Object { $_.status -ne "ready" } | ForEach-Object { $_.rid })
$report = [ordered]@{
    schemaVersion = 1
    tool = "audit-wdsp-runtime-artifacts"
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    runtimeRoot = ConvertTo-RelativePath $runtimeRootPath
    requiredNr4Symbols = $nr4Symbols
    requiredNr5Symbols = $nr5Symbols
    readyForWinX64Package = ($winX64.Count -gt 0 -and [bool]$winX64[0].nativePresent -and [bool]$winX64[0].nr4Ready -and [bool]$winX64[0].nr5Ready -and [bool]$winX64[0].sideBySideDependenciesReady)
    winX64NativePath = if ($winX64.Count -gt 0) { [string]$winX64[0].nativePath } else { "" }
    winX64NativeLength = if ($winX64.Count -gt 0) { [long]$winX64[0].nativeLength } else { 0 }
    winX64NativeSha256 = if ($winX64.Count -gt 0) { [string]$winX64[0].nativeSha256 } else { "" }
    pendingRidCount = $pending.Count
    pendingRids = $pending
    artifacts = @($artifacts.ToArray())
    recommendations = @(
        "Run tools/audit-wdsp-native-symbols.ps1 -BinaryPath Zeus.Dsp/runtimes/win-x64/native/wdsp.dll -RequireBinaryExports for PE export-table verification.",
        "Rebuild pending RIDs through the normal native artifact workflow before advertising NR5/SPNR support on those platforms.",
        "Keep FFTW side-by-side runtime libraries in each packaged native directory when WDSP is dynamically linked."
    )
}

if ($FailOnMissingWinX64Nr5 -and -not [bool]$report.readyForWinX64Package) {
    $json = $report | ConvertTo-Json -Depth 10
    if (-not $JsonOnly) { Write-Host $json }
    throw "win-x64 WDSP runtime artifact is not package-ready for NR4/NR5."
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportPathResolved = Resolve-RepoPath $ReportPath
    $dir = Split-Path -Parent $reportPathResolved
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPathResolved -Encoding UTF8
}

$output = $report | ConvertTo-Json -Depth 10
if ($JsonOnly) {
    $output
} else {
    Write-Host $output
}
