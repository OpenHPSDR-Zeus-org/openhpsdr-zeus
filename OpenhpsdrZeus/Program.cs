// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR (https://github.com/dl1bz/deskhpsdr),
// maintained by Heiko (DL1BZ). Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Photino.NET;
using Zeus.Server;

// Single binary, three modes:
//   OpenhpsdrZeus              → service mode (LAN HTTP + HTTPS, console banner).
//                                 Headless-friendly — what a Raspberry-Pi-shack or
//                                 a Docker container runs.
//   OpenhpsdrZeus --desktop    → Photino shell (loopback HTTP for the webview,
//                                 plus LAN HTTPS so a phone can pick up the
//                                 session while the operator is away from the
//                                 shack PC — see ShareOverLan).
//   OpenhpsdrZeus --server     → service mode + a small Photino status window
//                                 showing the bound URLs and a "Stop Zeus" button.
//                                 What the installer's "Zeus Server" desktop icon
//                                 launches on macOS / Windows / Linux so the
//                                 operator can read the LAN URL without hunting
//                                 for a console window.
//
// We use a classic `Main` (not top-level statements) so we can hang [STAThread]
// off it — Photino on Windows wraps WebView2 (COM), and CoreWebView2 has to be
// created on an STA thread or msedgewebview2.exe silently fails to spawn
// (Photino v0.5.0 black-screen bug). [STAThread] is a no-op on macOS / Linux
// and harmless in service mode where no UI runs.
//
// `Program` is declared `public partial` so Microsoft.AspNetCore.Mvc.Testing's
// WebApplicationFactory<Program> can resolve it from the test assembly. Tests
// that swap services (LevelerMaxGainEndpointTests, MicGainEndpointTests) rely
// on this — the type and a matching Main are how WebApplicationFactory finds
// the host pipeline to drive.

public partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--desktop"))
        {
            return RunDesktop(args);
        }

        if (args.Contains("--server"))
        {
            // Same service-mode backend as the no-flag path, plus a small
            // Photino status window so the operator on macOS / Linux has a
            // place to read the LAN URL and a Stop Zeus button. Headless
            // deploys (Docker, Pi) keep using the no-flag path and never
            // load Photino.
            return RunServerWithStatus(args);
        }

        return RunService(args).GetAwaiter().GetResult();
    }

    private static Task<int> RunService(string[] args)
    {
        // 5000 is claimed by macOS ControlCenter (AirPlay receiver) by default,
        // which replies 403 before Kestrel ever sees the request. 6060 is a
        // stable free port across macOS/Linux/Windows for local dev and avoids
        // conflicting with the user's Log4YM project (which also binds :5050).
        // ZEUS_PORT overrides the default (used by the /run skill's portOffset).
        var httpPort = int.TryParse(Environment.GetEnvironmentVariable("ZEUS_PORT"), out var zp) ? zp : 6060;
        // PERF_PASS_3_DEBUG: allow disabling HTTPS + LAN bind for a second instance
        // on the same box (Brian's main session keeps :6443/40001). Uncommitted.
        var perfTest = Environment.GetEnvironmentVariable("ZEUS_PERF_TEST") == "1";

        var options = new ZeusHostOptions
        {
            HttpPort = httpPort,
            BindAllInterfaces = !perfTest,
            UseHttpsLanCert = !perfTest,
            PrintConsoleBanner = true,
        };

        return ZeusHost.RunAsync(args, options);
    }

    private static int RunDesktop(string[] args)
    {
        // macOS Cocoa requires UI work (window/menu construction) to happen on the
        // initial process thread. .NET console apps don't install a SynchronizationContext,
        // so any `await` would resume the rest of this method on a thread-pool thread —
        // which then crashes Photino with "API misuse: setting the main menu on a
        // non-main thread". Block synchronously through the host startup so the
        // Photino calls below stay on the main thread; Kestrel runs on its own
        // thread pool either way and is unaffected.

        // Two listeners: loopback HTTP on an OS-assigned port (the Photino webview
        // is the only consumer of this one — picking port 0 lets the OS hand us
        // a guaranteed-free port; we read it back from IServer after StartAsync
        // so there's no TOCTOU race with a concurrent listener), plus LAN HTTPS
        // on :6443 with the existing self-signed cert so a phone or laptop on
        // the same network can pick up the session while the operator is away
        // from the shack PC. ShareOverLan=true is what decouples the two listener
        // bindings inside ZeusHost — see the comment by Kestrel.ConfigureKestrel.
        var hostOptions = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Desktop,
            HttpPort = 0,
            BindAllInterfaces = false,
            UseHttpsLanCert = true,
            ShareOverLan = true,
            PrintConsoleBanner = false,
        };

        var app = ZeusHost.Build(args, hostOptions);
        ZeusHost.InitializeAsync(app).GetAwaiter().GetResult();
        app.StartAsync().GetAwaiter().GetResult();

        // Resolve the bound URLs after Start — Kestrel writes the OS-assigned
        // loopback port (plus the LAN HTTPS port we configured) into
        // IServerAddressesFeature here. Photino must load the loopback HTTP URL:
        // pointing the webview at the LAN HTTPS URL would trip the self-signed
        // cert's interstitial inside the embedded WebKit/WebView2, which has no
        // UI to accept the warning.
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var startUrl = addresses.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Kestrel reported no loopback HTTP address.");

        Console.WriteLine($"OpenhpsdrZeus (desktop) hosting backend at {startUrl}");

        // LAN URL surface for the "walk to the kitchen, pick up phone" flow.
        // GetLanIps already filters to up + non-loopback interfaces, so the
        // operator gets a copy-pasteable URL per NIC. If for some reason no LAN
        // NIC is visible (offline laptop, ethernet down) we just skip this
        // line — the Photino window still works.
        var lanHttpsPort = LanCertificate.GetHttpsPort();
        foreach (var ip in LanCertificate.GetLanIps())
        {
            Console.WriteLine($"OpenhpsdrZeus (desktop) LAN share: https://{ip}:{lanHttpsPort}");
        }

        // SetUseOsDefaultLocation(false)+Center so first launch doesn't drop the
        // window in the corner. Title is the marketing name; we prefix "Openhpsdr"
        // elsewhere in copy but the OS title bar stays short.
        // Photino on macOS sometimes ignores SetSize on first show — Cocoa initialises
        // the NSWindow at a small default and only the *minimum* size is honoured
        // reliably. Pinning SetMinWidth/SetMinHeight at the desired width forces the
        // frame to open wide enough to clear the SPA's mobile breakpoint (900px) and
        // give the panadapter usable headroom.
        const int MinWidth = 1280;
        const int InitialWidth = 1680;
        const int InitialHeight = 1050;

        // Photino's window/dock icon is set per-OS. Windows expects .ico (Photino's
        // SetIconFile binds it to the NSWindow / HWND), Linux GTK expects PNG, and
        // macOS draws the dock icon from CFBundleIconFile in Info.plist — so during
        // `dotnet run` on macOS the SetIconFile call is a no-op (the .app bundle
        // generator wires the icns separately). Both files ship next to the binary
        // via the csproj's <Content Include="zeus.png/.ico"> so AppContext.BaseDirectory
        // resolves correctly from `dotnet run` output and from a published bundle.
        var iconFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus.ico" : "zeus.png";
        var iconPath = Path.Combine(AppContext.BaseDirectory, iconFileName);

        var window = new PhotinoWindow()
            .SetTitle("Zeus")
            .SetUseOsDefaultLocation(false)
            .SetMinWidth(MinWidth)
            .SetMinHeight(800)
            .SetSize(InitialWidth, InitialHeight)
            .Center()
            .SetIconFile(iconPath)
            .Load(new Uri(startUrl));

        // Translate Ctrl-C / SIGTERM into a window close so `dotnet run` (and the
        // installer's launcher script) can shut Zeus down without leaving the
        // Photino native loop blocking the main thread. Without this, signals only
        // reach Kestrel and the UI loop holds the process open until killed.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => window.Close();

        // WaitForClose blocks the main thread until the user closes the window. On
        // macOS this satisfies Cocoa's "UI on main thread" requirement; Kestrel
        // runs on its own thread-pool, untouched by the windowing loop.
        window.WaitForClose();

        Console.WriteLine("Window closed; stopping backend.");
        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static int RunServerWithStatus(string[] args)
    {
        // Service-mode backend (LAN bind, HTTPS, banner) PLUS a small Photino
        // window listing the bound URLs and a Stop button. Same Cocoa/main-thread
        // discipline as RunDesktop — block synchronously through host startup so
        // the Photino calls below stay on the main thread.
        var httpPort = int.TryParse(Environment.GetEnvironmentVariable("ZEUS_PORT"), out var zp) ? zp : 6060;
        var hostOptions = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Server,
            HttpPort = httpPort,
            BindAllInterfaces = true,
            UseHttpsLanCert = true,
            PrintConsoleBanner = true,
        };

        var app = ZeusHost.Build(args, hostOptions);
        ZeusHost.InitializeAsync(app).GetAwaiter().GetResult();
        app.StartAsync().GetAwaiter().GetResult();

        // Collect URLs to show the operator. Local always works; LAN entries
        // depend on whether there's a NIC up.
        var lanHttpsPort = LanCertificate.GetHttpsPort();
        var lanIps = LanCertificate.GetLanIps();
        var lanRows = new System.Text.StringBuilder();
        if (lanIps.Count > 0)
        {
            foreach (var ip in lanIps)
            {
                lanRows.Append($"<li><span class='lbl'>LAN HTTP</span><a class='url' href='#' data-url='http://{ip}:{httpPort}'>http://{ip}:{httpPort}</a></li>");
                lanRows.Append($"<li><span class='lbl'>LAN HTTPS</span><a class='url' href='#' data-url='https://{ip}:{lanHttpsPort}'>https://{ip}:{lanHttpsPort}</a></li>");
            }
        }
        else
        {
            lanRows.Append("<li class='muted'>No LAN interfaces detected — local only.</li>");
        }

        var statusHtml = $@"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>Zeus Server</title>
