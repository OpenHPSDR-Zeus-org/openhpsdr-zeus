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
# The win-x64 bridge load fails INTERMITTENTLY in CI (the same commit has both
# passed and failed on different nightlies) — a transient native-load hiccup,
# not a deterministic regression. Retry a few times so a one-off flake doesn't
# red the whole nightly, but keep EVERY attempt's output visible and still fail
# loudly if all attempts fail. That absorbs the flake without masking a genuine,
# persistent bridge-load problem: a real regression fails all 3 with the real
# exception printed, and a flake that needed a retry emits a ::warning:: so it
# stays visible in the run summary.
#
# Always echo the probe's own output verbatim — PowerShell's `throw` renders a
# truncated single-line view that previously swallowed the one diagnostic that
# matters (the managed exception type the published exe writes to stderr).
$maxAttempts = 3
$probeExit = 1
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    $probeOutput = & (Join-Path $publishPath "OpenhpsdrZeus.exe") --verify-vst-bridge 2>&1
    $probeExit = $LASTEXITCODE

    if ($probeOutput) {
        Write-Host "--- OpenhpsdrZeus --verify-vst-bridge output (attempt $attempt/$maxAttempts, exit $probeExit) ---"
        $probeOutput | ForEach-Object { Write-Host $_ }
        Write-Host "--- end probe output ---"
    }

    if ($probeExit -eq 0) {
        if ($attempt -gt 1) {
            Write-Host "::warning::VST bridge verify passed only on attempt $attempt/$maxAttempts — intermittent win-x64 bridge-load flake (see probe output above)."
        }
        break
    }

    if ($attempt -lt $maxAttempts) {
        Write-Host "VST bridge verify attempt $attempt/$maxAttempts failed (exit $probeExit); retrying in 3s..."
        Start-Sleep -Seconds 3
    }
}

if ($probeExit -ne 0) {
    throw "Managed VST bridge failed to load from installer payload after $maxAttempts attempts (probe exit $probeExit; see probe output above)."
}

Write-Host "Windows installer payload verified: $publishPath"
