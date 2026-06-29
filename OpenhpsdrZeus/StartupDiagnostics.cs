// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Zeus.Server;

namespace OpenhpsdrZeus;

// Verbose startup preflight + phase tracer.
//
// Why this exists: in --desktop / --server mode the console is detached
// (FreeConsole), so when the backend or the Photino window fails to come up the
// process vanishes with no visible error — the classic "window flashes and
// closes, nothing happens" report. There was no durable record of *where* it
// died or *what* the machine was missing.
//
// StartupDiagnostics writes a complete, human-readable report to
// %LOCALAPPDATA%/Zeus/zeus-startup.log on EVERY launch:
//   - environment (OS edition, arch, .NET, paths, args),
//   - presence + loadability of every native dependency (WebView2, wdsp,
//     miniaudio, Photino, VC runtime),
//   - the shipped web assets and WDSP model files,
//   - prefs DB health and TCP port availability,
//   - crash-dump capture status + recent Windows crash events,
//   - a timestamped phase marker for each startup step.
//
// If startup then crashes, the last phase marker in the log pinpoints the
// failing step and the preflight above it shows what was missing. Hand the user
// a build, have them reproduce, and ask for this one file. Everything here is
// best-effort and never throws — a diagnostic must not be able to crash the very
// startup it is trying to diagnose.
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private const string ExeName = "OpenhpsdrZeus.exe";
    private const int CrashDumpRetention = 6;
    private static readonly string LaunchId = Guid.NewGuid().ToString("N");
    private static string? _logPath;
    private static string? _crashDumpDir;
    private static string? _launchMarkerPath;
    private static string? _phaseMarkerPath;
    private static string? _cleanExitPath;

    // The WebView2 Evergreen Runtime loader, shipped next to the binary by
    // Photino. Returns S_OK and a version string when a runtime is installed;
    // a failing HRESULT (or a DllNotFound when the loader itself is absent)
    // means WebView2 is missing — the #1 fresh-Windows cause of flash-and-close.
    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAvailableCoreWebView2BrowserVersionString(
        string? browserExecutableFolder, out IntPtr versionInfo);

    public static string LogPath
    {
        get
        {
            if (_logPath is not null) return _logPath;
            try { _logPath = Path.Combine(PrefsDbPath.DataDir, "zeus-startup.log"); }
            catch { _logPath = Path.Combine(Path.GetTempPath(), "zeus-startup.log"); }
            return _logPath;
        }
    }

    public static string CrashDumpDir
    {
        get
        {
            if (_crashDumpDir is not null) return _crashDumpDir;
            try { _crashDumpDir = Path.Combine(PrefsDbPath.DataDir, "crash-dumps"); }
            catch { _crashDumpDir = Path.Combine(Path.GetTempPath(), "Zeus", "crash-dumps"); }
            return _crashDumpDir;
        }
    }

    private static string LaunchMarkerPath
    {
        get
        {
            if (_launchMarkerPath is not null) return _launchMarkerPath;
            try { _launchMarkerPath = Path.Combine(PrefsDbPath.DataDir, "zeus-launch-active.txt"); }
            catch { _launchMarkerPath = Path.Combine(Path.GetTempPath(), "zeus-launch-active.txt"); }
            return _launchMarkerPath;
        }
    }

    private static string PhaseMarkerPath
    {
        get
        {
            if (_phaseMarkerPath is not null) return _phaseMarkerPath;
            try { _phaseMarkerPath = Path.Combine(PrefsDbPath.DataDir, "zeus-last-phase.txt"); }
            catch { _phaseMarkerPath = Path.Combine(Path.GetTempPath(), "zeus-last-phase.txt"); }
            return _phaseMarkerPath;
        }
    }

    private static string CleanExitPath
    {
        get
        {
            if (_cleanExitPath is not null) return _cleanExitPath;
            try { _cleanExitPath = Path.Combine(PrefsDbPath.DataDir, "zeus-last-clean-exit.txt"); }
            catch { _cleanExitPath = Path.Combine(Path.GetTempPath(), "zeus-last-clean-exit.txt"); }
            return _cleanExitPath;
        }
    }

    // Call once at the very top of Main. Opens/rotates the log, installs
    // last-resort exception handlers (so a crash on ANY thread is recorded —
    // not just the synchronous startup path), and dumps the full preflight.
    public static void Begin(string[] args)
    {
        LaunchMarker? previousLaunch = null;
        var previousPhase = "<none>";
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Directory.CreateDirectory(CrashDumpDir);

            previousLaunch = ReadLaunchMarker();
            previousPhase = ReadPhaseValue();
            if (previousLaunch is not null && !IsLikelyStillRunning(previousLaunch))
                SnapshotPreviousStartupLog(previousLaunch);

            // Keep the file from growing without bound across launches, but
            // preserve recent history for a test user reproducing intermittently.
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.Delete(LogPath);
            }
            catch { /* best effort */ }

            WriteLaunchMarker(args);
            WritePhaseMarker("startup: diagnostics begin");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => MarkCleanExit();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                LogException("AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")",
                    e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogException("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
        catch { /* never block launch on diagnostics setup */ }

        WriteHeader(args);
        ReportPreviousLaunch(previousLaunch, previousPhase);
        InstallNativeCrashDumpCapture();
        WritePreflight();
    }

    // Append a timestamped phase marker. The LAST marker in the log before a
    // crash is the failing step.
    public static void Phase(string name)
    {
        WritePhaseMarker(name);
        Write($"[phase] {name}");
    }

    public static void Log(string message) => Write(message);

    public static void LogException(string context, Exception? ex)
    {
        if (ex is null) { Write($"[error] {context}: <no exception object>"); return; }
        Write($"[error] {context}:\n{ex}");
    }

    private static void WriteLaunchMarker(string[] args)
    {
        try
        {
            var lines = new[]
            {
                $"launch.id={LaunchId}",
                $"started.utc={DateTimeOffset.UtcNow:O}",
                $"pid={Environment.ProcessId}",
                $"version={VersionString()}",
                $"args={string.Join(' ', args)}",
                $"base.dir={AppContext.BaseDirectory}",
                $"cwd={SafeCwd()}",
                $"log.path={LogPath}",
                $"phase.path={PhaseMarkerPath}",
                $"crash.dump.dir={CrashDumpDir}",
            };
            File.WriteAllLines(LaunchMarkerPath, lines);
        }
        catch { /* best effort */ }
    }

    private static void WritePhaseMarker(string phase)
    {
        try
        {
            var lines = new[]
            {
                $"launch.id={LaunchId}",
                $"phase.utc={DateTimeOffset.UtcNow:O}",
                $"pid={Environment.ProcessId}",
                $"phase={phase}",
            };
            File.WriteAllLines(PhaseMarkerPath, lines);
        }
        catch { /* best effort */ }
    }

    private static void MarkCleanExit()
    {
        try
        {
            var marker = ReadLaunchMarker();
            if (marker is null || marker.LaunchId != LaunchId) return;

            File.WriteAllLines(CleanExitPath, new[]
            {
                $"launch.id={LaunchId}",
                $"exited.utc={DateTimeOffset.UtcNow:O}",
                $"pid={Environment.ProcessId}",
                $"last.phase={ReadPhaseValue()}",
            });
            TryDelete(LaunchMarkerPath);
            TryDelete(PhaseMarkerPath);
            Write("[shutdown] clean exit marker written");
        }
        catch { /* process-exit diagnostics are best effort */ }
    }

    private static LaunchMarker? ReadLaunchMarker()
    {
        try
        {
            if (!File.Exists(LaunchMarkerPath)) return null;
            var values = ReadKeyValueFile(LaunchMarkerPath);
            return new LaunchMarker(
                values.GetValueOrDefault("launch.id") ?? string.Empty,
                values.GetValueOrDefault("started.utc") ?? string.Empty,
                int.TryParse(values.GetValueOrDefault("pid"), out var pid) ? pid : 0,
                values.GetValueOrDefault("version") ?? string.Empty,
                values.GetValueOrDefault("args") ?? string.Empty,
                values.GetValueOrDefault("base.dir") ?? string.Empty,
                values.GetValueOrDefault("cwd") ?? string.Empty,
                values.GetValueOrDefault("log.path") ?? LogPath,
                values.GetValueOrDefault("phase.path") ?? PhaseMarkerPath,
                values.GetValueOrDefault("crash.dump.dir") ?? CrashDumpDir);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadKeyValueFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            values[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return values;
    }

    private static bool IsLikelyStillRunning(LaunchMarker marker)
    {
        if (marker.Pid <= 0 || marker.Pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(marker.Pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void SnapshotPreviousStartupLog(LaunchMarker previous)
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            Directory.CreateDirectory(CrashDumpDir);
            var stamp = SanitizeStamp(previous.StartedUtc);
            var snapshot = Path.Combine(CrashDumpDir, $"zeus-startup-unclean-{stamp}.log");
            File.Copy(LogPath, snapshot, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static void ReportPreviousLaunch(LaunchMarker? previous, string previousPhase)
    {
        Section("previous launch");
        if (previous is null)
        {
            Kv("previous.status", "no active launch marker found");
            return;
        }

        if (IsLikelyStillRunning(previous))
        {
            Kv("previous.status", $"marker belongs to running process pid={previous.Pid}");
            return;
        }

        Kv("previous.status", "UNCLEAN shutdown or crash detected");
        Kv("previous.started", string.IsNullOrWhiteSpace(previous.StartedUtc) ? "<unknown>" : previous.StartedUtc);
        Kv("previous.pid", previous.Pid.ToString());
        Kv("previous.version", string.IsNullOrWhiteSpace(previous.Version) ? "<unknown>" : previous.Version);
        Kv("previous.args", string.IsNullOrWhiteSpace(previous.Args) ? "<none>" : previous.Args);
        Kv("previous.base.dir", string.IsNullOrWhiteSpace(previous.BaseDir) ? "<unknown>" : previous.BaseDir);
        Kv("previous.cwd", string.IsNullOrWhiteSpace(previous.Cwd) ? "<unknown>" : previous.Cwd);
        Kv("previous.last.phase", previousPhase);
        Kv("previous.log.snapshot", Path.Combine(CrashDumpDir, $"zeus-startup-unclean-{SanitizeStamp(previous.StartedUtc)}.log"));
        Kv("recovery.hint", "collect zeus-startup.log plus crash-dumps; if launch loops, move zeus-prefs.db aside before retrying");
    }

    private static string ReadPhaseValue()
    {
        try
        {
            if (!File.Exists(PhaseMarkerPath)) return "<none>";
            var values = ReadKeyValueFile(PhaseMarkerPath);
            var phase = values.GetValueOrDefault("phase") ?? "<unknown>";
            var at = values.GetValueOrDefault("phase.utc");
            return string.IsNullOrWhiteSpace(at) ? phase : $"{phase} at {at}";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string SanitizeStamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var dto))
            return DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return dto.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort */ }
    }

    private static void WriteHeader(string[] args)
    {
        Write("================================================================");
        Write($"Zeus startup  version={VersionString()} launch={LaunchId}");
        Write($"args=[{string.Join(' ', args)}]");
        Write("================================================================");
    }

    private static string VersionString() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private sealed record LaunchMarker(
        string LaunchId,
        string StartedUtc,
        int Pid,
        string Version,
        string Args,
        string BaseDir,
        string Cwd,
        string LogPath,
        string PhasePath,
        string CrashDumpDir);

    private static void WritePreflight()
    {
        Section("environment");
        Kv("os", RuntimeInformation.OSDescription);
        Kv("os.arch", RuntimeInformation.OSArchitecture.ToString());
        Kv("process.arch", RuntimeInformation.ProcessArchitecture.ToString());
        Kv("runtime", RuntimeInformation.FrameworkDescription);
        Kv("rid", RuntimeInformation.RuntimeIdentifier);
        Kv("machine", SafeMachineName());
        Kv("process.id", Environment.ProcessId.ToString());
        Kv("process.path", Environment.ProcessPath ?? "<unknown>");
        Kv("64bit.os", Environment.Is64BitOperatingSystem.ToString());
        Kv("64bit.process", Environment.Is64BitProcess.ToString());
        Kv("user.interactive", Environment.UserInteractive.ToString());
        Kv("base.dir", AppContext.BaseDirectory);
        Kv("cwd", SafeCwd());
        Kv("data.dir", SafeDataDir());
        Kv("startup.log", LogPath);
        Kv("launch.marker", LaunchMarkerPath);
        Kv("phase.marker", PhaseMarkerPath);
        Kv("clean.exit", CleanExitPath);
        Kv("localappdata", SafeFolder(Environment.SpecialFolder.LocalApplicationData));
        Kv("temp", Path.GetTempPath());
        Kv("disk.free.data", SafeFreeSpace(SafeDataDir()));
        Kv("disk.free.temp", SafeFreeSpace(Path.GetTempPath()));
        Kv("env.ZEUS_WEBROOT", Environment.GetEnvironmentVariable("ZEUS_WEBROOT") ?? "<unset>");
        Kv("env.ZEUS_PREFS_PATH", Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH") ?? "<unset>");
        Kv("env.ZEUS_DESKTOP_PORT", Environment.GetEnvironmentVariable("ZEUS_DESKTOP_PORT") ?? "<unset>");
        Kv("env.ZEUS_ENABLE_VST_LOAD", Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") ?? "<unset>");

        Section("crash diagnostics");
        ProbeCrashDumpState();
        ProbeRecentWindowsCrashEvents();

        Section("webview2 (desktop UI runtime)");
        ProbeWebView2();

        Section("native libraries");
        ProbeNativeNextToExe("Photino.Native.dll");
        ProbeNativeNextToExe("WebView2Loader.dll");
        ProbeRuntimeNative("wdsp");
        ProbeRuntimeNative("miniaudio");
        ProbeVcRuntime();

        Section("web assets + WDSP models");
        var baseDir = AppContext.BaseDirectory;
        var webRoot = Environment.GetEnvironmentVariable("ZEUS_WEBROOT");
        var wwwroot = string.IsNullOrWhiteSpace(webRoot) ? Path.Combine(baseDir, "wwwroot") : webRoot;
        ProbeFile("wwwroot", wwwroot);
        ProbeFile("wwwroot/index.html", Path.Combine(wwwroot, "index.html"));
        ProbeFile("zetaHat.bin", Path.Combine(baseDir, "zetaHat.bin"));
        ProbeFile("calculus", Path.Combine(baseDir, "calculus"));
        ProbeFile("zeus.ico", Path.Combine(baseDir, "zeus.ico"));
        ProbeFile("appsettings.json", Path.Combine(baseDir, "appsettings.json"));

        Section("preferences database");
        ProbePrefsDb();

        Section("network ports");
        ProbePorts();

        Section("end preflight");
    }

    // ---- crash diagnostics ----------------------------------------------

    private static void InstallNativeCrashDumpCapture()
    {
        try
        {
            var result = NativeCrashDumpWriter.Install(CrashDumpDir, LogPath, ExeName);
            Write(result);
            Write(ConfigureWindowsErrorReportingLocalDumps());
        }
        catch (Exception ex)
        {
            Write($"  native.crash.dump      unavailable ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string ConfigureWindowsErrorReportingLocalDumps()
    {
        if (!OperatingSystem.IsWindows())
            return "  wer.localdumps         n/a (non-Windows)";

        try
        {
            Directory.CreateDirectory(CrashDumpDir);
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\{ExeName}");
            if (key is null)
                return "  wer.localdumps         unavailable (registry key could not be opened)";

            key.SetValue("DumpFolder", CrashDumpDir, RegistryValueKind.ExpandString);
            key.SetValue("DumpCount", CrashDumpRetention, RegistryValueKind.DWord);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord);
            return $"  wer.localdumps         HKCU configured for {ExeName}: folder={CrashDumpDir} count={CrashDumpRetention} type=1";
        }
        catch (Exception ex)
        {
            return $"  wer.localdumps         unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static void ProbeCrashDumpState()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Kv("native.crash.dump", "n/a (Windows-only hard-crash dump handler)");
                return;
            }

            Directory.CreateDirectory(CrashDumpDir);
            Kv("crash.dump.dir", CrashDumpDir);
            Kv("support.collect", $"{LogPath}; {CrashDumpDir}; Windows Event Viewer > Application");
            Kv("crash.dump.privacy", "minidumps may include stack memory; share only for support/debugging");

            var dumps = Directory
                .EnumerateFiles(CrashDumpDir, "OpenhpsdrZeus-crash-*.dmp")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(CrashDumpRetention)
                .ToArray();

            if (dumps.Length == 0)
            {
                Kv("crash.dumps", "none captured yet");
                return;
            }

            Kv("crash.dumps", $"{dumps.Length} retained (newest first)");
            foreach (var dump in dumps)
                Write($"  crash.dump             {dump.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z  {dump.Length} bytes  {dump.FullName}");
        }
        catch (Exception ex)
        {
            Kv("crash.dumps", $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static void ProbeRecentWindowsCrashEvents()
    {
        if (!OperatingSystem.IsWindows())
        {
            Kv("windows.events", "n/a (non-Windows)");
            return;
        }

        try
        {
            var xml = QueryRecentWindowsCrashEventsXml();
            var events = ParseWindowsCrashEvents(xml, ExeName);
            if (events.Count == 0)
            {
                Kv("windows.events", "no recent OpenhpsdrZeus crash events found");
                return;
            }

            Kv("windows.events", $"{events.Count} recent OpenhpsdrZeus crash event(s)");
            foreach (var entry in events)
            {
                Write($"  windows.event          {entry.TimeCreatedUtc:u} provider={entry.Provider} id={entry.EventId} record={entry.RecordId}");
                foreach (var line in entry.DetailLines)
                    Write($"    {line}");
            }
        }
        catch (Exception ex)
        {
            Kv("windows.events", $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string QueryRecentWindowsCrashEventsXml()
    {
        const int timeoutMs = 1500;
        var ageMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;
        var query =
            "*[System[" +
            "((Provider[@Name='.NET Runtime'] and EventID=1026) or " +
            "(Provider[@Name='Application Error'] and EventID=1000)) and " +
            $"TimeCreated[timediff(@SystemTime) <= {ageMs}]" +
            "]]";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("qe");
        process.StartInfo.ArgumentList.Add("Application");
        process.StartInfo.ArgumentList.Add($"/q:{query}");
        process.StartInfo.ArgumentList.Add("/f:xml");
        process.StartInfo.ArgumentList.Add("/rd:true");
        process.StartInfo.ArgumentList.Add("/c:12");

        process.Start();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"wevtutil.exe did not return within {timeoutMs}ms");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0 &&
            !stderr.Contains("No events were found", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"wevtutil.exe exited {process.ExitCode}: {OneLine(stderr)}");
        }

        return stdout;
    }

    internal static IReadOnlyList<WindowsCrashEvent> ParseWindowsCrashEvents(string xml, string exeName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<WindowsCrashEvent>();

        var doc = XDocument.Parse("<Events>" + StripXmlDeclarations(xml) + "</Events>");
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var results = new List<WindowsCrashEvent>();

        foreach (var ev in doc.Root?.Elements(ns + "Event") ?? Enumerable.Empty<XElement>())
        {
            var system = ev.Element(ns + "System");
            if (system is null) continue;

            var provider = system.Element(ns + "Provider")?.Attribute("Name")?.Value ?? "<unknown>";
            var eventId = int.TryParse(system.Element(ns + "EventID")?.Value, out var id) ? id : 0;
            var recordId = system.Element(ns + "EventRecordID")?.Value ?? "<unknown>";
            var timeRaw = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            var time = DateTimeOffset.TryParse(timeRaw, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTimeOffset.MinValue;

            var data = ev
                .Descendants(ns + "Data")
                .Select(d => d.Value)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            if (!MentionsExecutable(data, exeName)) continue;

            var details = FormatCrashEventDetails(provider, eventId, data);
            results.Add(new WindowsCrashEvent(provider, eventId, recordId, time, details));
        }

        return results;
    }

    private static bool MentionsExecutable(IEnumerable<string> data, string exeName)
    {
        foreach (var item in data)
        {
            if (item.Contains(exeName, StringComparison.OrdinalIgnoreCase)) return true;
            if (item.Contains(Path.GetFileNameWithoutExtension(exeName), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IReadOnlyList<string> FormatCrashEventDetails(string provider, int eventId, string[] data)
    {
        if (provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) &&
            eventId == 1000)
        {
            return new[]
            {
                $"faulting.app={At(data, 0)}",
                $"faulting.module={At(data, 3)}",
                $"exception.code={At(data, 6)}",
                $"fault.offset={At(data, 7)}",
                $"process.id={At(data, 8)}",
                $"app.path={At(data, 10)}",
                $"module.path={At(data, 11)}",
                $"report.id={At(data, 12)}",
            };
        }

        var lines = new List<string>();
        foreach (var item in data)
        {
            foreach (var line in item.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = OneLine(line);
                if (trimmed.Length == 0) continue;
                lines.Add(trimmed.Length <= 260 ? trimmed : trimmed[..260] + "...");
                if (lines.Count >= 16) return lines;
            }
        }
        return lines;
    }

    private static string At(string[] values, int index) =>
        index >= 0 && index < values.Length && values[index].Length > 0 ? OneLine(values[index]) : "<missing>";

    private static string OneLine(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string StripXmlDeclarations(string xml) =>
        Regex.Replace(xml, @"<\?xml\s+version=""1\.0""\s+encoding=""utf-16""\s*\?>", "", RegexOptions.IgnoreCase);

    internal sealed record WindowsCrashEvent(
        string Provider,
        int EventId,
        string RecordId,
        DateTimeOffset TimeCreatedUtc,
        IReadOnlyList<string> DetailLines);

    private static class NativeCrashDumpWriter
    {
        private const int ExceptionContinueSearch = 0;
        private static readonly uint[] DumpworthyExceptionCodes =
        {
            0xC0000005, // STATUS_ACCESS_VIOLATION
            0xC000001D, // STATUS_ILLEGAL_INSTRUCTION
            0xC00000FD, // STATUS_STACK_OVERFLOW
            0xC0000409, // STATUS_STACK_BUFFER_OVERRUN / fail-fast family
        };

        private static readonly object InstallGate = new();
        private static TopLevelExceptionFilter? _topLevelFilter;
        private static VectoredExceptionHandler? _vectoredHandler;
        private static string? _dumpDir;
        private static string? _logPath;
        private static string? _exeName;
        private static int _dumpAttempted;

        public static string Install(string dumpDir, string logPath, string exeName)
        {
            if (!OperatingSystem.IsWindows())
                return "  native.crash.dump      n/a (non-Windows)";

            lock (InstallGate)
            {
                _dumpDir = dumpDir;
                _logPath = logPath;
                _exeName = exeName;
                Directory.CreateDirectory(dumpDir);
                PruneOldDumps(dumpDir, exeName);

                _topLevelFilter ??= HandleTopLevelException;
                _vectoredHandler ??= HandleVectoredException;

                var vectored = AddVectoredExceptionHandler(1, _vectoredHandler);
                SetUnhandledExceptionFilter(_topLevelFilter);

                var status = vectored == IntPtr.Zero
                    ? "top-level filter installed; vectored handler unavailable"
                    : "top-level + vectored filters installed";
                return $"  native.crash.dump      {status}; writing {Path.Combine(dumpDir, "OpenhpsdrZeus-crash-*.dmp")}";
            }
        }

        private static int HandleVectoredException(IntPtr exceptionPointers)
        {
            try
            {
                var code = ReadExceptionCode(exceptionPointers);
                if (DumpworthyExceptionCodes.Contains(code))
                    TryWriteDump(exceptionPointers, $"first-chance fatal SEH 0x{code:X8}");
            }
            catch { /* never throw from an exception filter */ }
            return ExceptionContinueSearch;
        }

        private static int HandleTopLevelException(IntPtr exceptionPointers)
        {
            try { TryWriteDump(exceptionPointers, "top-level unhandled exception"); }
            catch { /* never throw from an exception filter */ }
            return ExceptionContinueSearch;
        }

        private static uint ReadExceptionCode(IntPtr exceptionPointers)
        {
            if (exceptionPointers == IntPtr.Zero) return 0;
            var exceptionRecord = Marshal.ReadIntPtr(exceptionPointers);
            return exceptionRecord == IntPtr.Zero ? 0 : unchecked((uint)Marshal.ReadInt32(exceptionRecord));
        }

        private static void TryWriteDump(IntPtr exceptionPointers, string reason)
        {
            if (Interlocked.CompareExchange(ref _dumpAttempted, 1, 0) != 0) return;

            var dumpDir = _dumpDir;
            var logPath = _logPath;
            var exeName = _exeName ?? ExeName;
            if (string.IsNullOrWhiteSpace(dumpDir)) return;

            Directory.CreateDirectory(dumpDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(dumpDir, $"OpenhpsdrZeus-crash-{stamp}-pid{GetCurrentProcessId()}.dmp");

            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var info = new MiniDumpExceptionInformation
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = false,
            };
            var ok = MiniDumpWriteDump(
                GetCurrentProcess(),
                GetCurrentProcessId(),
                fs.SafeFileHandle.DangerousGetHandle(),
                MiniDumpType.Normal | MiniDumpType.WithThreadInfo | MiniDumpType.WithUnloadedModules,
                ref info,
                IntPtr.Zero,
                IntPtr.Zero);

            if (ok)
            {
                SafeAppend(logPath, $"[crash-dump] wrote {path} ({reason}; exe={exeName})");
            }
            else
            {
                SafeAppend(logPath, $"[crash-dump] FAILED {path} ({reason}; win32={Marshal.GetLastWin32Error()})");
            }
        }

        private static void PruneOldDumps(string dumpDir, string exeName)
        {
            try
            {
                foreach (var dump in Directory
                    .EnumerateFiles(dumpDir, Path.GetFileNameWithoutExtension(exeName) + "-crash-*.dmp")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(CrashDumpRetention))
                {
                    try { dump.Delete(); } catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
        }

        private static void SafeAppend(string? path, string line)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                File.AppendAllText(path,
                    $"{DateTime.UtcNow:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* crash path is best effort */ }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int TopLevelExceptionFilter(IntPtr exceptionPointers);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int VectoredExceptionHandler(IntPtr exceptionPointers);

        [Flags]
        private enum MiniDumpType : int
        {
            Normal = 0x00000000,
            WithUnloadedModules = 0x00000020,
            WithThreadInfo = 0x00001000,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MiniDumpExceptionInformation
        {
            public uint ThreadId;
            public IntPtr ExceptionPointers;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ClientPointers;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredExceptionHandler handler);

        [DllImport("kernel32.dll")]
        private static extern IntPtr SetUnhandledExceptionFilter(TopLevelExceptionFilter filter);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            int processId,
            IntPtr hFile,
            MiniDumpType dumpType,
            ref MiniDumpExceptionInformation exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);
    }

    // ---- WebView2 -------------------------------------------------------

    private static void ProbeWebView2()
    {
        if (!OperatingSystem.IsWindows())
        {
            Kv("webview2", "n/a (non-Windows host uses the system WebKit)");
            return;
        }
        try
        {
            var hr = GetAvailableCoreWebView2BrowserVersionString(null, out var ptr);
            if (hr == 0 && ptr != IntPtr.Zero)
            {
                var version = Marshal.PtrToStringUni(ptr) ?? "<null>";
                Marshal.FreeCoTaskMem(ptr);
                Kv("webview2.runtime", $"OK version={version}");
            }
            else
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
                Kv("webview2.runtime",
                    $"MISSING (hr=0x{hr:X8}) — install from https://developer.microsoft.com/microsoft-edge/webview2/");
            }
        }
        catch (DllNotFoundException)
        {
            Kv("webview2.runtime", "MISSING (WebView2Loader.dll not found next to the binary)");
        }
        catch (Exception ex)
        {
            Kv("webview2.runtime", $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- native libraries ----------------------------------------------

    private static void ProbeNativeNextToExe(string fileName)
    {
        // Self-contained publishes flatten these next to the exe; framework-
        // dependent (dev) builds keep them under runtimes/<rid>/native. Check the
        // flat location first, then the RID path, then the whole runtimes tree.
        var flat = Path.Combine(AppContext.BaseDirectory, fileName);
        var rid = Path.Combine(AppContext.BaseDirectory, "runtimes",
            RuntimeInformation.RuntimeIdentifier, "native", fileName);
        var path = File.Exists(flat) ? flat
            : File.Exists(rid) ? rid
            : FindUnderRuntimes(fileName);

        if (path is null) { Kv(fileName, "MISSING (not next to binary or under runtimes/)"); return; }
        TryLoad(fileName, path);
    }

    private static void ProbeRuntimeNative(string libName)
    {
        // Self-contained publishes lay native libs under
        // runtimes/<rid>/native/<file>. Compute the expected file for this OS
        // and, if the exact RID path isn't there, fall back to searching the
        // runtimes tree so a slightly different RID still reports correctly.
        var fileName = NativeFileName(libName);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var primary = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
        var path = File.Exists(primary) ? primary : FindUnderRuntimes(fileName);

        if (path is null) { Kv(libName, $"MISSING (no {fileName} under runtimes/*/native)"); return; }
        TryLoad(libName, path);
    }

    // Loading by full path exercises the OS loader + dependency chain, so a
    // wdsp.dll / miniaudio.dll that's present but un-loadable for want of the
    // VC++ runtime is reported as a load FAILURE, not a false "OK".
    private static void TryLoad(string label, string path)
    {
        try
        {
            var h = NativeLibrary.Load(path);
            NativeLibrary.Free(h);
            Kv(label, $"OK loadable ({path})");
        }
        catch (Exception ex)
        {
            Kv(label, $"PRESENT but FAILED to load: {ex.GetType().Name}: {ex.Message} ({path})");
        }
    }

    private static void ProbeVcRuntime()
    {
        if (!OperatingSystem.IsWindows()) { Kv("vcruntime", "n/a (non-Windows)"); return; }
        // wdsp.dll + miniaudio.dll are MSVC-linked; without the VC++ 2015-2022
        // redistributable they fail to load and Zeus drops to synthetic DSP /
        // no audio. Probe the runtime DLLs by name (resolved from the system).
        foreach (var dll in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
        {
            try
            {
                var h = NativeLibrary.Load(dll);
                NativeLibrary.Free(h);
                Kv(dll, "OK present");
            }
            catch (Exception ex)
            {
                Kv(dll, $"MISSING ({ex.GetType().Name}) — install the VC++ 2015-2022 Redistributable");
            }
        }
    }

    private static string NativeFileName(string libName)
    {
        if (OperatingSystem.IsWindows()) return libName + ".dll";
        if (OperatingSystem.IsMacOS()) return "lib" + libName + ".dylib";
        return "lib" + libName + ".so";
    }

    private static string? FindUnderRuntimes(string fileName)
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "runtimes");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    // ---- files ----------------------------------------------------------

    private static void ProbeFile(string label, string path)
    {
        try
        {
            if (Directory.Exists(path)) { Kv(label, $"OK dir ({path})"); return; }
            if (File.Exists(path))
            {
                Kv(label, $"OK {new FileInfo(path).Length} bytes ({path})");
                return;
            }
            Kv(label, $"MISSING ({path})");
        }
        catch (Exception ex)
        {
            Kv(label, $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- prefs DB -------------------------------------------------------

    private static void ProbePrefsDb()
    {
        try
        {
            var path = PrefsDbPath.Get();
            Kv("prefs.path", path);
            Kv("prefs.exists", File.Exists(path) ? $"yes ({new FileInfo(path).Length} bytes)" : "no (fresh install)");
            // EnsureUsable is the same guard the host runs; report its verdict so
            // a corrupt-and-locked DB (which forces the temp-DB fallback) shows up.
            var usable = PrefsDbPath.EnsureUsable(path);
            Kv("prefs.usable", usable ? "yes" : "NO (corrupt + locked → temp DB fallback)");
        }
        catch (Exception ex)
        {
            Kv("prefs", $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- ports ----------------------------------------------------------

    private static void ProbePorts()
    {
        try
        {
            var lanIps = LanCertificate.GetLanIps();
            Kv("lan.ips", lanIps.Count == 0 ? "<none up>" : string.Join(", ", lanIps));
            Kv("lan.https.port", LanCertificate.GetHttpsPort().ToString());
        }
        catch (Exception ex)
        {
            Kv("lan", $"UNKNOWN ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- low-level helpers ---------------------------------------------

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; } catch { return "<unavailable>"; }
    }

    private static string SafeCwd()
    {
        try { return Directory.GetCurrentDirectory(); } catch { return "<unavailable>"; }
    }

    private static string SafeDataDir()
    {
        try { return PrefsDbPath.DataDir; } catch { return "<unavailable>"; }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); } catch { return "<unavailable>"; }
    }

    private static string SafeFreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path == "<unavailable>") return "<unavailable>";
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return "<unavailable>";
            var drive = new DriveInfo(root);
            return $"{drive.AvailableFreeSpace / 1024 / 1024} MiB available on {drive.Name}";
        }
        catch (Exception ex)
        {
            return $"<unavailable: {ex.GetType().Name}>";
        }
    }

    private static void Section(string title) => Write($"---- {title} ----");

    private static void Kv(string key, string value) => Write($"  {key,-22} {value}");

    private static void Write(string line)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, stamped + Environment.NewLine); }
            catch { /* best effort — diagnostics must never throw */ }
        }
        // Also to stderr: detached on Windows desktop/server mode, but visible
        // for `dotnet run`, service mode, and macOS/Linux launches.
        try { Console.Error.WriteLine(stamped); } catch { /* ignore */ }
    }
}
