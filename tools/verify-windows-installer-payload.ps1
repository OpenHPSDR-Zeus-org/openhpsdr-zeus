param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"

$publishPath = (Resolve-Path -LiteralPath $PublishDir).Path
$rid = "win-$Arch"
$nativeDir = Join-Path $publishPath "runtimes\$rid\native"

function Assert-File {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $publishPath $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer payload is missing required file: $RelativePath"
    }
    return (Get-Item -LiteralPath $path)
}

$requiredFiles = @(
    "OpenhpsdrZeus.exe",
    "OpenhpsdrZeus.dll",
    "OpenhpsdrZeus.deps.json",
    "Zeus.Plugins.Host.dll",
    "wwwroot\index.html",
    "wwwroot\sw.js",
    "zeus.ico",
    "zeus.png",
    "zetaHat.bin",
    "calculus",
    "runtimes\$rid\native\wdsp.dll",
    "runtimes\$rid\native\miniaudio.dll",
    "runtimes\$rid\native\zeus-vst-bridge.dll"
)

foreach ($file in $requiredFiles) {
    Assert-File $file | Out-Null
}

$processArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($processArch -ne $Arch) {
    Write-Host "Skipping native load checks: process arch is $processArch, payload arch is $Arch."
    exit 0
}

foreach ($nativeName in @("wdsp.dll", "miniaudio.dll", "zeus-vst-bridge.dll")) {
    $nativePath = Join-Path $nativeDir $nativeName
    $handle = [IntPtr]::Zero
    if (-not [System.Runtime.InteropServices.NativeLibrary]::TryLoad($nativePath, [ref]$handle)) {
        throw "Native DLL exists but cannot be loaded: $nativePath"
    }
    [System.Runtime.InteropServices.NativeLibrary]::Free($handle)
}

# Validate the managed VST bridge resolver against the installed layout by
# running the published app itself. Loading the net10.0 host assembly inside
# PowerShell uses PowerShell's runtime context instead of the publish payload's
# runtime context, which can fail before the VST resolver is exercised.
$probeOutput = & (Join-Path $publishPath "OpenhpsdrZeus.exe") --verify-vst-bridge 2>&1
$probeExit = $LASTEXITCODE

# Always echo the probe's own output verbatim. PowerShell's `throw` renders a
# truncated single-line view of the message, which previously swallowed the one
# diagnostic that matters (the managed exception type + message the published
# exe writes to stderr). Print it plainly so CI logs show the real cause.
if ($probeOutput) {
    Write-Host "--- OpenhpsdrZeus --verify-vst-bridge output (exit $probeExit) ---"
    $probeOutput | ForEach-Object { Write-Host $_ }
    Write-Host "--- end probe output ---"
}

if ($probeExit -ne 0) {
    throw "Managed VST bridge failed to load from installer payload (probe exit $probeExit; see probe output above)."
}

Write-Host "Windows installer payload verified: $publishPath"
