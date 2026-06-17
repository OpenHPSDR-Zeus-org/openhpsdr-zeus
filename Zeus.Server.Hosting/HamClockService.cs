// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// HamClockService — optional, on-demand embed of OpenHamClock
// (https://github.com/accius/openhamclock, MIT) as a Zeus panel.
//
// OpenHamClock is a self-contained Node/Express web app. Its Express
// server is NOT optional chrome: it's a CORS proxy that nearly every
// live-data feature (NOAA space weather, POTA/SOTA, DX cluster,
// PSKReporter, RBN, ITURHFProp propagation, satellites, APRS) routes
// through. Serving its static dist/ alone would leave those panels dead,
// so the only "works" option is to run its own server as a managed
// sidecar process and point an <iframe> at it.
//
// Nothing here touches the radio / DSP / TX path. This is a self-supervised
// child process that is entirely inert until the operator clicks "Install"
// in Settings → HamClock. If Node is missing, install fails loudly with a
// clear message; it never wedges Zeus.
//
// Lifecycle:
//   NotInstalled --Install--> (download zip → npm ci → npm run build) --> Installed
//   Installed    --Start----> (spawn `node server.js` on a free port) ---> Running
//   Running      --Stop-----> (kill child) --------------------------------> Installed
// The child is always killed on Zeus shutdown (IHostedService.StopAsync).

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>Coarse lifecycle phase for the HamClock embed, surfaced to the UI.</summary>
public enum HamClockPhase
{
    NotInstalled,
    Installing,
    Installed,
    Starting,
    Running,
    Error,
}

/// <summary>Immutable status snapshot returned by <c>GET /api/hamclock/status</c>.</summary>
public sealed record HamClockStatus(
    string Phase,
    bool Installed,
    bool Running,
    bool Busy,
    int Port,
    string? Version,
    bool NodeAvailable,
    string? NodeVersion,
    string? Error,
    IReadOnlyList<string> Log);

/// <summary>
/// Owns the OpenHamClock sidecar: download/build install, start/stop of the
/// Node server process, and a status snapshot for the Settings panel.
/// Singleton + <see cref="IHostedService"/> (only for clean shutdown — it
/// never auto-starts the child; the operator opens the panel to start it).
/// </summary>
public sealed class HamClockService : IHostedService, IAsyncDisposable
{
    // Pinned release. Override with ZEUS_HAMCLOCK_ZIP_URL to track a different
    // tag / a local mirror. Pinned (not a branch) for reproducible installs;
    // bump deliberately. codeload returns a zip whose single top-level folder
    // is openhamclock-<tag-without-v>/, which InstallAsync flattens.
    private const string DefaultTag = "v26.4.1";
    private static string SourceZipUrl =>
        Environment.GetEnvironmentVariable("ZEUS_HAMCLOCK_ZIP_URL")
        ?? $"https://github.com/accius/openhamclock/archive/refs/tags/{DefaultTag}.zip";

    private const int MaxLogLines = 400;

    // Single client for download + health-poll. GitHub codeload follows a
    // redirect to its asset CDN; HttpClient follows it by default.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly ILogger<HamClockService> _log;
    private readonly RigBridgeService _rigBridge;
    private readonly object _gate = new();
    private readonly LinkedList<string> _logLines = new();

    private HamClockPhase _phase = HamClockPhase.NotInstalled;
    private string? _error;
    private int _port;
    private Process? _proc;
    private bool _busy; // an install or start is in flight

    // Resolved Node runtime. _nodeDir = a directory to prepend to PATH so
    // node/npm resolve to a private copy we downloaded; null = use whatever
    // 'node' is on the system PATH. Set by EnsureNodeAsync. _nodeInfo is the
    // cached "(available, version)" for Snapshot so status polls don't spawn
    // `node --version` on every request.
    private string? _nodeDir;
    private bool _nodeBundled;
    private readonly object _nodeGate = new();
    private (bool ok, string? version) _nodeInfo;
    private bool _nodeProbed;

    // Pinned portable Node (current LTS). Downloaded into PortableNodeRoot when
    // the system has no Node, so Install works on a machine with none
    // preinstalled. Override the version only by editing this constant.
    private const string PortableNodeVersion = "v22.11.0";

    public HamClockService(ILogger<HamClockService> log, RigBridgeService rigBridge)
    {
        _log = log;
        _rigBridge = rigBridge;
        // Reflect a prior install on boot so the panel comes up "Installed"
        // without the operator re-downloading every launch.
        if (IsBuilt(InstallDir))
            _phase = HamClockPhase.Installed;
    }

    /// <summary>App-data install root: %LOCALAPPDATA%/Zeus/hamclock (mirrors PrefsDbPath).</summary>
    public static string InstallDir
    {
        get
        {
            var appData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(appData, "Zeus", "hamclock");
        }
    }

    /// <summary>App-data root for a downloaded private Node: %LOCALAPPDATA%/Zeus/node.</summary>
    private static string PortableNodeRoot
    {
        get
        {
            var appData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(appData, "Zeus", "node");
        }
    }

    /// <summary>A built install has both a server entrypoint and a Vite dist/.</summary>
    private static bool IsBuilt(string dir) =>
        File.Exists(Path.Combine(dir, "server.js")) &&
        File.Exists(Path.Combine(dir, "dist", "index.html"));

    // -- Status ----------------------------------------------------------

    public HamClockStatus Snapshot()
    {
        // Probe Node outside _gate (it may spawn `node --version`) and cache it.
        var (nodeOk, nodeVer) = GetNodeInfoCached();
        lock (_gate)
        {
            bool running = _proc is { HasExited: false };
            return new HamClockStatus(
                Phase: _phase.ToString(),
                Installed: IsBuilt(InstallDir),
                Running: running,
                Busy: _busy,
                Port: running ? _port : 0,
                Version: ReadInstalledVersion(),
                NodeAvailable: nodeOk,
                NodeVersion: nodeVer,
                Error: _error,
                Log: _logLines.ToArray());
        }
    }

