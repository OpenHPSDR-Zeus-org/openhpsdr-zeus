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
# OpenhpsdrZeus.exe is built GUI-subsystem (WinExe, so no console window pops up
# on normal launch). That breaks the obvious `& exe 2>&1; $LASTEXITCODE` probe:
# PowerShell's call operator does NOT wait for a GUI-subsystem process and
# cannot capture its console, so BOTH the output and the exit code came back
# EMPTY — every attempt looked like an exit-code-less failure even when the
# bridge was fine. Start-Process -Wait reliably blocks until the GUI process
# exits and -PassThru yields the real .ExitCode, while -RedirectStandard*
# captures the exe's stdout/stderr (the managed verdict + any exception) to
# files we can read and print.
#
# Retry a few times so a one-off native-load hiccup doesn't red the whole
# nightly, but keep EVERY attempt's output visible and still fail loudly if all
# attempts fail: a real regression fails all 3 with the real exception printed,
# and a flake that needed a retry emits a ::warning:: in the run summary.
$exe = Join-Path $publishPath "OpenhpsdrZeus.exe"
$tmp = [System.IO.Path]::GetTempPath()
$outFile = Join-Path $tmp "zeus-vst-verify-out-$Arch.txt"
$errFile = Join-Path $tmp "zeus-vst-verify-err-$Arch.txt"
$maxAttempts = 3
$probeExit = 1
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    Remove-Item -LiteralPath $outFile, $errFile -ErrorAction SilentlyContinue

    $spArgs = @{
        FilePath               = $exe
        ArgumentList           = '--verify-vst-bridge'
        Wait                   = $true
        PassThru               = $true
        NoNewWindow            = $true
        RedirectStandardOutput = $outFile
        RedirectStandardError  = $errFile
    }
    $proc = Start-Process @spArgs
    $probeExit = $proc.ExitCode

    $probeOutput = @()
    foreach ($f in @($outFile, $errFile)) {
        if (Test-Path -LiteralPath $f) {
            $probeOutput += (Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)
        }
    }
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
