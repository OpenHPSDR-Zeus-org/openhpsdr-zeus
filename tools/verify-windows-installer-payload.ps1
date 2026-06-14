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

# Validate the managed VST bridge resolver against the installed layout. This
# catches the easy-to-miss case where the DLL is present under runtimes/<rid>/
# native but P/Invoke still searches only next to the executable or on PATH.
try {
    $hostAssembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $publishPath "Zeus.Plugins.Host.dll"))
    $abiType = $hostAssembly.GetType("Zeus.Plugins.Host.Audio.VstBridgeAbi", $true)
    $bridgeType = $hostAssembly.GetType("Zeus.Plugins.Host.Audio.VstBridgeNative", $true)
    $abi = [int]$abiType.GetField("Current").GetRawConstantValue()
    $bridge = [Activator]::CreateInstance($bridgeType)
    $initStatus = [int]$bridgeType.GetMethod("Init").Invoke($bridge, [object[]]@($abi))
    if ($initStatus -ne 0) {
        throw "VST bridge init returned status $initStatus"
    }
    [void]$bridgeType.GetMethod("Shutdown").Invoke($bridge, [object[]]@())
}
catch {
    $ex = $_.Exception
    if ($ex -is [System.Reflection.TargetInvocationException] -and $ex.InnerException) {
        $ex = $ex.InnerException
    }
    throw "Managed VST bridge failed to load from installer payload: $($ex.Message)"
}

Write-Host "Windows installer payload verified: $publishPath"
