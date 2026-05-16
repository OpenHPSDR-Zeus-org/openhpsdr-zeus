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

// Single binary, two modes:
//   OpenhpsdrZeus              → service mode (LAN bind, HTTPS, banner)
//   OpenhpsdrZeus --desktop    → Photino shell (loopback only, no HTTPS, no banner)
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

        // Loopback-only, OS-assigned port, no LAN HTTPS, no console banner — the
        // Photino webview is the only consumer. Picking port 0 lets the OS hand us
        // a guaranteed-free port; we read it back from IServer after StartAsync so
        // no TOCTOU race with a concurrent listener.
        var hostOptions = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Desktop,
            HttpPort = 0,
            BindAllInterfaces = false,
            UseHttpsLanCert = false,
            PrintConsoleBanner = false,
        };

        var app = ZeusHost.Build(args, hostOptions);
        ZeusHost.InitializeAsync(app).GetAwaiter().GetResult();
        app.StartAsync().GetAwaiter().GetResult();

        // Resolve the bound URL after Start — Kestrel writes the OS-assigned port
        // into IServerAddressesFeature here. Exactly one HTTP address because
        // hostOptions.UseHttpsLanCert=false and BindAllInterfaces=false.
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var startUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel reported no listening addresses.");

        Console.WriteLine($"OpenhpsdrZeus (desktop) hosting backend at {startUrl}");

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
}
