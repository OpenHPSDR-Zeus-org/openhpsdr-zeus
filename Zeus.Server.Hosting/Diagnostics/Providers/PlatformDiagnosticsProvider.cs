// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider exposing the host platform/runtime fingerprint and WDSP native
// load state. This is the "what am I running on, and did the native DSP load
// here?" provider — the first thing to check when debugging an
// architecture-specific issue (macOS/Windows/Linux, x64/arm64, Raspberry Pi).
// Pure read-only reflection over RuntimeInformation + the WDSP static probes.

using System.Runtime.InteropServices;
using Zeus.Dsp.Wdsp;

namespace Zeus.Server.Diagnostics;

public sealed class PlatformDiagnosticsProvider : IDiagnosticsProvider
{
    public string Id => "system.platform";
    public string RouteSegment => "platform";
    public string Category => "system";
    public int SchemaVersion => 1;
    public string Description => "Host OS/architecture/runtime fingerprint and WDSP native load state.";

    public object Snapshot()
    {
        bool wdspLoadable = SafeProbe(() => WdspDspEngine.NativeLibraryLoadable);
        return new
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow,
            os = new
            {
                description = RuntimeInformation.OSDescription,
                platform = OsPlatformName(),
                isWindows = OperatingSystem.IsWindows(),
                isMacOS = OperatingSystem.IsMacOS(),
                isLinux = OperatingSystem.IsLinux(),
                architecture = RuntimeInformation.OSArchitecture.ToString(),
            },
            process = new
            {
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                is64Bit = Environment.Is64BitProcess,
                processorCount = Environment.ProcessorCount,
                pid = Environment.ProcessId,
                workingSetMb = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1),
                gcHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1),
                uptimeMs = Environment.TickCount64,
            },
            runtime = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                dotnetVersion = Environment.Version.ToString(),
            },
            wdsp = new
            {
                nativeLoadable = wdspLoadable,
                emnrPost2Available = wdspLoadable && SafeProbe(() => WdspDspEngine.EmnrPost2Available),
                nr4SbnrAvailable = wdspLoadable && SafeProbe(() => WdspDspEngine.Nr4SbnrAvailable),
            },
        };
    }

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("wdsp-native-loadable",
            "WDSP native library loads on this platform/architecture.", DiagnosticsSeverity.Warn,
            _ => SafeProbe(() => WdspDspEngine.NativeLibraryLoadable)
                ? new SelfCheckResult(SelfCheckOutcome.Pass,
                    $"WDSP native loaded ({RuntimeInformation.RuntimeIdentifier}).", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Warn,
                    $"WDSP native NOT loadable on {RuntimeInformation.RuntimeIdentifier}; DSP falls back to synthetic.", DateTimeOffset.UtcNow)),

        new DiagnosticsSelfCheck("architecture-known",
            "Process architecture is a known/supported value.", DiagnosticsSeverity.Info,
            _ => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64 or Architecture.X86 or Architecture.Arm
                ? new SelfCheckResult(SelfCheckOutcome.Pass,
                    $"arch={RuntimeInformation.ProcessArchitecture}", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Warn,
                    $"unexpected arch={RuntimeInformation.ProcessArchitecture}", DateTimeOffset.UtcNow)),
    };

    private static string OsPlatformName() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    private static bool SafeProbe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }
}
