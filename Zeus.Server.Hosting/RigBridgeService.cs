// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// RigBridgeService — the second Zeus-supervised Node sidecar that makes
// HamClock "click-to-tune" work with zero operator setup.
//
// HamClock's "Rig Control" feature POSTs the clicked-spot frequency to a small
// local HTTP daemon — the OpenHamClock "Rig Bridge" agent (bundled inside the
// HamClock install at hamclock/rig-bridge/rig-bridge.js). That agent, when its
// radio plugin is `tci`, opens a TCI WebSocket to an SDR's TCI server and sends
// `VFO:<trx>,<vfo>,<hz>;`. Zeus already runs an ExpertSDR3-compatible TCI server
// on :40001 (Zeus.Server.Tci.TciServer); its TciSession.HandleVfo applies the
// inbound VFO set via RadioService.SetVfo(fromExternal: true). So the chain is:
//
//   HamClock spot click → POST localhost:5555/freq
//     → rig-bridge (radio.type=tci) → ws://localhost:40001  VFO:0,0,<hz>;
//       → Zeus TciServer → RadioService.SetVfo → radio tunes.
//
// Without this service the operator would have to (1) enable Zeus's TCI server,
// (2) download + run the rig-bridge agent in TCI mode, and (3) point HamClock's
// Rig Bridge setting at :5555 with a matching API token. This service automates
// all three: Zeus binds :40001 in desktop mode (see ZeusHost), spawns + supervises
// the BUNDLED rig-bridge.js (same Node runtime + lifecycle as HamClockService),
// writes its config (radio.type=tci, tci.port=40001, bridge port 5555, a stable
// API token), and HamClockService seeds HamClock's rigControl setting with the
// matching token. Net: install HamClock → click a spot → radio tunes.
//
// Nothing here touches the radio / DSP / TX path. It is inert until HamClock is
// started (HamClockService.StartAsync calls StartAsync here), lives and dies
// with HamClock, and is also killed on Zeus shutdown (IHostedService.StopAsync).

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// Owns the OpenHamClock "Rig Bridge" agent sidecar in TCI mode, wiring
/// HamClock click-to-tune to Zeus's TCI server. Singleton +
/// <see cref="IHostedService"/> (only for clean shutdown — it never
/// auto-starts; HamClockService starts/stops it alongside HamClock).
/// </summary>
public sealed class RigBridgeService : IHostedService, IAsyncDisposable
{
    // The bridge's local HTTP port HamClock POSTs spot frequencies to. 5555 is
    // the rig-bridge agent's own default and the value HamClock's Rig Control
    // setting defaults to, so the operator never has to change it.
    public const int BridgePort = 5555;

    // Zeus's TCI server port (ExpertSDR3-compatible). Must match the port Zeus
    // binds in ZeusHost (Tci:Port / desktop auto-bind).
    private const int TciPort = 40001;

    private const int MaxLogLines = 200;

    private readonly ILogger<RigBridgeService> _log;
    // HamClockService and RigBridgeService reference each other (HamClock starts/
    // stops the bridge; the bridge borrows HamClock's resolved Node runtime), which
    // is a constructor-injection cycle the DI container rejects. Break it by lazily
    // resolving HamClockService from the provider on first use — see
    // docs/lessons feedback_di_cycle_iservice_provider.
    private readonly IServiceProvider _services;
    private HamClockService? _hamclockCached;
    private readonly object _gate = new();
    private readonly LinkedList<string> _logLines = new();
    private Process? _proc;

    public RigBridgeService(ILogger<RigBridgeService> log, IServiceProvider services)
    {
        _log = log;
        _services = services;
    }

    private HamClockService HamClock =>
        _hamclockCached ??= _services.GetRequiredService<HamClockService>();

    /// <summary>The bundled rig-bridge dir inside the HamClock install.</summary>
    private static string RigBridgeDir => Path.Combine(HamClockService.InstallDir, "rig-bridge");

    /// <summary>The agent entrypoint (node rig-bridge.js).</summary>
    private static string Entry => Path.Combine(RigBridgeDir, "rig-bridge.js");

    /// <summary>True once the bundled rig-bridge agent is present on disk.</summary>
    public static bool IsAvailable => File.Exists(Entry);

    /// <summary>True while the bridge child process is alive.</summary>
    public bool Running { get { lock (_gate) return _proc is { HasExited: false }; } }

    // -- API token --------------------------------------------------------