<style>
  :root {{
    --bg-app:#657486; --panel-top:#14161a; --panel-bot:#0e1014;
    --fg-0:#e8eaed; --fg-1:#d6d8dc; --fg-2:#b8bcc3; --fg-3:#5a5e66;
    --line-1:#2a2c30; --line-2:#3a3d42; --accent:#4a9eff; --tx:#e63a2b;
    --power:#ffc93a; --bg-2:#1f2226;
  }}
  body {{
    margin:0; padding:18px 20px; min-height:100vh; box-sizing:border-box;
    background:var(--bg-app); color:var(--fg-0);
    font-family:-apple-system, 'Segoe UI', 'Inter', system-ui, sans-serif; font-size:13px;
  }}
  .panel {{
    background:linear-gradient(180deg, var(--panel-top), var(--panel-bot));
    border:1px solid var(--line-1); border-radius:8px; padding:14px 16px;
    box-shadow:0 1px 0 rgba(255,255,255,0.04) inset, 0 4px 12px rgba(0,0,0,0.3);
  }}
  h1 {{
    margin:0 0 4px; font-size:14px; font-weight:600; letter-spacing:2px;
    text-transform:uppercase; color:var(--fg-0);
    border-bottom:1px solid var(--line-1); padding-bottom:8px;
    box-shadow:inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255,201,58,0.12);
  }}
  .sub {{ font-size:11px; color:var(--fg-2); margin:8px 0 12px; letter-spacing:0.4px; }}
  ul {{ list-style:none; padding:0; margin:0 0 14px; }}
  li {{ display:flex; align-items:center; gap:10px; padding:6px 0; border-bottom:1px solid var(--line-1); font-family:'JetBrains Mono', ui-monospace, monospace; font-size:12px; }}
  li:last-child {{ border-bottom:none; }}
  .lbl {{ display:inline-block; min-width:96px; font-family:-apple-system, 'Segoe UI', system-ui, sans-serif; font-size:10px; letter-spacing:0.8px; text-transform:uppercase; color:var(--fg-3); }}
  .url {{ color:var(--accent); text-decoration:none; font-variant-numeric:tabular-nums; }}
  .url:hover {{ text-decoration:underline; }}
  .muted {{ color:var(--fg-3); font-style:italic; }}
  .actions {{ display:flex; justify-content:flex-end; gap:8px; margin-top:6px; }}
  button {{
    padding:6px 14px; font-family:-apple-system, system-ui, sans-serif; font-size:11px;
    font-weight:600; letter-spacing:1.5px; text-transform:uppercase; color:#fff;
    background:var(--tx); border:1px solid var(--tx); border-radius:3px; cursor:pointer;
    box-shadow:0 0 8px rgba(230,58,43,0.4), inset 0 1px 0 rgba(255,255,255,0.15);
  }}
  button:hover {{ filter:brightness(1.1); }}
  .hint {{ font-size:10px; color:var(--fg-3); margin-top:10px; line-height:1.5; }}
</style>
</head><body>
<div class='panel'>
  <h1>Zeus Server</h1>
  <div class='sub'>Backend is running. Connect from this device or any device on your LAN.</div>
  <ul>
    <li><span class='lbl'>This device</span><a class='url' href='#' data-url='http://localhost:{httpPort}'>http://localhost:{httpPort}</a></li>
    {lanRows}
  </ul>
  <div class='actions'><button id='stop'>Stop Zeus</button></div>
  <div class='hint'>HTTPS uses a self-signed certificate — accept the browser warning on first connect. Closing this window also stops the server.</div>
</div>
<script>
  document.getElementById('stop').addEventListener('click', () => {{
    if (window.external && window.external.sendMessage) window.external.sendMessage('stop');
    else window.close();
  }});
  // Click-to-copy on any URL row.
  document.querySelectorAll('a.url').forEach(a => {{
    a.addEventListener('click', e => {{
      e.preventDefault();
      const u = a.getAttribute('data-url');
      navigator.clipboard.writeText(u);
      const prev = a.textContent;
      a.textContent = 'copied ✓';
      setTimeout(() => a.textContent = prev, 900);
    }});
  }});
</script>
</body></html>";

        var iconFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus.ico" : "zeus.png";
        var iconPath = Path.Combine(AppContext.BaseDirectory, iconFileName);

        var window = new PhotinoWindow()
            .SetTitle("Zeus Server")
            .SetUseOsDefaultLocation(false)
            .SetMinWidth(420)
            .SetMinHeight(280)
            .SetSize(520, 360)
            .SetResizable(true)
            .Center()
            .SetIconFile(iconPath)
            .RegisterWebMessageReceivedHandler((sender, msg) =>
            {
                if (msg == "stop" && sender is PhotinoWindow w) w.Close();
            })
            .LoadRawString(statusHtml);

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => window.Close();

        window.WaitForClose();

        Console.WriteLine("Status window closed; stopping backend.");
        app.StopAsync().GetAwaiter().GetResult();
        return 0;
    }
}
