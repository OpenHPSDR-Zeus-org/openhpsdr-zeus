// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Server;
using Zeus.Support.Contracts;

namespace OpenhpsdrZeus;

/// <summary>
/// Spawns the out-of-process support sidecar (Zeus.SupportAgent) from the desktop
/// host. The sidecar is launched DETACHED — a plain child process, not killed
/// when this process dies — so it survives a backend crash and can capture the
/// crash. We hand it every path it needs as arguments (the host owns the
/// platform data-dir logic); it never re-derives them.
///
/// The sidecar is a diagnostics enhancement: any failure to launch is logged and
/// swallowed and MUST never block or slow Zeus startup.
/// </summary>
internal static class SupportSidecarLauncher
{
    /// <summary>
    /// Same broker default + override as <c>RemoteBrokerClient</c>: a <c>wss</c>
    /// signaling URL. The sidecar derives the <c>https</c> origin for its
    /// presence/crash POSTs from it. Honour <c>ZEUS_REMOTE_BROKER_URL</c> so a
    /// staging broker can be pointed at without a rebuild.
    /// </summary>
    private static string BrokerUrl() =>
        Environment.GetEnvironmentVariable("ZEUS_REMOTE_BROKER_URL")
            ?? "wss://remote.openhpsdrzeus.com/signal?role=host";

    /// <param name="services">
    /// The running host's service provider, used to resolve the operator's QRZ
    /// identity (callsign + session key) so the sidecar can authenticate its
    /// presence/crash calls. May be null (e.g. a host build without DI wired);
    /// the sidecar then runs local-only crash capture.
    /// </param>
    public static void TryLaunch(IServiceProvider? services = null)
    {
        try
        {
            var exe = ResolveSidecarExe();
            if (exe is null)
            {
                Console.Error.WriteLine(
                    "support-agent: sidecar executable not found next to host; crash capture disabled this session.");
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            void Add(string key, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                psi.ArgumentList.Add(key);
                psi.ArgumentList.Add(value);
            }

            Add("--supervise-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            // Use the IPC bridge's session token so the sidecar listens on the SAME
            // pipe the backend bridge connects to (remote-diag P3c). Falls back to a
            // fresh token when no bridge is registered (non-desktop host) — the
            // sidecar then runs without live posture updates, local crash-capture
            // only, exactly as before P3c.
            var bridge = services?.GetService<Zeus.Server.Hosting.Support.SupportSidecarBridge>();
            Add("--session", bridge?.SessionToken ?? Guid.NewGuid().ToString("N"));
            Add("--app-log", PrefsDbPath.AppLogPath());
            Add("--startup-log", StartupDiagnostics.LogPath);
            Add("--crash-dir", PrefsDbPath.CrashDir());
            Add("--app-version", ResolveAppVersion());

            // Remote presence/crash identity. Resolve the operator's QRZ identity
            // best-effort; if they are signed in, hand the sidecar the broker URL +
            // callsign (args) and the session key (env var, kept off the process
            // table). Availability + crash auto-share START OFF — the backend turns
            // them on at runtime via the SupportStateChanged IPC, so presence never
            // advertises and crashes never upload until the operator opts in.
            Add("--broker-url", BrokerUrl());
            var identity = TryResolveQrzIdentity(services);
            if (identity is not null)
            {
                Add("--operator-callsign", identity.Value.Callsign);
                psi.Environment[SupportIpc.SidecarQrzSessionEnvVar] = identity.Value.SessionKey;
            }
            Add("--remote-diagnostics", "off");
            Add("--auto-share-crash", "off");

            Process.Start(psi);
            StartupDiagnostics.Log($"support-agent: launched sidecar to supervise pid {Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"support-agent: launch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort QRZ identity for the sidecar's broker auth. Bounded so a slow
    /// QRZ session-key fetch never delays launch; any failure just leaves the
    /// sidecar local-only.
    /// </summary>
    private static (string Callsign, string SessionKey)? TryResolveQrzIdentity(IServiceProvider? services)
    {
        if (services is null) return null;
        try
        {
            var qrz = services.GetService<QrzService>();
            if (qrz is null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return qrz.GetChatIdentityAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveSidecarExe()
    {
        var name = OperatingSystem.IsWindows() ? "Zeus.SupportAgent.exe" : "Zeus.SupportAgent";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveAppVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm?.GetName().Version?.ToString();
    }
}