    /// <summary>
    /// A stable hex API token shared by the rig-bridge config (radio write auth)
    /// and HamClock's rigControl.apiToken. The rig-bridge agent auto-generates
    /// (and enforces) a token on /freq when its config token is empty, so we must
    /// pre-seed a KNOWN token on both sides or HamClock's POST /freq would 401.
    /// Persisted in a small file under the rig-bridge dir so it survives restarts.
    /// </summary>
    public string GetOrCreateApiToken()
    {
        var tokenFile = Path.Combine(RigBridgeDir, ".zeus-rig-token");
        try
        {
            if (File.Exists(tokenFile))
            {
                var existing = File.ReadAllText(tokenFile).Trim();
                if (existing.Length >= 16) return existing;
            }
        }
        catch { /* fall through to regenerate */ }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(RigBridgeDir);
            File.WriteAllText(tokenFile, token);
        }
        catch (Exception ex)
        {
            Append($"  (could not persist rig-bridge token: {ex.Message})");
        }
        return token;
    }

    // -- Config -----------------------------------------------------------

    /// <summary>
    /// The agent's external config path: ~/.config/openhamclock/rig-bridge-config.json
    /// on mac/Linux, %APPDATA%\openhamclock\rig-bridge-config.json on Windows.
    /// This mirrors the agent's own resolveConfigPath() (core/config.js), which
    /// reads this file verbatim — so we write our TCI config here directly rather
    /// than into the read-only installed tree.
    /// </summary>
    private static string ConfigPath()
    {
        string dir = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetEnvironmentVariable("APPDATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming"),
                "openhamclock")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "openhamclock");
        return Path.Combine(dir, "rig-bridge-config.json");
    }

    /// <summary>
    /// Write (or merge into) the agent's config so it runs in TCI mode pointed at
    /// Zeus's TCI server, with our stable API token. Merge-safe: preserves any
    /// keys the operator may have set, only forcing the radio.type/tci/apiToken
    /// that make Zeus auto-linking work. configVersion 8 matches the agent's
    /// current schema (core/config.js CONFIG_VERSION) so it doesn't run a migration.
    /// </summary>
    private void WriteConfig(string token)
    {
        var path = ConfigPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            JsonObject root;
            if (File.Exists(path))
            {
                try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
                catch { root = new JsonObject(); }
            }
            else
            {
                root = new JsonObject();
            }

            root["configVersion"] = 8;
            root["port"] = BridgePort;
            root["bindAddress"] = "127.0.0.1";
            root["apiToken"] = token;
            // Mark the token as already shown so the agent's setup UI gates normally
            // rather than flashing a first-run "copy this token" banner.
            root["tokenDisplayed"] = true;
            root["logging"] = true;

            var radio = root["radio"] as JsonObject ?? new JsonObject();
            radio["type"] = "tci";
            root["radio"] = radio;

            var tci = root["tci"] as JsonObject ?? new JsonObject();
            tci["host"] = "localhost";
            tci["port"] = TciPort;
            tci["trx"] = 0;
            tci["vfo"] = 0;
            root["tci"] = tci;

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Append($"Wrote rig-bridge config (radio.type=tci, tci.port={TciPort}) to {path}.");
        }
        catch (Exception ex)
        {
            Append($"  (rig-bridge config write skipped: {ex.Message})");
        }
    }

    // -- Start / Stop -----------------------------------------------------

    /// <summary>
    /// Write the config and spawn `node rig-bridge.js --port 5555 --bind 127.0.0.1`
    /// using HamClock's resolved Node. Idempotent: a no-op if already running or
    /// if the bundled agent isn't present (older HamClock installs). Never throws —
    /// click-to-tune is a best-effort convenience, not a launch blocker.
    /// </summary>
    public async Task StartAsync()
    {
        lock (_gate) { if (_proc is { HasExited: false }) return; }

        if (!IsAvailable)
        {
            Append("rig-bridge agent not present in this HamClock install — click-to-tune unavailable.");
            return;
        }

        var token = GetOrCreateApiToken();
        WriteConfig(token);

        try
        {
            var psi = await HamClock
                .MakeNodePsiAsync("node", $"\"{Entry}\" --port {BridgePort} --bind 127.0.0.1", RigBridgeDir)
                .ConfigureAwait(false);
            if (psi is null)
            {
                Append("Node unavailable — rig-bridge not started (reinstall HamClock from Settings).");
                return;
            }

            // The agent's TCI plugin requires the `ws` npm package; it resolves
            // from the parent hamclock/node_modules via Node's upward walk (cwd is
            // RigBridgeDir). NODE_PATH is belt-and-suspenders for that resolution.
            psi.Environment["NODE_PATH"] = Path.Combine(HamClockService.InstallDir, "node_modules");
            psi.Environment["NODE_ENV"] = "production";

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Append("[rb] " + e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append("[rb!] " + e.Data); };
            proc.Exited += (_, _) => Append("rig-bridge exited.");

            if (!proc.Start())
            {
                Append("Failed to start rig-bridge (node).");
                return;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            lock (_gate) { _proc = proc; }

            // Best-effort health probe — the agent retries the TCI WS on its own
            // every few seconds, so we don't block on it.
            _ = await WaitForListeningAsync(BridgePort, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Append($"rig-bridge running on :{BridgePort} (TCI → :{TciPort}).");
        }
        catch (Exception ex)
        {
            Append($"  (rig-bridge start error: {ex.Message})");
        }
    }

    /// <summary>Kill the rig-bridge child if running. Idempotent.</summary>
    public void Stop()
    {
        Process? proc;
        lock (_gate) { proc = _proc; _proc = null; }
        if (proc is { HasExited: false })
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        proc?.Dispose();
    }

    // -- Helpers ----------------------------------------------------------

    private static async Task<bool> WaitForListeningAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                var done = await Task.WhenAny(connect, Task.Delay(1000)).ConfigureAwait(false);
                if (done == connect && client.Connected)
                {
                    await connect.ConfigureAwait(false);
                    return true;
                }
            }
            catch { /* not listening yet */ }
            await Task.Delay(300).ConfigureAwait(false);
        }
        return false;
    }

    private void Append(string line)
    {
        _log.LogInformation("RigBridge: {Line}", line);
        lock (_gate)
        {
            _logLines.AddLast(line);
            while (_logLines.Count > MaxLogLines) _logLines.RemoveFirst();
        }
    }

    // -- IHostedService (clean shutdown only) -----------------------------

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