    // -- Install ---------------------------------------------------------

    /// <summary>
    /// Kick off download → npm ci → npm run build on a background task.
    /// Returns immediately; progress is observable via <see cref="Snapshot"/>.
    /// Returns false if an install/start is already running.
    /// </summary>
    public bool BeginInstall()
    {
        lock (_gate)
        {
            if (_busy) return false;
            _busy = true;
            _error = null;
            _phase = HamClockPhase.Installing;
            _logLines.Clear();
        }
        Append("Starting HamClock install…");
        _ = Task.Run(InstallCoreAsync);
        return true;
    }

    private async Task InstallCoreAsync()
    {
        try
        {
            // Resolve Node — use the system copy if present, otherwise download a
            // private one. This is what makes Install one-click on a machine with
            // no Node preinstalled.
            if (!await EnsureNodeAsync().ConfigureAwait(false)) return; // Fail() already set

            Directory.CreateDirectory(InstallDir);

            // 1. Download the pinned source zip to a temp file.
            Append($"Downloading {SourceZipUrl} …");
            var tmpZip = Path.Combine(Path.GetTempPath(), $"hamclock-{Guid.NewGuid():N}.zip");
            await using (var resp = await Http.GetStreamAsync(SourceZipUrl).ConfigureAwait(false))
            await using (var fs = File.Create(tmpZip))
                await resp.CopyToAsync(fs).ConfigureAwait(false);
            Append($"Downloaded {new FileInfo(tmpZip).Length / 1024} KiB.");

            // 2. Extract to a staging dir, then flatten the single top-level
            //    folder (openhamclock-<tag>/) into InstallDir. Wipe any prior
            //    install first so a re-install is clean.
            var staging = Path.Combine(Path.GetTempPath(), $"hamclock-stage-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(tmpZip, staging);
            try { File.Delete(tmpZip); } catch { /* best effort */ }

            var top = Directory.GetDirectories(staging).FirstOrDefault() ?? staging;
            Append("Staging extracted source…");
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true);
            Directory.Move(top, InstallDir);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }

            // 3. npm ci (reproducible from the committed lockfile). Fall back to
            //    npm install if the lockfile is absent / out of sync.
            Append("Installing dependencies (npm ci)… this can take a few minutes.");
            int rc = await RunToolAsync("npm", "ci", InstallDir).ConfigureAwait(false);
            if (rc != 0)
            {
                Append("npm ci failed; retrying with npm install…");
                rc = await RunToolAsync("npm", "install", InstallDir).ConfigureAwait(false);
                if (rc != 0) { Fail("npm install failed — see log."); return; }
            }

            // 4. Build the Vite frontend into dist/.
            Append("Building frontend (npm run build)…");
            rc = await RunToolAsync("npm", "run build", InstallDir).ConfigureAwait(false);
            if (rc != 0) { Fail("npm run build failed — see log."); return; }

            if (!IsBuilt(InstallDir)) { Fail("Build finished but dist/index.html is missing."); return; }

            // HamClock's Express server sends X-Frame-Options: SAMEORIGIN (helmet
            // frameguard), which blocks embedding it in the Zeus workspace iframe.
            // We own this local copy, so disable frameguard.
            PatchHelmetFrameguard();
            // Make "Use My Current Location" work on the desktop webview (no HTML5
            // Geolocation API) via a native-first shim with a coarse IP fallback.
            PatchGeolocationFallback();
            PatchSettingsPersistence();
            PatchDownloadBridge();

            lock (_gate) { _phase = HamClockPhase.Installed; _busy = false; }
            Append("HamClock installed. Click Start to launch the panel.");
        }
        catch (Exception ex)
        {
            Fail($"Install error: {ex.Message}");
        }
    }

    // -- Start / Stop ----------------------------------------------------

    /// <summary>
    /// Launch `node server.js` on a free port and health-poll until it
    /// answers. Idempotent: a no-op (returns the live port) if already
    /// running. Throws on failure with a UI-friendly message.
    /// </summary>
    public async Task<int> StartAsync()
    {
        lock (_gate)
        {
            if (_proc is { HasExited: false }) return _port;
            if (_busy) throw new InvalidOperationException("HamClock is busy.");
            if (!IsBuilt(InstallDir)) throw new InvalidOperationException("HamClock is not installed yet.");
            _busy = true;
            _error = null;
            _phase = HamClockPhase.Starting;
        }

        try
        {
            // Resolve Node (system, or the private copy fetched at install time).
            if (!await EnsureNodeAsync().ConfigureAwait(false))
                throw new InvalidOperationException("Node.js is unavailable — reinstall HamClock from Settings.");

            // Reuse the previously-chosen port (persisted in .env) when it's
            // still free, so HamClock keeps the SAME loopback origin across
            // restarts. Its settings (callsign, location, map prefs) live in
            // localStorage, which is per-origin — a changing port silently
            // wipes them every launch. Falls back to a fresh free port only on
            // first run or if the saved one is taken.
            var port = ResolveStablePort();
            Append($"Starting HamClock server on port {port}…");

            // HamClock's own server/config loads .env with precedence over
            // process.env, and creates .env from .env.example on first run with
            // a pinned PORT=3001. So passing PORT via the environment is not
            // enough — we write the chosen port into .env. server.js binds
            // app.listen(PORT, '0.0.0.0'), so HOST is moot; only PORT matters.
            EnsureEnvPort(port);
            // Covers installs that predate the frameguard patch (idempotent).
            PatchHelmetFrameguard();
            // Covers pre-existing installs + re-injects after a dist/ rebuild that
            // regenerated index.html without the marker (idempotent).
            PatchGeolocationFallback();
            PatchSettingsPersistence();
            PatchDownloadBridge();

            var psi = MakePsi("node", "server.js", InstallDir);
            // Belt-and-suspenders env (overridden by .env, but harmless).
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["NODE_ENV"] = "production";
            psi.Environment["AUTO_UPDATE_ENABLED"] = "false";

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Append("[hc] " + e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Append("[hc!] " + e.Data); };
            proc.Exited += (_, _) =>
            {
                Append("HamClock server exited.");
                lock (_gate)
                {
                    if (_phase is HamClockPhase.Running or HamClockPhase.Starting)
                        _phase = IsBuilt(InstallDir) ? HamClockPhase.Installed : HamClockPhase.NotInstalled;
                }
            };

            if (!proc.Start()) throw new InvalidOperationException("Failed to start node.");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_gate) { _proc = proc; _port = port; }

            // Health-poll the server root for up to ~30s.
            var healthy = await WaitForHealthAsync(port, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!healthy)
            {
                if (proc is { HasExited: false }) { try { proc.Kill(entireProcessTree: true); } catch { } }
                lock (_gate) { _proc = null; }
                throw new InvalidOperationException("HamClock server did not become healthy in time — see log.");
            }

            lock (_gate) { _phase = HamClockPhase.Running; }
            Append($"HamClock running on port {port}.");

            // Auto-link click-to-tune (zero operator setup): seed HamClock's
            // Rig Control setting to point at the bundled rig-bridge agent with a
            // matching API token, then spawn that agent in TCI mode (config written
            // by RigBridgeService, talking to Zeus's TCI server on :40001). Both
            // are best-effort — a failure here never blocks HamClock itself.
            try { SeedRigControl(port); } catch (Exception ex) { Append($"  (rigControl seed skipped: {ex.Message})"); }
            try { await _rigBridge.StartAsync(port).ConfigureAwait(false); }
            catch (Exception ex) { Append($"  (rig-bridge start skipped: {ex.Message})"); }

            return port;
        }
        catch (Exception ex)
        {
            Fail($"Start error: {ex.Message}");
            throw;
        }
        finally
        {
            lock (_gate) { _busy = false; }
        }
    }

    /// <summary>Kill the HamClock server if running. Idempotent.</summary>
    public void Stop()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _proc;
            _proc = null;
            if (_phase is HamClockPhase.Running or HamClockPhase.Starting)
                _phase = IsBuilt(InstallDir) ? HamClockPhase.Installed : HamClockPhase.NotInstalled;
        }
        if (proc is { HasExited: false })
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        proc?.Dispose();
        // Stopping HamClock also stops its rig-bridge sidecar (lives/dies with it).
        try { _rigBridge.Stop(); } catch { /* best effort */ }
        Append("HamClock server stopped.");
    }

    // -- Helpers ---------------------------------------------------------

    /// <summary>Run `node --version` using the currently resolved Node (system
    /// PATH, or the private copy via _nodeDir). Returns (ok, version-string).</summary>
    private (bool ok, string? version) DetectNode()
    {
        try
        {
            var psi = MakePsi("node", "--version", Path.GetTempPath());
            using var p = Process.Start(psi);
            if (p is null) return (false, null);
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? (true, outp) : (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Cached Node availability for Snapshot — probes once, then reuses
    /// the result (refreshed by EnsureNodeAsync). Also adopts a previously
    /// downloaded private Node if the system has none.</summary>
    private (bool ok, string? version) GetNodeInfoCached()
    {
        lock (_nodeGate) { if (_nodeProbed) return _nodeInfo; }
        var info = DetectNode();
        if (!info.ok && _nodeDir is null)
        {
            var portable = FindPortableNodeBinDir();
            if (portable is not null)
            {
                _nodeDir = portable;
                info = DetectNode();
                if (info.ok) _nodeBundled = true; else _nodeDir = null;
            }
        }
        lock (_nodeGate) { _nodeInfo = info; _nodeProbed = true; }
        return info;
    }

    private void SetNodeInfo((bool ok, string? version) info)
    {
        lock (_nodeGate) { _nodeInfo = info; _nodeProbed = true; }
    }

    /// <summary>
    /// Ensure a usable Node is resolved into <see cref="_nodeDir"/>. Order:
    /// (1) system Node on PATH, (2) a private copy from a prior install,
    /// (3) download a pinned portable Node from nodejs.org (checksum-verified).
    /// Returns false (and calls Fail) only if the download/extract fails.
    /// </summary>
    private async Task<bool> EnsureNodeAsync()
    {
        // (1) Whatever's already resolved (system, or a _nodeDir set earlier).
        var (ok, ver) = DetectNode();
        if (ok)
        {
            Append($"Using {(_nodeBundled ? "bundled" : "system")} Node {ver}.");
            SetNodeInfo((true, ver));
            return true;
        }

        // (2) A private Node from a previous install.
        var portable = FindPortableNodeBinDir();
        if (portable is not null)
        {
            _nodeDir = portable;
            (ok, ver) = DetectNode();
            if (ok)
            {
                _nodeBundled = true;
                Append($"Using bundled Node {ver}.");
                SetNodeInfo((true, ver));
                return true;
            }
            _nodeDir = null;
        }

        // (3) Download a pinned portable Node.
        Append($"Node.js not found on this system — downloading a private copy ({PortableNodeVersion}) for HamClock…");
        var binDir = await DownloadPortableNodeAsync().ConfigureAwait(false);
        if (binDir is null) return false; // Fail() already set
        _nodeDir = binDir;
        _nodeBundled = true;
        (ok, ver) = DetectNode();
        if (!ok)
        {
            _nodeDir = null;
            Fail("Downloaded Node did not run.");
            return false;
        }
        Append($"Bundled Node {ver} ready.");
        SetNodeInfo((true, ver));
        return true;
    }

    /// <summary>The PATH-prependable bin dir of a previously downloaded private
    /// Node, or null. Windows: the extracted folder (node.exe + npm.cmd at root);
    /// Unix: its <c>bin/</c> subdir.</summary>
    private static string? FindPortableNodeBinDir()
    {
        try
        {
            if (!Directory.Exists(PortableNodeRoot)) return null;
            foreach (var dir in Directory.GetDirectories(PortableNodeRoot))
            {
                var binDir = OperatingSystem.IsWindows() ? dir : Path.Combine(dir, "bin");
                var exe = Path.Combine(binDir, OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(exe)) return binDir;
            }
        }
        catch { /* best effort */ }
        return null;
    }

    /// <summary>Compute the nodejs.org artifact for this OS/arch.</summary>
    private static (string url, string fileName, bool isZip, string dirName) PortableNodeArtifact()
    {
        string os, ext;
        bool isZip;
        if (OperatingSystem.IsWindows()) { os = "win"; ext = "zip"; isZip = true; }
        else if (OperatingSystem.IsMacOS()) { os = "darwin"; ext = "tar.gz"; isZip = false; }
        else { os = "linux"; ext = "tar.gz"; isZip = false; }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        var dirName = $"node-{PortableNodeVersion}-{os}-{arch}";
        var fileName = $"{dirName}.{ext}";
        var url = $"https://nodejs.org/dist/{PortableNodeVersion}/{fileName}";
        return (url, fileName, isZip, dirName);
    }

    /// <summary>Download + checksum-verify + extract a portable Node into
    /// PortableNodeRoot. Returns the PATH-prependable bin dir, or null on failure
    /// (after calling Fail).</summary>
    private async Task<string?> DownloadPortableNodeAsync()
    {
        try
        {
            var (url, fileName, isZip, dirName) = PortableNodeArtifact();
            Directory.CreateDirectory(PortableNodeRoot);

            Append($"Downloading {url} …");
            var tmp = Path.Combine(Path.GetTempPath(), fileName);
            await using (var s = await Http.GetStreamAsync(url).ConfigureAwait(false))
            await using (var fs = File.Create(tmp))
                await s.CopyToAsync(fs).ConfigureAwait(false);
            Append($"Downloaded {new FileInfo(tmp).Length / 1024 / 1024} MiB. Verifying checksum…");

            if (!await VerifyNodeChecksumAsync(tmp, fileName).ConfigureAwait(false))
            {
                try { File.Delete(tmp); } catch { }
                Fail("Node download failed SHA-256 verification — aborting.");
                return null;
            }

            var dest = Path.Combine(PortableNodeRoot, dirName);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Append("Extracting Node…");
            if (isZip)
            {
                ZipFile.ExtractToDirectory(tmp, PortableNodeRoot);
            }
            else
            {
                // tar.gz — use the system `tar` (present on Windows 10+, macOS, Linux).
                var rc = await RunToolAsync("tar", $"-xzf \"{tmp}\" -C \"{PortableNodeRoot}\"", PortableNodeRoot)
                    .ConfigureAwait(false);
                if (rc != 0) { Fail("Failed to extract the Node tarball (tar)."); return null; }
            }
            try { File.Delete(tmp); } catch { }

            var binDir = OperatingSystem.IsWindows() ? dest : Path.Combine(dest, "bin");
            var exe = Path.Combine(binDir, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (!File.Exists(exe)) { Fail("Node archive did not contain the expected binary."); return null; }
            // Defensively ensure the extracted node is executable. tar normally
            // preserves the mode, but a stray umask / extractor quirk that drops
            // the +x bit would leave node un-runnable → HamClock "won't start".
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(exe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { /* best-effort; tar usually set it already */ }
            }
            return binDir;
        }
        catch (Exception ex)
        {
            Fail($"Node download error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Verify a downloaded Node archive against nodejs.org's
    /// SHASUMS256.txt. Returns true on match; also true (with a note) if the
    /// checksum list can't be fetched or has no entry — so a transient
    /// SHASUMS fetch failure doesn't block install, while a real mismatch does.</summary>
    private async Task<bool> VerifyNodeChecksumAsync(string filePath, string fileName)
    {
        try
        {
            var sums = await Http.GetStringAsync(
                $"https://nodejs.org/dist/{PortableNodeVersion}/SHASUMS256.txt").ConfigureAwait(false);
            string? expected = null;
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == fileName)
                {
                    expected = parts[0].ToLowerInvariant();
                    break;
                }
            }
            if (expected is null)
            {
                Append("  (checksum: no entry for this file — skipping verification)");
                return true;
            }
            await using var fs = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (actual == expected) { Append("  checksum OK."); return true; }
            Append($"  checksum MISMATCH (expected {expected[..12]}…, got {actual[..12]}…)");
            return false;
        }
        catch (Exception ex)
        {
            Append($"  (checksum check skipped: {ex.Message})");
            return true;
        }
    }

    private static string? ReadInstalledVersion()
    {
        try
        {
            var pkg = Path.Combine(InstallDir, "package.json");
            if (!File.Exists(pkg)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Build a ProcessStartInfo for `node`/`npm` using the SAME resolved Node
    /// runtime HamClock uses (system PATH, or the private copy via _nodeDir).
    /// Exposed for the rig-bridge sidecar (RigBridgeService) so both Node
    /// children share one runtime resolution. Ensures Node is resolved first.
    /// </summary>
    internal async Task<ProcessStartInfo?> MakeNodePsiAsync(string tool, string args, string cwd)
    {
        if (!await EnsureNodeAsync().ConfigureAwait(false)) return null;
        return MakePsi(tool, args, cwd);
    }

    /// <summary>
    /// Build a ProcessStartInfo that resolves PATH-installed tools on every
    /// OS. npm is a .cmd shim on Windows (not a PATH-resolvable .exe), so it
    /// must be invoked through cmd.exe; node.exe resolves directly.
    /// </summary>
    private ProcessStartInfo MakePsi(string tool, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows() && (tool is "npm" or "npx"))
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c {tool} {args}";
        }
        else
        {
            psi.FileName = tool;
            psi.Arguments = args;
        }
        // Build the dirs to prepend to the child's PATH, highest priority first.
        //  - _nodeDir: the private portable copy (npm lives beside node there,
        //    not on the system PATH).
        //  - On macOS, the common system-node install dirs. A GUI-launched .app
        //    inherits a minimal launchd PATH (/usr/bin:/bin:/usr/sbin:/sbin) and
        //    never runs the login-shell path_helper, so an already-installed
        //    node (Homebrew/official .pkg/MacPorts) is invisible — `node` then
        //    fails to resolve and HamClock reports "Node missing" even though it
        //    is right there. Prepending these makes the app resolve node exactly
        //    as a terminal does. (#657)
        var prepend = new List<string>();
        if (_nodeDir is not null) prepend.Add(_nodeDir);
        if (OperatingSystem.IsMacOS())
        {
            prepend.Add("/opt/homebrew/bin"); // Apple-silicon Homebrew
            prepend.Add("/usr/local/bin");    // Intel Homebrew + official node .pkg
            prepend.Add("/opt/local/bin");    // MacPorts
        }
        if (prepend.Count > 0)
        {
            var existing = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH");
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, prepend)
                + Path.PathSeparator + (existing ?? string.Empty);
        }
        return psi;
    }

    /// <summary>Run a tool to completion, streaming its output into the log. Returns the exit code (or -1 on spawn failure).</summary>
    private async Task<int> RunToolAsync(string tool, string args, string cwd)
    {
        try
        {
            var psi = MakePsi(tool, args, cwd);
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Append("  " + e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Append("  " + e.Data); };
            if (!p.Start()) return -1;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Append($"  ! {tool} {args}: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Wait until the sidecar is accepting TCP connections on its port. A raw
    /// loopback connect (not an HTTP GET) deliberately sidesteps any system
    /// proxy / WinHTTP indirection that can stall or refuse loopback HTTP — the
    /// listening socket is the only signal we need before showing the iframe.
    /// </summary>
    private static async Task<bool> WaitForHealthAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                var done = await Task.WhenAny(connect, Task.Delay(1500)).ConfigureAwait(false);
                if (done == connect && client.Connected)
                {
                    await connect.ConfigureAwait(false); // observe any exception
                    return true;
                }
            }
            catch { /* not listening yet */ }
            await Task.Delay(400).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Ensure <c>.env</c> in the install dir pins HamClock's PORT to the one we
    /// chose (its config gives .env precedence over process.env). Seeds from
    /// .env.example if .env doesn't exist yet, then upserts the PORT,
    /// AUTO_UPDATE_ENABLED and SETTINGS_SYNC keys, preserving every other line.
    /// </summary>
    private static void EnsureEnvPort(int port)
    {
        var envPath = Path.Combine(InstallDir, ".env");
        string content;
        if (File.Exists(envPath))
            content = File.ReadAllText(envPath);
        else
        {
            var example = Path.Combine(InstallDir, ".env.example");
            content = File.Exists(example) ? File.ReadAllText(example) : string.Empty;
        }
        content = UpsertEnvKey(content, "PORT", port.ToString());
        content = UpsertEnvKey(content, "AUTO_UPDATE_ENABLED", "false");
        // Persist HamClock's UI settings (callsign, location, map prefs) on the
        // server side, in the install dir. The embed's localStorage does NOT
        // survive — the cross-origin iframe gets partitioned/ephemeral storage
        // in the desktop webview (WebKit third-party storage), so settings reset
        // (and the first-visit popup reappears) every launch otherwise. Server
        // sync mirrors the openhamclock_* keys to disk and reloads them on boot.
        // requireWriteAuth is open with no API_WRITE_KEY set (our local install).
        content = UpsertEnvKey(content, "SETTINGS_SYNC", "true");
        File.WriteAllText(envPath, content);
    }

    /// <summary>
    /// Disable helmet's frameguard in HamClock's middleware so it stops sending
    /// X-Frame-Options: SAMEORIGIN, which otherwise blocks embedding it in the
    /// Zeus workspace iframe. Idempotent — a no-op once "frameguard" is present
    /// (or if upstream restructures the helmet call). CSP is already disabled
    /// upstream, so there's no frame-ancestors directive to worry about.
    /// </summary>
    private void PatchHelmetFrameguard()
    {
        try
        {
            var file = Path.Combine(InstallDir, "server", "middleware", "index.js");
            if (!File.Exists(file)) return;
            var src = File.ReadAllText(file);
            if (src.Contains("frameguard", StringComparison.Ordinal)) return; // already patched
            const string anchor = "helmet({";
            var idx = src.IndexOf(anchor, StringComparison.Ordinal);
            if (idx < 0)
            {
                Append("  (note: couldn't disable frameguard — helmet anchor not found; embedding may be blocked)");
                return;
            }
            src = src.Insert(idx + anchor.Length,
                "\n      frameguard: false, // Zeus: allow embedding in the Zeus workspace iframe");
            File.WriteAllText(file, src);
            Append("Patched HamClock to allow embedding (X-Frame-Options off).");
        }
        catch (Exception ex)
        {
            Append($"  (frameguard patch skipped: {ex.Message})");
        }
    }

    /// <summary>
    /// Inject a native-first geolocation shim into HamClock's served HTML so the
    /// "Use My Current Location" button works on every platform. HamClock calls
    /// only navigator.geolocation.getCurrentPosition; the macOS desktop webview
    /// (Photino → WKWebView) does not implement the HTML5 Geolocation API, so the
    /// call always fails there. The shim tries the NATIVE API first (preserving
    /// exact browser / Windows-WebView2 behaviour), and only on failure — or when
    /// the API is absent entirely — falls back to a user-initiated, coarse
    /// (city-level) IP lookup, mapped into the standard GeolocationPosition shape.
    /// Idempotent — a no-op once the ZEUS_GEO_FALLBACK marker is present. Targets
    /// the global navigator.geolocation, so it is independent of the hashed Vite
    /// bundle filename and survives rebuilds (re-injected on the next start).
    /// </summary>
    private void PatchGeolocationFallback()
    {
        // Inline (non-module) script: runs at parse time, before the deferred
        // ES-module bundle, so the global patch is installed first. The bundle
        // reads navigator.geolocation at call time, so it picks this up unchanged.
        const string shim =
@"<!-- ZEUS_GEO_FALLBACK: native-first geolocation with IP fallback (KB2UKA) -->
<script>
(function () {
  var geo = navigator.geolocation;
  var nativeGet = geo && geo.getCurrentPosition ? geo.getCurrentPosition.bind(geo) : null;
  // City-level, no-key, CORS + HTTPS services; both expose flat latitude/longitude.
  var IP_SERVICES = ['https://ipapi.co/json/', 'https://ipwho.is/'];
  function ipLookup() {
    var i = 0;
    function tryNext() {
      if (i >= IP_SERVICES.length) return Promise.reject(new Error('IP geolocation failed'));
      var url = IP_SERVICES[i++];
      return fetch(url, { cache: 'no-store' }).then(function (res) {
        if (!res.ok) return tryNext();
        return res.json().then(function (j) {
          if (!j || j.success === false) return tryNext();
          var lat = Number(j.latitude), lon = Number(j.longitude);
          if (!isFinite(lat) || !isFinite(lon)) return tryNext();
          // Coarse, user-initiated lookup — tag accuracy ~city-level (metres).
          return { coords: { latitude: lat, longitude: lon, accuracy: 50000,
            altitude: null, altitudeAccuracy: null, heading: null, speed: null },
            timestamp: Date.now() };
        });
      }).catch(function () { return tryNext(); });
    }
    return tryNext();
  }
  function patched(success, error, options) {
    function fallback(failCode, failMsg) {
      ipLookup().then(success).catch(function () {
        if (error) error({ code: failCode, message: failMsg });
      });
    }
    if (nativeGet) {
      // 1) NATIVE FIRST — exact existing web / Windows-WebView2 behaviour.
      nativeGet(success, function () {
        // 2) Native failed (incl. macOS WKWebView) -> IP fallback.
        fallback(2, 'Location unavailable (native + IP failed)');
      }, options);
    } else {
      fallback(2, 'Location unavailable (IP lookup failed)');
    }
  }
  if (navigator.geolocation) {
    try { navigator.geolocation.getCurrentPosition = patched; } catch (e) {}
  } else {
    // macOS WKWebView: the geolocation object is absent, so HamClock's
    // `navigator.geolocation ? ... : alert('unsupported')` would alert. Synthesize
    // the object so the happy path is taken and the IP fallback runs.
    try {
      Object.defineProperty(navigator, 'geolocation', {
        value: { getCurrentPosition: patched, watchPosition: function () {}, clearWatch: function () {} },
        configurable: true
      });
    } catch (e) {}
  }
})();
</script>
";

        foreach (var rel in new[] { "dist", "public" })
        {
            try
            {
                var file = Path.Combine(InstallDir, rel, "index.html");
                if (!File.Exists(file)) continue;
                var src = File.ReadAllText(file);
                if (src.Contains("ZEUS_GEO_FALLBACK", StringComparison.Ordinal)) continue; // already patched
                const string anchor = "</head>";
                var idx = src.IndexOf(anchor, StringComparison.Ordinal);
                if (idx < 0)
                {
                    Append($"  (note: couldn't inject geolocation fallback into {rel}/index.html — </head> not found)");
                    continue;
                }
                src = src.Insert(idx, shim);
                File.WriteAllText(file, src);
                Append($"Patched HamClock geolocation fallback into {rel}/index.html.");
            }
            catch (Exception ex)
            {
                Append($"  (geolocation fallback patch skipped for {rel}: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// Inject a server-backed-localStorage shim into HamClock's served HTML so its
    /// settings survive restarts. The desktop webview (WebKit) partitions the
    /// cross-origin HamClock iframe's localStorage as third-party / ephemeral
    /// storage, so it is wiped every launch (settings reset, first-visit popup).
    /// HamClock's own SETTINGS_SYNC saves to disk but does not reliably restore
    /// into localStorage on boot. This shim — running BEFORE HamClock's deferred
    /// module bundle — synchronously pulls /api/settings into localStorage (so the
    /// app boots as a returning visitor with settings intact) and mirrors
    /// openhamclock_*/ohc_* writes back to the server (debounced). Same-origin
    /// (the sidecar's own port), so no CORS. Idempotent via the ZEUS_SETTINGS_PERSIST
    /// marker; re-injected after any rebuild. Requires SETTINGS_SYNC=true (set by
    /// EnsureEnvPort) for the /api/settings store to be live.
    /// </summary>
    private void PatchSettingsPersistence()
    {
        const string shim =
@"<!-- ZEUS_SETTINGS_PERSIST: server-backed localStorage so HamClock settings survive the webview's ephemeral iframe storage (KB2UKA) -->
<script>
(function () {
  function ok(k) {
    return typeof k === 'string'
      && (k.indexOf('openhamclock_') === 0 || k.indexOf('ohc_') === 0)
      && k !== 'openhamclock_profiles' && k !== 'openhamclock_activeProfile';
  }
  // WebKit partitions/blocks the cross-origin HamClock iframe's NATIVE
  // localStorage, so HamClock can't persist anything (first-visit every launch,
  // no save). Replace localStorage with a server-backed store: seed it
  // synchronously from /api/settings (so the app boots as a returning visitor)
  // and mirror writes back to the server (debounced). Runs before HamClock's
  // deferred module bundle, so it transparently uses this instead of the blocked
  // native one. /api/settings persists to disk via SETTINGS_SYNC.
  var store = {};
  try {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', '/api/settings', false);
    xhr.send(null);
    if (xhr.status >= 200 && xhr.status < 300) {
      var data = JSON.parse(xhr.responseText || '{}');
      for (var k in data) {
        if (Object.prototype.hasOwnProperty.call(data, k) && typeof data[k] === 'string') store[k] = data[k];
      }
    }
  } catch (e) {}
  var timer = null;
  function schedule() {
    if (timer) clearTimeout(timer);
    timer = setTimeout(function () {
      timer = null;
      try {
        var out = {};
        for (var k in store) { if (Object.prototype.hasOwnProperty.call(store, k) && ok(k)) out[k] = store[k]; }
        fetch('/api/settings', { method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(out) }).catch(function () {});
      } catch (e) {}
    }, 600);
  }
  var methods = {
    getItem: function (k) { k = String(k); return Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null; },
    setItem: function (k, v) { k = String(k); store[k] = String(v); if (ok(k)) schedule(); },
    removeItem: function (k) { k = String(k); delete store[k]; if (ok(k)) schedule(); },
    clear: function () { for (var k in store) delete store[k]; schedule(); },
    key: function (i) { return Object.keys(store)[i] || null; }
  };
  var backed = new Proxy(methods, {
    get: function (t, p) {
      if (p === 'length') return Object.keys(store).length;
      if (p in t) return t[p];
      return (typeof p === 'string' && Object.prototype.hasOwnProperty.call(store, p)) ? store[p] : undefined;
    },
    set: function (t, p, v) {
      if (p in t) { t[p] = v; return true; }
      var k = String(p); store[k] = String(v); if (ok(k)) schedule(); return true;
    },
    deleteProperty: function (t, p) { var k = String(p); delete store[k]; if (ok(k)) schedule(); return true; },
    has: function (t, p) { return (p in t) || Object.prototype.hasOwnProperty.call(store, p); },
    ownKeys: function () { return Object.keys(store); },
    getOwnPropertyDescriptor: function (t, p) {
      if (Object.prototype.hasOwnProperty.call(store, p)) return { configurable: true, enumerable: true, writable: true, value: store[p] };
      return undefined;
    }
  });
  try { Object.defineProperty(window, 'localStorage', { value: backed, configurable: true }); } catch (e) {}
})();
</script>
";

        foreach (var rel in new[] { "dist", "public" })
        {
            try
            {
                var file = Path.Combine(InstallDir, rel, "index.html");
                if (!File.Exists(file)) continue;
                var src = File.ReadAllText(file);
                // Self-updating: strip any prior version of our block, then inject
                // the current shim — so installs carrying an older shim get the fix.
                var ms = src.IndexOf("<!-- ZEUS_SETTINGS_PERSIST", StringComparison.Ordinal);
                if (ms >= 0)
                {
                    var es = src.IndexOf("</script>", ms, StringComparison.Ordinal);
                    if (es >= 0) src = src.Remove(ms, (es + "</script>".Length) - ms);
                }
                const string anchor = "</head>";
                var idx = src.IndexOf(anchor, StringComparison.Ordinal);
                if (idx < 0)
                {
                    Append($"  (note: couldn't inject settings-persistence into {rel}/index.html — </head> not found)");
                    continue;
                }
                src = src.Insert(idx, shim);
                File.WriteAllText(file, src);
                Append($"Patched HamClock settings persistence into {rel}/index.html.");
            }
            catch (Exception ex)
            {
                Append($"  (settings-persistence patch skipped for {rel}: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// Inject a download-forwarding shim into HamClock's served HTML so the
    /// desktop webview (Photino → WKWebView/WebView2) actually handles attachment
    /// downloads (e.g. HamClock's Rig Bridge installer scripts). The embed iframe
    /// has allow-downloads, but the macOS WKWebView Photino wraps has no download
    /// delegate, so an attachment navigation is silently dropped ("nothing
    /// happens"). This shim intercepts clicks on download links / the rig-bridge
    /// download endpoint and posts the absolute URL up to the Zeus SPA shell, which
    /// hands it to C# (window.external.sendMessage('download:'+url)) to fetch + save
    /// to disk. Same mechanism as the geo / settings patches; idempotent via the
    /// ZEUS_DL_BRIDGE marker; re-injected after any rebuild. No-op in a plain
    /// browser where native downloads already work AND there's no Zeus shell parent.
    /// </summary>
    private void PatchDownloadBridge()
    {
        const string shim =
@"<!-- ZEUS_DL_BRIDGE: forward attachment downloads to the Zeus desktop shell so the webview saves them (KB2UKA) -->
<script>
(function () {
  document.addEventListener('click', function (e) {
    try {
      var a = e.target && e.target.closest ? e.target.closest('a') : null;
      if (!a) return;
      var href = a.getAttribute('href') || a.href || '';
      if (!href) return;
      var isDownload = a.hasAttribute('download') || /\/api\/rig-bridge\/download\//.test(href);
      if (!isDownload) return;
      // Only intercept when running inside the Zeus desktop shell (a parent
      // window we can postMessage to). In a standalone browser, let the native
      // download proceed untouched.
      if (window.parent === window) return;
      e.preventDefault();
      var abs = new URL(href, location.href).href;
      window.parent.postMessage({ zeusDownload: abs }, '*');
    } catch (err) {}
  }, true);
})();
</script>
";

        foreach (var rel in new[] { "dist", "public" })
        {
            try
            {
                var file = Path.Combine(InstallDir, rel, "index.html");
                if (!File.Exists(file)) continue;
                var src = File.ReadAllText(file);
                if (src.Contains("ZEUS_DL_BRIDGE", StringComparison.Ordinal)) continue; // already patched
                const string anchor = "</head>";
                var idx = src.IndexOf(anchor, StringComparison.Ordinal);
                if (idx < 0)
                {
                    Append($"  (note: couldn't inject download bridge into {rel}/index.html — </head> not found)");
                    continue;
                }
                src = src.Insert(idx, shim);
                File.WriteAllText(file, src);
                Append($"Patched HamClock download bridge into {rel}/index.html.");
            }
            catch (Exception ex)
            {
                Append($"  (download bridge patch skipped for {rel}: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// Seed HamClock's Rig Control setting so clicking a spot tunes the Zeus
    /// radio with no operator setup. HamClock stores all UI prefs in a single
    /// JSON blob under the localStorage key <c>openhamclock_config</c>, mirrored
    /// server-side by its <c>/api/settings</c> store (SETTINGS_SYNC, already
    /// enabled by EnsureEnvPort). Inside that blob, <c>rigControl</c> is
    /// <c>{ enabled, host, port, tuneEnabled, autoMode, apiToken }</c>. We GET the
    /// current blob, merge our rigControl (pointing at the rig-bridge agent on
    /// :5555 with the SAME api token RigBridgeService writes into the agent
    /// config), and POST it back. The ZEUS_SETTINGS_PERSIST shim then seeds this
    /// into localStorage at HamClock boot, so it reads the rigControl on first paint.
    /// Merge-safe — never overwrites the blob's other keys (callsign, map prefs).
    /// </summary>
    private void SeedRigControl(int port)
    {
        var token = _rigBridge.GetOrCreateApiToken();
        var baseUrl = $"http://127.0.0.1:{port}";

        // 1. Read the existing settings store, extract the current config blob.
        var settings = new System.Text.Json.Nodes.JsonObject();
        try
        {
            var raw = Http.GetStringAsync($"{baseUrl}/api/settings").GetAwaiter().GetResult();
            if (System.Text.Json.Nodes.JsonNode.Parse(raw) is System.Text.Json.Nodes.JsonObject obj)
                settings = obj;
        }
        catch { /* store may be empty / not yet written — start fresh */ }

        System.Text.Json.Nodes.JsonObject config;
        try
        {
            // Stored as a JSON *string* (the only value shape /api/settings accepts).
            var inner = settings["openhamclock_config"]?.GetValue<string>();
            config = (inner is not null
                ? System.Text.Json.Nodes.JsonNode.Parse(inner) as System.Text.Json.Nodes.JsonObject
                : null) ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch { config = new System.Text.Json.Nodes.JsonObject(); }

        // 2. Merge our rigControl.
        config["rigControl"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true,
            ["host"] = "http://localhost",
            ["port"] = RigBridgeService.BridgePort,
            ["tuneEnabled"] = true,
            ["autoMode"] = false,
            ["apiToken"] = token,
        };

        // 3. Re-stringify the blob and POST the full settings set back (the store
        //    replaces wholesale, so we send every key we read plus our update).
        settings["openhamclock_config"] = config.ToJsonString();
        try
        {
            var body = settings.ToJsonString();
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync($"{baseUrl}/api/settings", content).GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
                Append("Seeded HamClock Rig Control (click-to-tune → rig-bridge :5555 → TCI).");
            else
                Append($"  (rigControl seed POST returned {(int)resp.StatusCode})");
        }
        catch (Exception ex)
        {
            Append($"  (rigControl seed POST failed: {ex.Message})");
        }
    }

    /// <summary>Replace the first uncommented <c>KEY=</c> line, or append one.</summary>
    private static string UpsertEnvKey(string content, string key, string value)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        bool found = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith('#') && trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                lines[i] = $"{key}={value}";
                found = true;
                break;
            }
        }
        if (!found) lines.Add($"{key}={value}");
        return string.Join("\n", lines);
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Prefer the port persisted in <c>.env</c> when it's still free, so the
    /// HamClock loopback origin is stable across restarts (its UI settings live
    /// in per-origin localStorage). Falls back to a fresh free port on first
    /// run or if the saved one is in use.
    /// </summary>
    private static int ResolveStablePort()
    {
        var envPath = Path.Combine(InstallDir, ".env");
        if (File.Exists(envPath))
        {
            foreach (var raw in File.ReadLines(envPath))
            {
                var line = raw.AsSpan().Trim();
                if (line.StartsWith("PORT=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[5..].Trim(), out var saved) &&
                    saved is > 0 and <= 65535 && IsPortFree(saved))
                {
                    return saved;
                }
            }
        }
        return FreeTcpPort();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void Append(string line)
    {
        _log.LogInformation("HamClock: {Line}", line);
        lock (_gate)
        {
            _logLines.AddLast(line);
            while (_logLines.Count > MaxLogLines) _logLines.RemoveFirst();
        }
    }

    private void Fail(string message)
    {
        Append("ERROR: " + message);
        lock (_gate) { _phase = HamClockPhase.Error; _error = message; _busy = false; }
    }

    // -- IHostedService (clean shutdown only) ----------------------------

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct)
    {
        Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
