param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",

    [string]$Configuration = "Release",

    [switch]$InitSubmodules
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$bridgeRoot = Join-Path $repoRoot "native\zeus-vst-bridge"
$rid = "win-$Arch"
$buildDir = Join-Path $bridgeRoot "build-$rid"
$cmakeArch = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
$stagedDir = Join-Path $repoRoot "Zeus.Plugins.Host\runtimes\$rid\native"
$builtDll = Join-Path $buildDir "$Configuration\zeus-vst-bridge.dll"

if ($InitSubmodules) {
    git -C $repoRoot submodule update --init native/zeus-vst-bridge/third_party/vst3sdk
    git -C (Join-Path $bridgeRoot "third_party\vst3sdk") submodule update --init base pluginterfaces public.sdk cmake
}

cmake -S $bridgeRoot -B $buildDir -G "Visual Studio 17 2022" -A $cmakeArch
cmake --build $buildDir --config $Configuration --parallel

if (-not (Test-Path -LiteralPath $builtDll -PathType Leaf)) {
    throw "VST bridge build did not produce $builtDll"
}

New-Item -ItemType Directory -Force -Path $stagedDir | Out-Null
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $stagedDir "zeus-vst-bridge.dll") -Force
Get-Item -LiteralPath (Join-Path $stagedDir "zeus-vst-bridge.dll") | Select-Object FullName, Length
