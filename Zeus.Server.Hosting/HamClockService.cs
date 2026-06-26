// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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
    // HamClock persists most UI preferences in browser localStorage. Because
    // localStorage is scoped by origin, including port, Zeus should keep the
    // sidecar port stable across restarts whenever possible.
    internal const int DefaultStablePort = 59950;
    private const string PortOverrideEnvVar = "ZEUS_HAMCLOCK_PORT";

    // Single client for download + health-poll. GitHub codeload follows a
    // redirect to its asset CDN; HttpClient follows it by default.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly ILogger<HamClockService> _log;
    private readonly object _gate = new();
    private readonly LinkedList<string> _logLines = new();

    private HamClockPhase _phase = HamClockPhase.NotInstalled;
    private string? _error;
    private int _port;
    private Process? _proc;
    private bool _busy; // an install or start is in flight

    // Resolved Node runtime. _nodeDir = the private Node directory we downloaded;
    // null = use whatever 'node' is on the system PATH. Set by EnsureNodeAsync.
    // _nodeInfo is the cached "(available, version)" for Snapshot so status
    // polls don't spawn `node --version` on every request.
    private string? _nodeDir;
    private bool _nodeBundled;
    private readonly object _nodeGate = new();
    private (bool ok, string? version) _nodeInfo;
    private bool _nodeProbed;

    // Pinned portable Node (current LTS). Downloaded into PortableNodeRoot when
    // the system has no Node, so Install works on a machine with none
    // preinstalled. Override the version only by editing this constant.
    //
    // MUST be >= MinNodeVersion: HamClock's deps (axios-cookiejar-support,
    // @electron/rebuild) are ESM-only and are reached via require(), which Node
    // only permits unflagged from v22.12.0 on. v22.11.0 throws ERR_REQUIRE_ESM
    // at server startup ("require() of ES Module ... not supported").
    private const string PortableNodeVersion = "v22.23.0";

    // Minimum Node that can run HamClock without ERR_REQUIRE_ESM (see above).
    // A resolved Node below this is rejected in favour of the bundled copy.
    private static readonly Version MinNodeVersion = new(22, 12, 0);

    public HamClockService(ILogger<HamClockService> log)
    {
        _log = log;
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

    /// <summary>
    /// The TCP port the sidecar is currently listening on, or null when it is
    /// not running. Used by <see cref="PropagationService"/> to reach the
    /// HamClock REST API (solar + ITU-R P.533-14 propagation) on localhost.
    /// </summary>
    public int? RunningPort
    {
        get
        {
            lock (_gate)
            {
                return _proc is { HasExited: false } ? _port : null;
            }
        }
    }

    /// <summary>
    /// The port the sidecar would normally bind: the <c>ZEUS_HAMCLOCK_PORT</c>
    /// override when valid, otherwise <see cref="DefaultStablePort"/>. Used as a
    /// best-effort fallback by <see cref="PropagationService"/> so propagation
    /// works even when the sidecar was started outside this Zeus process — the
    /// caller treats a failed localhost probe as "unavailable", so guessing the
    /// port is harmless.
    /// </summary>
    public int ConfiguredPort
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(PortOverrideEnvVar);
            return int.TryParse(raw, out var p) && p is > 0 and <= 65535 ? p : DefaultStablePort;
        }
    }

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
            PatchZeusCatBridge();

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

            var port = ResolvePortForStart(
                Environment.GetEnvironmentVariable(PortOverrideEnvVar),
                ReadEnvPort(),
                IsTcpPortAvailable,
                FreeTcpPort,
                Append);
            Append($"Starting HamClock server on port {port}…");

            // HamClock's own server/config loads .env with precedence over
            // process.env, and creates .env from .env.example on first run.
            // Passing PORT via the environment is not enough — write the
            // chosen stable port into .env, and enable HamClock's server-side
            // settings sync so preferences also persist in data/settings.json.
            // server.js binds app.listen(PORT, '0.0.0.0'), so port availability
            // must be checked against all IPv4 interfaces.
            EnsureEnvSettings(port);
            // Covers installs that predate the frameguard patch (idempotent).
            PatchHelmetFrameguard();
            // Covers installs that predate the Zeus CAT bridge (idempotent).
            PatchZeusCatBridge();

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

    /// <summary>True if a "vX.Y.Z" Node version string is at least
    /// <see cref="MinNodeVersion"/>. Tolerant of a missing 'v' prefix and any
    /// prerelease/build suffix; an unparseable string fails closed (false).</summary>
    internal static bool NodeMeetsMinimum(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(['-', '+']);
        if (cut >= 0) v = v[..cut];
        return Version.TryParse(v, out var parsed) && parsed >= MinNodeVersion;
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
        if (ok && NodeMeetsMinimum(ver))
        {
            Append($"Using {(_nodeBundled ? "bundled" : "system")} Node {ver}.");
            SetNodeInfo((true, ver));
            return true;
        }
        if (ok)
            Append($"System Node {ver} is older than the required {MinNodeVersion} — using a private copy for HamClock.");

        // (2) A private Node from a previous install.
        var portable = FindPortableNodeBinDir();
        if (portable is not null)
        {
            _nodeDir = portable;
            (ok, ver) = DetectNode();
            if (ok && NodeMeetsMinimum(ver))
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
    /// Unix: its <c>bin/</c> subdir. Matches only the currently pinned
    /// <see cref="PortableNodeVersion"/> dir, so bumping that constant makes an
    /// existing install fall through to a fresh download instead of reusing a
    /// stale (e.g. ERR_REQUIRE_ESM-prone) Node left on disk.</summary>
    private static string? FindPortableNodeBinDir()
    {
        try
        {
            if (!Directory.Exists(PortableNodeRoot)) return null;
            var (_, _, _, dirName) = PortableNodeArtifact();
            var dir = Path.Combine(PortableNodeRoot, dirName);
            var binDir = OperatingSystem.IsWindows() ? dir : Path.Combine(dir, "bin");
            var exe = Path.Combine(binDir, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(exe)) return binDir;
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
    /// Build a ProcessStartInfo that resolves PATH-installed tools on every
    /// OS. npm/npx are run by invoking their JS entrypoint with node directly
    /// (node "…/npm-cli.js" …) rather than through the npm/npm.cmd shim, so the
    /// working directory can never hijack which npm runs; node.exe resolves
    /// directly. See <see cref="CreateToolProcessStartInfo"/>.
    /// </summary>
    private ProcessStartInfo MakePsi(string tool, string args, string cwd)
    {
        return CreateToolProcessStartInfo(tool, args, cwd, _nodeDir);
    }

    internal static ProcessStartInfo CreateToolProcessStartInfo(string tool, string args, string cwd, string? nodeDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // For npm/npx, run their JS entrypoint with node directly
        // (node "<…>/npm-cli.js" <args>) instead of going through the
        // npm / npm.cmd shim. cmd.exe resolves a bare `npm` against the working
        // directory *before* PATH, so a stray or half-written node_modules\npm
        // in the install dir (e.g. left by an interrupted `npm ci`) can shadow
        // the real npm and fail with "Cannot find module npm-cli.js". Calling
        // node with an absolute entrypoint removes every shim / PATH / CWD
        // ambiguity, and is identical on Windows, macOS and Linux.
        if (tool is "npm" or "npx")
        {
            var nodeExe = ResolveNodeExe(nodeDir);
            var cliJs = nodeExe is null ? null : ResolveNpmCliJs(nodeExe, tool);
            if (nodeExe is not null && cliJs is not null)
            {
                psi.FileName = nodeExe;
                var quotedCli = $"\"{cliJs}\"";
                psi.Arguments = args.Length == 0 ? quotedCli : $"{quotedCli} {args}";
                if (nodeDir is not null)
                    PrependPath(psi, nodeDir);
                return psi;
            }
        }

        // Fallback (and the normal path for node/tar/etc.): the npm/npx CLI
        // entrypoint couldn't be located, so fall back to the shim. npm is a
        // .cmd shim on Windows (not a PATH-resolvable .exe), so it must be
        // invoked through cmd.exe; node.exe and tar resolve directly.
        var bundledTool = nodeDir is null ? null : ResolveBundledToolPath(tool, nodeDir);
        if (OperatingSystem.IsWindows() && (tool is "npm" or "npx"))
        {
            psi.FileName = "cmd.exe";
            var command = bundledTool ?? tool;
            psi.Arguments = args.Length == 0
                ? $"/d /s /c \"\"{command}\"\""
                : $"/d /s /c \"\"{command}\" {args}\"";
        }
        else
        {
            psi.FileName = bundledTool ?? tool;
            psi.Arguments = args;
        }
        // When using a private Node, prepend its bin dir to the child's PATH so
        // `node`, `npm`, and `npm.cmd` resolve to it (npm lives beside node in
        // the portable archive, not on the system PATH).
        if (nodeDir is not null)
            PrependPath(psi, nodeDir);
        return psi;
    }

    /// <summary>
    /// Resolve the absolute path to the node executable to run: the bundled copy
    /// when <paramref name="nodeDir"/> is set, otherwise the first <c>node</c>
    /// found on PATH. Returns null if none is found.
    /// </summary>
    private static string? ResolveNodeExe(string? nodeDir)
    {
        var exeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        if (nodeDir is not null)
        {
            var bundled = Path.Combine(nodeDir, exeName);
            return File.Exists(bundled) ? bundled : null;
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), exeName); }
            catch { continue; } // malformed PATH entry — skip
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Resolve the absolute path to npm-cli.js / npx-cli.js for the given node
    /// executable. Checks the Windows / portable-archive layout (npm beside
    /// node) and the Unix system layout (node in <c>&lt;prefix&gt;/bin</c>, npm
    /// under <c>&lt;prefix&gt;/lib</c>). Returns null if not found.
    /// </summary>
    private static string? ResolveNpmCliJs(string nodeExe, string tool)
    {
        var cliName = tool == "npx" ? "npx-cli.js" : "npm-cli.js";
        var dir = Path.GetDirectoryName(nodeExe);
        if (string.IsNullOrEmpty(dir)) return null;
        string[] candidates =
        [
            Path.Combine(dir, "node_modules", "npm", "bin", cliName),              // Windows install / portable archive
            Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", cliName), // Unix system (node in <prefix>/bin)
        ];
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); }
            catch { /* malformed candidate — try the next */ }
        }
        return null;
    }

    private static string? ResolveBundledToolPath(string tool, string nodeDir)
    {
        var fileName = OperatingSystem.IsWindows()
            ? tool switch
            {
                "node" => "node.exe",
                "npm" => "npm.cmd",
                "npx" => "npx.cmd",
                _ => tool,
            }
            : tool;
        var candidate = Path.Combine(nodeDir, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static void PrependPath(ProcessStartInfo psi, string directory)
    {
        var pathKey = psi.Environment.Keys
            .FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase))
            ?? "PATH";
        var existing = psi.Environment.TryGetValue(pathKey, out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH");
        psi.Environment[pathKey] = directory + Path.PathSeparator + (existing ?? string.Empty);
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
    /// .env.example if .env doesn't exist yet, then upserts the Zeus-managed
    /// keys, preserving every other line.
    /// </summary>
    private static void EnsureEnvSettings(int port)
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
        content = BuildEnvContent(content, port);
        File.WriteAllText(envPath, content);
    }

    internal static string BuildEnvContent(string content, int port)
    {
        content = UpsertEnvKey(content, "PORT", port.ToString());
        content = UpsertEnvKey(content, "AUTO_UPDATE_ENABLED", "false");
        content = UpsertEnvKey(content, "SETTINGS_SYNC", "true");
        return content;
    }

    private static int? ReadEnvPort()
    {
        var envPath = Path.Combine(InstallDir, ".env");
        if (!File.Exists(envPath)) return null;
        return ReadEnvPortFromContent(File.ReadAllText(envPath));
    }

    internal static int? ReadEnvPortFromContent(string content)
    {
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith('#')) continue;
            if (!line.StartsWith("PORT=", StringComparison.Ordinal)) continue;
            var value = line["PORT=".Length..].Trim().Trim('"', '\'');
            return TryParsePort(value, out var port) ? port : null;
        }
        return null;
    }

    internal static int ResolvePortForStart(
        string? overridePort,
        int? savedPort,
        Func<int, bool> isAvailable,
        Func<int> freePort,
        Action<string>? note = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePort))
        {
            if (TryParsePort(overridePort, out var configured))
            {
                if (isAvailable(configured)) return configured;
                note?.Invoke($"{PortOverrideEnvVar}={configured} is unavailable; falling back.");
            }
            else
            {
                note?.Invoke($"{PortOverrideEnvVar} is not a valid TCP port; falling back.");
            }
        }

        if (savedPort is int saved)
        {
            if (isAvailable(saved)) return saved;
            note?.Invoke($"Saved HamClock port {saved} is unavailable; selecting another port.");
        }

        if (isAvailable(DefaultStablePort)) return DefaultStablePort;

        var fallback = freePort();
        note?.Invoke($"Default HamClock port {DefaultStablePort} is unavailable; using {fallback}.");
        return fallback;
    }

    internal static bool TryParsePort(string? raw, out int port)
    {
        if (int.TryParse(raw, out port) && port is > 0 and <= 65535)
            return true;
        port = 0;
        return false;
    }

    internal const string ZeusCatBridgeScriptName = "zeus-cat-bridge.js";

    private const string ZeusCatBridgeScript = """
        (() => {
          const messageType = 'zeus.hamclock.dxSpotTune';
          if (window.__zeusHamClockCatBridgeInstalled) return;
          window.__zeusHamClockCatBridgeInstalled = true;

          const textOf = (node) => (node?.innerText || node?.textContent || '').replace(/\s+/g, ' ').trim();

          const parseFreqHz = (text) => {
            const match = String(text || '').match(/(\d+(?:\.\d+)?)/);
            if (!match) return null;
            const value = Number(match[1]);
            if (!Number.isFinite(value) || value <= 0) return null;
            const lower = String(text || '').toLowerCase();
            if (lower.includes('khz')) return Math.round(value * 1000);
            return Math.round(value >= 1000 ? value * 1000 : value * 1000000);
          };

          const isDxClusterTable = (node) => {
            const label = node?.getAttribute?.('aria-label') || '';
            if (/dx\s*cluster/i.test(label)) return true;
            return /dx\s*cluster/i.test(textOf(node).slice(0, 240));
          };

          document.addEventListener('click', (event) => {
            const row = event.target?.closest?.('[role="row"]');
            if (!row || row.querySelector('[role="columnheader"]')) return;

            const table = row.closest('[role="table"]');
            if (!table || !isDxClusterTable(table)) return;

            const cells = Array.from(row.querySelectorAll('[role="cell"]'));
            if (cells.length < 2) return;

            const freqHz = parseFreqHz(textOf(cells[0]));
            if (!freqHz) return;

            const modeMatch = textOf(cells[2]).match(/[A-Za-z0-9+/-]+/);
            const callsign = textOf(cells[1]).match(/[A-Z0-9/]+/i)?.[0]?.toUpperCase() || 'DX';
            window.parent?.postMessage(
              {
                type: messageType,
                source: 'DX',
                freqHz,
                mode: modeMatch ? modeMatch[0].toUpperCase() : '',
                callsign,
              },
              '*',
            );
          }, true);
        })();
        """;

    internal static string InjectZeusCatBridgeTag(string html)
    {
        if (html.Contains(ZeusCatBridgeScriptName, StringComparison.Ordinal))
            return html;

        var tag = $"<script src=\"/{ZeusCatBridgeScriptName}\" defer></script>";
        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0)
            return html.Insert(headEnd, $"  {tag}\n  ");

        return html + "\n" + tag + "\n";
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
    /// Inject a tiny bridge into HamClock's built frontend so DX-cluster row
    /// clicks can ask the parent Zeus iframe host to tune through Zeus CAT.
    /// This avoids depending on OpenHamClock's standalone rig-bridge settings.
    /// </summary>
    private void PatchZeusCatBridge()
    {
        try
        {
            var dist = Path.Combine(InstallDir, "dist");
            var index = Path.Combine(dist, "index.html");
            if (!File.Exists(index)) return;

            Directory.CreateDirectory(dist);
            File.WriteAllText(Path.Combine(dist, ZeusCatBridgeScriptName), ZeusCatBridgeScript);

            var html = File.ReadAllText(index);
            var updated = InjectZeusCatBridgeTag(html);
            if (!ReferenceEquals(updated, html) && updated != html)
            {
                File.WriteAllText(index, updated);
                Append("Patched HamClock DX spots to notify Zeus click-to-tune.");
            }
        }
        catch (Exception ex)
        {
            Append($"  (Zeus CAT bridge patch skipped: {ex.Message})");
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

    private static bool IsTcpPortAvailable(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
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
