// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Reflection;
using System.Runtime.InteropServices;
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
    private static string? _logPath;

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

    // Call once at the very top of Main. Opens/rotates the log, installs
    // last-resort exception handlers (so a crash on ANY thread is recorded —
    // not just the synchronous startup path), and dumps the full preflight.
    public static void Begin(string[] args)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Keep the file from growing without bound across launches, but
            // preserve recent history for a test user reproducing intermittently.
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.Delete(LogPath);
            }
            catch { /* best effort */ }

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
        WritePreflight();
    }

    // Append a timestamped phase marker. The LAST marker in the log before a
    // crash is the failing step.
    public static void Phase(string name) => Write($"[phase] {name}");

    public static void Log(string message) => Write(message);

    public static void LogException(string context, Exception? ex)
    {
        if (ex is null) { Write($"[error] {context}: <no exception object>"); return; }
        Write($"[error] {context}:\n{ex}");
    }

    private static void WriteHeader(string[] args)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        Write("================================================================");
        Write($"Zeus startup  version={version}");
        Write($"args=[{string.Join(' ', args)}]");
        Write("================================================================");
    }

    private static void WritePreflight()
    {
        Section("environment");
        Kv("os", RuntimeInformation.OSDescription);
        Kv("os.arch", RuntimeInformation.OSArchitecture.ToString());
        Kv("process.arch", RuntimeInformation.ProcessArchitecture.ToString());
        Kv("runtime", RuntimeInformation.FrameworkDescription);
        Kv("rid", RuntimeInformation.RuntimeIdentifier);
        Kv("machine", SafeMachineName());
        Kv("64bit.os", Environment.Is64BitOperatingSystem.ToString());
        Kv("64bit.process", Environment.Is64BitProcess.ToString());
        Kv("user.interactive", Environment.UserInteractive.ToString());
        Kv("base.dir", AppContext.BaseDirectory);
        Kv("cwd", SafeCwd());
        Kv("localappdata", SafeFolder(Environment.SpecialFolder.LocalApplicationData));
        Kv("temp", Path.GetTempPath());
        Kv("env.ZEUS_WEBROOT", Environment.GetEnvironmentVariable("ZEUS_WEBROOT") ?? "<unset>");
        Kv("env.ZEUS_PREFS_PATH", Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH") ?? "<unset>");
        Kv("env.ZEUS_DESKTOP_PORT", Environment.GetEnvironmentVariable("ZEUS_DESKTOP_PORT") ?? "<unset>");
        Kv("env.ZEUS_ENABLE_VST_LOAD", Environment.GetEnvironmentVariable("ZEUS_ENABLE_VST_LOAD") ?? "<unset>");

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

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); } catch { return "<unavailable>"; }
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
