// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenhpsdrZeus;
using Photino.NET;
using Zeus.Plugins.Host.Audio;
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
    private const int DefaultDesktopHttpPort = 6061;
    // Size of the deterministic loopback-port scan range (6061..6080). The
    // loopback port IS the web origin, and the webview keeps UI settings in
    // localStorage keyed to that origin — so the port must stay stable across
    // launches or saved layout/preferences appear to vanish. See
    // ResolveDesktopHttpPort.
    private const int DesktopHttpPortScanCount = 20;
    private static readonly JsonSerializerOptions WebMessageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class WorkspaceWindowRequest
    {
        public string? Type { get; set; }
        public string? LayoutId { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
    }

    // A live detached workspace frame plus the metadata needed to persist and
    // re-open it (the native PhotinoWindow alone carries neither its layout id
    // nor a clean title). Tracked in a managed list so the layout id survives
    // even after the native window is torn down at shutdown.
    private sealed class DetachedWorkspaceWindow
    {
        public required string LayoutId { get; init; }
        public required string Title { get; init; }
        public required PhotinoWindow Window { get; init; }
    }

    // The executable is built as OutputType=WinExe (GUI subsystem) — see
    // OpenhpsdrZeus.csproj. Windows therefore never auto-allocates a console for
    // it, so the desktop / server Photino shells start with no console window to
    // flash. The trade-off is that headless service mode no longer inherits a
    // console automatically; AttachParentConsoleOnWindows reattaches to the
    // launching shell's console (ATTACH_PARENT_PROCESS) when there is one, so the
    // banner / stdout still reaches a terminal launch. When launched by
    // double-click (installer icon) there is no parent console and the attach
    // simply fails — which is exactly the "no window" behaviour we want. Both
    // P/Invokes are no-ops on macOS / Linux, so the calls are guarded by an OS
    // check.
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void AttachParentConsoleOnWindows()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            AttachConsole(AttachParentProcess);
        }
    }

    // Windows' default system timer resolution is ~15.6 ms, which floors every
    // Task.Delay / Thread.Sleep the .NET runtime issues. The Protocol-2 TX-IQ
    // sender paces packets to the radio with sub-millisecond Task.Delay waits;
    // at 15.6 ms granularity it can only feed the DUC at ~380 packets/s instead
    // of the required 800 (192 kHz), starving the radio's TX FIFO so it holds
    // its T/R relay ~2 s after un-key. macOS / Linux already run a ~1 ms timer
    // so the identical code paces correctly there — which is why the symptom is
    // Windows-only. timeBeginPeriod(1) raises the process-wide timer resolution
    // to 1 ms for the lifetime of the process (the same thing Thetis gets via
    // its multimedia-timer / "Pro Audio" thread), so Task.Delay-based TX pacing
    // hits the full rate on Windows too. The matching timeEndPeriod is omitted
    // deliberately — we want 1 ms resolution for the whole process lifetime and
    // Windows restores the default on exit.
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    private static void RaiseTimerResolutionOnWindows()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            TimeBeginPeriod(1);
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before any TX pacing starts — see RaiseTimerResolutionOnWindows.
        RaiseTimerResolutionOnWindows();

        if (args.Contains("--verify-vst-bridge"))
        {
            // WinExe has no console by default — reattach so Console.WriteLine
            // output reaches the caller (PowerShell verify script reads stdout).
            AttachParentConsoleOnWindows();
            return VerifyVstBridge();
        }

        // Verbose preflight + crash tracer. Writes a full environment/dependency
        // report and per-phase markers to %LOCALAPPDATA%/Zeus/zeus-startup.log on
        // every launch, and installs last-resort exception handlers. This is the
        // durable record we ask a user to send when "the window just closes".
        StartupDiagnostics.Begin(args);

        if (args.Contains("--desktop"))
        {
            try
            {
                return RunDesktop(args);
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                ReportStartupAddressInUse(ex);
                return 1;
            }
            catch (Exception ex)
            {
                // Any other startup failure would otherwise escape Main and the
                // process would vanish silently — the GUI-subsystem binary has no
                // console, so the operator sees the window flash and close with no
                // diagnostics. Record it and surface a dialog.
                ReportStartupFatal(ex);
                return 1;
            }
        }

        if (args.Contains("--server"))
        {
            // Same service-mode backend as the no-flag path, plus a small
            // Photino status window so the operator on macOS / Linux has a
            // place to read the LAN URL and a Stop Zeus button. Headless
            // deploys (Docker, Pi) keep using the no-flag path and never
            // load Photino. Reattach to the launching terminal's console (if
            // any) so the banner still prints there; the GUI-subsystem binary
            // means double-clicking the installer icon shows no console.
            AttachParentConsoleOnWindows();
            try
            {
                return RunServerWithStatus(args);
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                ReportStartupAddressInUse(ex);
                return 1;
            }
            catch (Exception ex)
            {
                ReportStartupFatal(ex);
                return 1;
            }
        }

        return RunService(args).GetAwaiter().GetResult();
    }

    private static int VerifyVstBridge()
    {
        try
        {
            var bridge = new VstBridgeNative();
            var initStatus = bridge.Init(VstBridgeAbi.Current);
            if (initStatus != VstBridgeStatus.Ok)
            {
                Console.Error.WriteLine($"VST bridge init returned status {initStatus}.");
                return 1;
            }

            var shutdownStatus = bridge.Shutdown();
            if (shutdownStatus != VstBridgeStatus.Ok)
            {
                Console.Error.WriteLine($"VST bridge shutdown returned status {shutdownStatus}.");
                return 1;
            }

            Console.WriteLine("VST bridge verified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VST bridge verification failed: {ex.GetBaseException().Message}");
            return 1;
        }
    }

    private static Task<int> RunService(string[] args)
    {
        // Headless service mode logs to stdout. The binary is GUI-subsystem
        // (WinExe) so Windows never gives it a console of its own; reattach to
        // the launching terminal's console so the banner / logs land there.
        AttachParentConsoleOnWindows();

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
        // VST hosting — audio AND the plugin editor window — now lives in the
        // out-of-process engine (VST processing mode), crash-isolated from the
        // radio: a plugin that segfaults on load or in its GUI can't take the
        // backend down. The legacy in-process native VST load, where a single
        // bad .vst3 loaded on boot CAN hard-crash this process (an unrecoverable
        // native segfault no C# try/catch can stop), therefore stays OFF by
        // default in EVERY mode, including desktop. It was briefly default-ON in
        // desktop so the old in-process editor worked; the engine's crash-
        // isolated editor superseded that (steps 1–2 of
        // docs/designs/vst-host-consolidation.md), and a large scanned library
        // turned the boot-time in-process load into a guaranteed crash. It
        // remains an explicit opt-in (ZEUS_ENABLE_VST_LOAD=1) for developing
        // the native bridge itself.

        // macOS Cocoa requires UI work (window/menu construction) to happen on the
        // initial process thread. .NET console apps don't install a SynchronizationContext,
        // so any `await` would resume the rest of this method on a thread-pool thread —
        // which then crashes Photino with "API misuse: setting the main menu on a
        // non-main thread". Block synchronously through the host startup so the
        // Photino calls below stay on the main thread; Kestrel runs on its own
        // thread pool either way and is unaffected.

        // Two listeners: loopback HTTP for the Photino webview, plus LAN HTTPS
        // on :6443 with the existing self-signed cert so a phone or laptop on
        // the same network can pick up the session while the operator is away
        // from the shack PC. Keep the loopback origin stable when possible:
        // a random port makes every desktop launch a new web origin, which
        // strands localStorage-backed UI preferences. If the preferred port is
        // busy we fall back to port 0 so Zeus still launches.
        var lanHttpsPort = LanCertificate.GetHttpsPort();
        var shareOverLan = IsAnyTcpPortAvailable(lanHttpsPort);
        if (!shareOverLan)
        {
            Console.WriteLine(
                $"LAN share port {lanHttpsPort} is already in use; desktop will start loopback-only for this launch.");
        }

        var hostOptions = new ZeusHostOptions
        {
            HostMode = ZeusHostMode.Desktop,
            HttpPort = ResolveDesktopHttpPort(),
            BindAllInterfaces = false,
            UseHttpsLanCert = shareOverLan,
            ShareOverLan = shareOverLan,
            PrintConsoleBanner = false,
        };

        StartupDiagnostics.Phase("desktop: ZeusHost.Build");
        var app = ZeusHost.Build(args, hostOptions);
        StartupDiagnostics.Phase("desktop: ZeusHost.InitializeAsync");
        ZeusHost.InitializeAsync(app).GetAwaiter().GetResult();
        StartupDiagnostics.Phase("desktop: app.StartAsync (Kestrel + hosted services)");
        app.StartAsync().GetAwaiter().GetResult();
        StartupDiagnostics.Phase("desktop: host started");

        // Spawn the out-of-process support sidecar now the backend is up. It runs
        // detached so it survives a backend crash and can capture the crash logs
        // the in-memory ring would lose. Best-effort — never blocks launch.
        SupportSidecarLauncher.TryLaunch(app.Services);

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
        if (hostOptions.ShareOverLan)
        {
            foreach (var ip in LanCertificate.GetLanIps())
            {
                Console.WriteLine($"OpenhpsdrZeus (desktop) LAN share: https://{ip}:{lanHttpsPort}");
            }
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
        const int MinHeight = 800;

        // Restore the size the operator left the window at last run. The store
        // returns the InitialWidth/InitialHeight defaults on a fresh install, so
        // first launch is unchanged; thereafter the frame reopens at its saved
        // size (and maximized state). Position is not restored — see
        // WindowGeometryStore for why (off-screen / monitor-layout safety).
        var geometryStore = app.Services.GetRequiredService<WindowGeometryStore>();
        var openWindowsStore = app.Services.GetRequiredService<OpenWorkspaceWindowsStore>();
        var savedGeometry = geometryStore.Get();
        var initialWidth = savedGeometry.Width;
        var initialHeight = savedGeometry.Height;

        // Photino's window/dock icon is set per-OS. Windows expects .ico (Photino's
        // SetIconFile binds it to the NSWindow / HWND), Linux GTK expects PNG, and
        // macOS draws the dock icon from CFBundleIconFile in Info.plist — so during
        // `dotnet run` on macOS the SetIconFile call is a no-op (the .app bundle
        // generator wires the icns separately). Both files ship next to the binary
        // via the csproj's <Content Include="zeus.png/.ico"> so AppContext.BaseDirectory
        // resolves correctly from `dotnet run` output and from a published bundle.
        var iconFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "zeus.ico" : "zeus.png";
        var iconPath = Path.Combine(AppContext.BaseDirectory, iconFileName);
        var detachedWorkspaceWindows = new List<DetachedWorkspaceWindow>();

        // The dark placeholder loaded as StartString carries a tiny script that,
        // once the page is live (i.e. WebView2 has finished initialising), posts a
        // `zeus.placeholderReady` message back to the host. The host then navigates
        // to the SPA from the WebMessageReceived handler below — a point where the
        // native WebView2 control is provably ready to accept a navigation.
        //
        // This replaces the old approach of calling window.Load(startUrl) directly
        // from the WindowCreated handler. WindowCreated fires *during* native window
        // construction, before WebView2's CoreWebView2 is initialised; calling
        // Photino_NavigateToUrl there dereferences a not-yet-created control and
        // hard-crashes the process with a native access violation (0xc0000005 —
        // an uncatchable corrupted-state exception, so no try/catch and no managed
        // diagnostics handler can intercept it). Driving the SPA navigation off the
        // placeholder-ready message keeps it on the live WebView2 while still
        // happening AFTER the WindowCreated resize, so the no-reflow guarantee from
        // #930 holds. If the host bridge is somehow absent the placeholder navigates
        // itself as a fallback.
        const string placeholderHtml =
            "<!doctype html><meta name=\"color-scheme\" content=\"dark\">" +
            "<body style=\"margin:0;height:100vh;background:#0a0a0c\">" +
            "<script>(function(){function spa(){location.replace('__START_URL__');}" +
            "try{var e=window.external;" +
            "if(e&&typeof e.sendMessage==='function')" +
            "e.sendMessage(JSON.stringify({type:'zeus.placeholderReady'}));" +
            "else spa();}catch(_){spa();}})();</script>";
        // Navigate to the SPA exactly once, even if the ready message arrives more
        // than once (e.g. a placeholder reload).
        var spaLoaded = false;

        // Window construction is where a missing WebView2 runtime throws — the
        // last phase marker in the log will be this one if that's the cause.
        StartupDiagnostics.Phase("desktop: creating Photino window (needs WebView2)");
        var window = new PhotinoWindow()
            .SetTitle("Zeus")
            .SetUseOsDefaultLocation(false)
            .SetMinWidth(MinWidth)
            .SetMinHeight(MinHeight)
            .SetSize(initialWidth, initialHeight)
            .Center()
            .SetIconFile(iconPath)
            .RegisterWebMessageReceivedHandler((sender, msg) =>
            {
                if (sender is not PhotinoWindow owner) return;
                // The dark placeholder reports it is live once WebView2 is ready.
                // Navigate to the SPA here, not from WindowCreated — see the long
                // note above placeholderHtml for why the WindowCreated path AVs.
                if (IsPlaceholderReady(msg))
                {
                    if (spaLoaded) return;
                    spaLoaded = true;
                    StartupDiagnostics.Phase("desktop: placeholder ready, loading SPA");
                    try { owner.Load(new Uri(startUrl)); }
                    catch (Exception ex) { Console.Error.WriteLine($"window.spa.load failed: {ex.Message}"); }
                    return;
                }
                if (TryReadWorkspaceWindowRequest(msg, out var request))
                {
                    OpenWorkspaceWindow(owner, detachedWorkspaceWindows, request, iconPath);
                    return;
                }
                // External links (e.g. the "Report a problem" → GitHub button):
                // window.open to an external site is unreliable inside the Photino
                // webview, so the frontend posts a zeus.openExternal message and the
                // host opens it in the operator's real browser via the OS opener.
                if (TryReadOpenExternalRequest(msg, out var externalUrl))
                    OpenExternalUrl(externalUrl);
            })
            // Deliberately load a dark placeholder, NOT the SPA, as the startup
            // content. The placeholder posts `zeus.placeholderReady` once WebView2
            // is live; the WebMessageReceived handler above then navigates to the
            // SPA — after the WindowCreated resize, so React's first layout measures
            // the final viewport (no panel reflow). The placeholder matches --bg-app
            // (#0a0a0c) so the brief pre-size frame is invisible against the dark
            // chrome instead of flashing white.
            .LoadRawString(placeholderHtml.Replace("__START_URL__", startUrl));

        // Remember the last NORMAL (non-maximized) frame size as resize events
        // arrive. This is only needed for the maximized-at-close case: when the
        // window is maximized we must persist the size it had *before* maximizing
        // so a later un-maximize lands somewhere sensible. The authoritative
        // persist below reads the live window directly and does not depend on
        // these events firing.
        var lastNormalWidth = initialWidth;
        var lastNormalHeight = initialHeight;
        var isMaximized = savedGeometry.Maximized;
        var isMinimized = false;

        // Reopen maximized if that's how it was left. Setting the property before
        // WaitForClose stores it as a startup parameter (the native window doesn't
        // exist yet), so the frame opens maximized; we still seeded SetSize above
        // so a later un-maximize restores to the saved normal size.
        if (savedGeometry.Maximized)
            window.Maximized = true;

        // Reliably apply the restored size AFTER the native window exists. The
        // .SetSize() in the builder chain above is a startup parameter, and on
        // first show Photino/WebView2 frequently ignores it and opens the frame at
        // its *minimum* size instead (the long-standing gotcha noted in the size
        // comment above — observed on Windows here, not just macOS). OnWindowCreated
        // fires once _nativeInstance is live (before the message loop), so setting
        // window.Size / .Center() here routes through Photino_SetSize/Photino_Center
        // on the real window — which IS honoured. Runs on the UI thread, so the
        // native calls execute inline.
        window.RegisterWindowCreatedHandler((_, _) =>
        {
            try
            {
                // Record the full display topology + the geometry we're about to
                // restore. This is the smoking gun for "window flashes and never
                // appears" reports where the startup log otherwise reaches
                // "entering message loop" cleanly (the window IS created — it just
                // lands off the visible desktop or comes up minimized).
                try
                {
                    foreach (var mon in window.Monitors)
                        StartupDiagnostics.Log(
                            $"[geometry] monitor area=({mon.MonitorArea.X},{mon.MonitorArea.Y} {mon.MonitorArea.Width}x{mon.MonitorArea.Height}) " +
                            $"work=({mon.WorkArea.X},{mon.WorkArea.Y} {mon.WorkArea.Width}x{mon.WorkArea.Height}) scale={mon.Scale}");
                    StartupDiagnostics.Log(
                        $"[geometry] saved width={savedGeometry.Width} height={savedGeometry.Height} maximized={savedGeometry.Maximized}");
                }
                catch (Exception ex) { StartupDiagnostics.Log($"[geometry] monitor enumerate failed: {ex.Message}"); }

                // Always seat a sane on-screen normal size + position first — even
                // when restoring maximized — so un-maximizing later lands on the
                // visible desktop rather than wherever the saved bytes pointed.
                PlaceWindowOnVisibleWorkArea(window, initialWidth, initialHeight);
                if (savedGeometry.Maximized)
                    window.Maximized = true;

                // A frame that comes up minimized is indistinguishable from "no
                // window" to the operator; some Windows builds have been observed
                // to open this way after a restore. Force it back to normal.
                if (window.Minimized)
                    window.Minimized = false;
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"[geometry] restore failed: {ex.Message}");
                Console.Error.WriteLine($"window.geometry.restore failed: {ex.Message}");
            }
            // NOTE: the SPA is NOT loaded here. Calling window.Load(startUrl) from
            // this WindowCreated callback runs Photino_NavigateToUrl re-entrantly
            // during native window construction — before CoreWebView2 exists — which
            // crashes the process with a native access violation (0xc0000005). The
            // navigation is driven instead by the `zeus.placeholderReady` web
            // message (see placeholderHtml and the WebMessageReceived handler),
            // which fires once WebView2 is live and still after this resize, so the
            // no-reflow guarantee is preserved without the crash.
        });

        // Maximized and minimized are tracked separately: both must suppress
        // normal-size recording (their size events report screen-filling / zeroed
        // dimensions). Restored fires when returning to normal from either state.
        window.RegisterMaximizedHandler((_, _) => { isMaximized = true; isMinimized = false; });
        window.RegisterMinimizedHandler((_, _) => isMinimized = true);
        window.RegisterRestoredHandler((_, _) => { isMaximized = false; isMinimized = false; });
        window.RegisterSizeChangedHandler((_, size) =>
        {
            if (!isMaximized && !isMinimized && size.Width >= MinWidth && size.Height >= MinHeight)
            {
                lastNormalWidth = size.Width;
                lastNormalHeight = size.Height;
            }
        });

        // Persist the geometry from the closing handler, which fires while the
        // native window is still alive — so window.Width/Height/Maximized return
        // the real current size via Photino_GetSize. Crucially this does NOT
        // depend on WindowSizeChanged having fired: even if that event is silent
        // on a given platform/WebView2 build, the size is read straight off the
        // live window here. The callback runs on the UI thread, so PhotinoWindow's
        // Invoke() executes the native getters inline (no marshaling, no deadlock).
        // Returning false lets the close proceed (true would cancel it). Fires for
        // both the title-bar X and the Ctrl-C → window.Close() path below.
        window.RegisterWindowClosingHandler((_, _) =>
        {
            try
            {
                if (window.Maximized)
                {
                    // Maximized frame: window.Width/Height would be the screen
                    // size — persist the remembered normal size plus the flag.
                    geometryStore.Save(lastNormalWidth, lastNormalHeight, maximized: true);
                }
                else
                {
                    var w = window.Width;
                    var h = window.Height;
                    if (w < MinWidth || h < MinHeight) { w = lastNormalWidth; h = lastNormalHeight; }
                    geometryStore.Save(w, h, maximized: false);
                }
            }
            catch (Exception ex)
            {
                // Never block shutdown on a prefs write. Desktop mode has no
                // console (GUI-subsystem binary), so this is best-effort.
                Console.Error.WriteLine($"window.geometry.save failed: {ex.Message}");
            }
            // Snapshot the still-open detached pop-out windows while the native
            // window is alive. On macOS the post-WaitForClose persistence below
            // never runs (AppKit's terminate: → _exit preempts it), so capture it
            // here. Idempotent with the post-close save on Windows/Linux.
            try
            {
                openWindowsStore.Replace(detachedWorkspaceWindows
                    .Select(d => new OpenWorkspaceWindowDto(d.LayoutId, d.Title)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"workspace.windows.save failed: {ex.Message}");
            }
            return false; // allow the window to close
        });

        // Translate Ctrl-C into a window close so `dotnet run` (and the installer's
        // launcher script) can shut Zeus down without leaving the Photino native
        // loop blocking the main thread. Without this, signals only reach Kestrel
        // and the UI loop holds the process open until killed.
        //
        // Deliberately NOT hooking AppDomain.CurrentDomain.ProcessExit: that event
        // fires AFTER Main returns (i.e., after WaitForClose already unblocked and
        // the PhotinoWindow is gone), so calling window.Close() from it re-enters
        // a torn-down WebView2/COM apartment on Windows and deadlocks for ~30 s
        // — which is the "process lingers after window close" symptom users see in
        // Task Manager.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };

        // Make every macOS quit path skip exit()'s C++ static-destructor sweep
        // (see InstallMacSafeTerminate). Installed AFTER the PhotinoWindow forces
        // AppKit to load, and BEFORE the runloop where AppKit's auto-terminate can
        // fire on last-window-close. No-op on Windows/Linux.
        InstallMacSafeTerminate();

        // WaitForClose blocks the main thread until the user closes the window. On
        // macOS this satisfies Cocoa's "UI on main thread" requirement; Kestrel
        // runs on its own thread-pool, untouched by the windowing loop.
        StartupDiagnostics.Phase("desktop: window created, entering message loop");
        window.WaitForClose();
        // Persist the detached windows that are STILL open (operator-closed ones
        // already removed themselves via their closing handler) so the next
        // launch reopens them. Must run BEFORE the close-loop below — closing a
        // child fires its handler, which removes it from this same list.
        // Best-effort: never let a prefs write block shutdown.
        try
        {
            openWindowsStore.Replace(detachedWorkspaceWindows
                .Select(d => new OpenWorkspaceWindowDto(d.LayoutId, d.Title)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"workspace.windows.save failed: {ex.Message}");
        }
        foreach (var child in detachedWorkspaceWindows.ToArray())
        {
            try
            {
                child.Window.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"detached workspace close failed: {ex.Message}");
            }
        }

        // Geometry was already persisted by the closing handler above, while the
        // native window was still alive. We deliberately do NOT save here — the
        // window is torn down and its size getters are unsafe to read.
        Console.WriteLine("Window closed; stopping backend.");
        ShutdownDesktopHost(app);
        return 0;
    }

    // libc _exit(2): terminate the process immediately WITHOUT running atexit
    // handlers or C-runtime static destructors. Present on macOS and Linux.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
    private static extern void LibcImmediateExit(int status);

    // --- macOS: route every Cocoa quit through _exit(2) ----------------------
    //
    // ROOT CAUSE (proven from an OpenhpsdrZeus .ips, issue #1065): closing the
    // last window makes AppKit auto-fire -[NSApplication terminate:] from inside
    // the CFRunLoop ("terminate after last window closed"). terminate: calls libc
    // exit(), which runs __cxa_finalize_ranges — the C++ static-destructor sweep
    // across EVERY loaded module, including in-process JUCE audio plugins (Waves,
    // Supertone "Clear", …). Those destructors tear down a plugin's
    // juce::MessageManager while its OWN background juce::Timer thread is still
    // live, so the Timer's MessageQueue::post() locks a freed mutex → SIGSEGV
    // (exit 139). Crash-thread proof: juce::Timer::TimerThread::run ->
    // MessageQueue::post -> pthread_mutex_lock at NULL; main-thread proof:
    // -[NSApplication terminate:] -> exit -> __cxa_finalize_ranges ->
    // ~WCBIClientWithWLSThread (the Waves variant). Because terminate: preempts
    // the runloop, WaitForClose() never returns and the _exit(0) in
    // ShutdownDesktopHost is never reached on the quit path. The vendor-agnostic
    // cure is to stop exit() from ever running: replace -[NSApplication
    // terminate:] so it calls _exit(2) directly. _exit skips __cxa_finalize, so
    // no plugin static destructor can run under a live plugin thread — closing
    // both the Timer and Waves-WLS faces of the crash for ANY plugin the operator
    // loads. Window geometry + open detached windows are already committed
    // synchronously by the window-closing handler before this fires.
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(
        System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void ObjcTerminateImp(IntPtr self, IntPtr sel, IntPtr sender);

    // Held for the process lifetime: the GC must never collect the delegate that
    // backs the swizzled IMP, or terminate: would jump into freed memory.
    private static ObjcTerminateImp? _safeTerminateImp;

    private static void InstallMacSafeTerminate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var cls = objc_getClass("NSApplication");
            if (cls == IntPtr.Zero) return;
            var sel = sel_registerName("terminate:");
            var method = class_getInstanceMethod(cls, sel);
            if (method == IntPtr.Zero) return;
            _safeTerminateImp = static (_, _, _) => LibcImmediateExit(0);
            var imp = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_safeTerminateImp);
            method_setImplementation(method, imp);
            StartupDiagnostics.Log("[shutdown] macOS: -[NSApplication terminate:] routed to _exit(2) (skips plugin static-dtor race)");
        }
        catch (Exception ex)
        {
            // Non-fatal: worst case we retain the pre-existing crash-on-close risk.
            StartupDiagnostics.Log($"[shutdown] macOS safe-terminate install failed: {ex.Message}");
        }
    }

    // Windows: terminate immediately, skipping CLR finalizers, CRT static
    // destructors and DLL_PROCESS_DETACH. zeus-vst-bridge loads VST3 (.dll)
    // in-process on Windows too, so the same JUCE MessageManager-vs-Timer
    // destructor race exists at normal CLR/CRT shutdown; TerminateProcess(self)
    // runs none of those. Unlike Environment.Exit/quick_exit it fires no managed
    // ProcessExit handler, so it also avoids the WebView2/Photino teardown
    // deadlock noted in RunDesktop.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    // Shutdown for the Photino desktop / status-window paths, which drive the host
    // lifecycle by hand (Photino owns the main thread, so we can't use app.Run()).
    //
    // The hard constraint: a third-party in-process JUCE plugin (e.g. a Waves /
    // Supertone Audio Unit loaded via the macOS AU bridge) keeps its OWN background
    // juce::Timer thread that managed code cannot stop. ANY teardown of that
    // plugin's JUCE MessageManager while the thread is live is a use-after-free
    // (SIGSEGV in juce::MessageQueue::post). DISPOSING THE HOST TRIGGERS EXACTLY
    // THAT: AudioPluginBridge.DisposeAsync unloads the AU
    // (AudioComponentInstanceDispose), tearing the MessageManager down under the
    // live Timer thread. (An earlier fix disposed the host then _exit'd — but the
    // crash is DURING dispose, before _exit is ever reached.) So we must NEVER
    // dispose the host on the way out.
    //
    // Instead: StopAsync() the host — this stops hosted services (kills the
    // out-of-process RX VST engine, stops sidecars) but does NOT unload the
    // in-process AU (the bridge's StopAsync only unsubscribes) — then explicitly
    // kill the out-of-process TX VST engine (it runs no JUCE in our process, so
    // disposing it is safe) so it isn't orphaned by the hard exit, then on
    // macOS/Linux _exit(2): the kernel reaps the live plugin thread WITHOUT running
    // any destructor it could fault in. LiteDB writes are committed transactionally,
    // so skipping graceful disposal is durability-safe. Windows hosts no in-process
    // AU bridge, so it returns normally (and avoids the WebView2 ProcessExit
    // deadlock noted above).
    private static void ShutdownDesktopHost(WebApplication app)
    {
        // Deliberate shutdown — tell any supervising sidecar this is a clean exit,
        // not a crash, BEFORE we stop the host or _exit(2).
        SupportSidecar.MarkCleanExit();

        // Stop hosted services WITHOUT disposing the host (see why above).
        try { app.StopAsync().GetAwaiter().GetResult(); }
        catch { /* best effort — never block the exit */ }

        // Kill the out-of-process TX VST engine explicitly (it would otherwise only
        // be torn down by the host DisposeAsync we deliberately skip). No in-process
        // JUCE, so this cannot trigger the plugin-thread race.
        try
        {
            if (app.Services.GetService(typeof(Zeus.Plugins.Host.Audio.VstEngineController)) is IAsyncDisposable engine)
                engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { /* best effort */ }

        Console.Out.Flush();
        Console.Error.Flush();
        if (OperatingSystem.IsWindows())
        {
            // Skip CLR/CRT shutdown + DLL detach: the in-process VST3 bridge has
            // the same plugin-thread teardown race as macOS (see
            // InstallMacSafeTerminate). TerminateProcess runs no destructor.
            try { TerminateProcess(GetCurrentProcess(), 0); } catch { /* fall through */ }
            return;
        }
        try { LibcImmediateExit(0); } catch { /* fall through to a normal return */ }
    }

    // Photino occasionally opens the frame off the visible desktop on
    // multi-monitor / fractional-DPI setups: the window IS created and the
    // message loop runs (the startup log reaches "entering message loop"), but
    // the operator sees only a brief flash because the frame landed beyond the
    // work area, or larger than the monitor, or centred using a different
    // monitor's metrics. Clamp the desired size to the monitor work area and
    // force the position back inside it so a visible frame is guaranteed.
    //
    // Best-effort and self-contained: any failure falls back to Photino's own
    // SetSize + Center so a quirky monitor query can never strand the window.
    private static void PlaceWindowOnVisibleWorkArea(PhotinoWindow window, int desiredWidth, int desiredHeight)
    {
        try
        {
            var work = window.MainMonitor.WorkArea; // System.Drawing.Rectangle, OS pixels
            // No usable monitor reported — let Photino place it and bail.
            if (work.Width <= 0 || work.Height <= 0)
            {
                window.Size = new System.Drawing.Size(desiredWidth, desiredHeight);
                window.Center();
                return;
            }

            // Never open larger than the visible work area: a size saved on a
            // bigger monitor would otherwise hang the frame off the edges.
            var availW = work.Width;
            var availH = work.Height;

            // On Linux, keep the frame strictly INSIDE the work area. A GTK
            // window whose size meets or exceeds the available work area gets
            // auto-maximized by the Wayland compositor (KWin / Mutter / wlroots),
            // and a maximized GTK frame is drawn borderless — no titlebar — which
            // the operator reads as "opens fullscreen with no titlebar". The
            // SetMinWidth/SetMinHeight pinned at 1280x800 make this unavoidable on
            // small panels (e.g. the 8" 1280x800 CM5 DSI screen): the minimum
            // itself is >= the work area once a desktop panel is subtracted, so
            // GTK can't shrink the frame and the compositor maximizes it. Reserve
            // a margin AND relax the runtime minimum to that margin-inset size so
            // the frame is provably smaller than the work area and stays a normal
            // decorated window. Windows/macOS keep decorations at work-area size,
            // so this whole branch is Linux-only to leave them untouched.
            if (OperatingSystem.IsLinux())
            {
                const int LinuxWorkAreaMargin = 48;
                availW = Math.Max(640, work.Width - LinuxWorkAreaMargin);
                availH = Math.Max(480, work.Height - LinuxWorkAreaMargin);
                // Lower the floor so the size below actually takes effect instead
                // of being clamped back up by the geometry hint (which would
                // re-trigger the auto-maximize).
                if (availW < window.MinWidth) window.MinWidth = availW;
                if (availH < window.MinHeight) window.MinHeight = availH;
            }

            var w = Math.Min(desiredWidth, availW);
            var h = Math.Min(desiredHeight, availH);
            window.Size = new System.Drawing.Size(w, h);

            // Centre within the work area, then clamp so the title bar is always
            // reachable even when the monitor origin is negative (a display to
            // the left of / above the primary).
            var left = work.X + (work.Width - w) / 2;
            var top = work.Y + (work.Height - h) / 2;
            left = Math.Max(work.X, Math.Min(left, work.X + work.Width - w));
            top = Math.Max(work.Y, Math.Min(top, work.Y + work.Height - h));
            window.Location = new System.Drawing.Point(left, top);

            StartupDiagnostics.Log(
                $"[geometry] placed at ({left},{top}) size {w}x{h} within work " +
                $"({work.X},{work.Y} {work.Width}x{work.Height})");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[geometry] place-on-work-area failed, using Center(): {ex.Message}");
            try
            {
                window.Size = new System.Drawing.Size(desiredWidth, desiredHeight);
                window.Center();
            }
            catch (Exception inner)
            {
                Console.Error.WriteLine($"window.geometry.center fallback failed: {inner.Message}");
            }
        }
    }

    // The dark startup placeholder posts this once WebView2 is live, signalling
    // that it is safe to navigate the window to the SPA. Kept deliberately narrow
    // so it can never be confused with the SPA's own messages.
    private static bool IsPlaceholderReady(string message)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkspaceWindowRequest>(message, WebMessageJsonOptions);
            return parsed?.Type == "zeus.placeholderReady";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadWorkspaceWindowRequest(string message, out WorkspaceWindowRequest request)
    {
        request = new WorkspaceWindowRequest();
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkspaceWindowRequest>(message, WebMessageJsonOptions);
            if (parsed?.Type != "zeus.openWorkspaceWindow") return false;
            if (string.IsNullOrWhiteSpace(parsed.LayoutId)) return false;
            if (string.IsNullOrWhiteSpace(parsed.Url)) return false;
            if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out _)) return false;
            request = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void OpenWorkspaceWindow(
        PhotinoWindow owner,
        List<DetachedWorkspaceWindow> detachedWorkspaceWindows,
        WorkspaceWindowRequest request,
        string iconPath)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) return;
        var layoutId = request.LayoutId?.Trim() ?? string.Empty;
        var layoutTitle = string.IsNullOrWhiteSpace(request.Title)
            ? "Workspace"
            : request.Title.Trim();
        // Startup restore reopens one frame per persisted layout; if one is
        // already on screen for this layout (e.g. restore raced an operator
        // drag-off) don't stack a duplicate.
        if (layoutId.Length > 0 &&
            detachedWorkspaceWindows.Any(d => d.LayoutId == layoutId))
        {
            return;
        }
        var child = new PhotinoWindow(owner)
            .SetTitle($"Zeus - {layoutTitle}")
            .SetUseOsDefaultLocation(true)
            .SetMinWidth(900)
            .SetMinHeight(600)
            .SetSize(1180, 760)
            .SetIconFile(iconPath)
            .RegisterWindowClosingHandler((sender, _) =>
            {
                // An operator closing one detached window deliberately drops it
                // from the live set, so it is NOT re-opened next launch. The
                // shutdown close-loop persists the set BEFORE closing children,
                // so those removals don't erase what we just saved.
                if (sender is PhotinoWindow closed)
                    detachedWorkspaceWindows.RemoveAll(d => ReferenceEquals(d.Window, closed));
                return false;
            })
            .Load(uri);

        var entry = new DetachedWorkspaceWindow
        {
            LayoutId = layoutId,
            Title = layoutTitle,
            Window = child,
        };
        detachedWorkspaceWindows.Add(entry);
        try
        {
            child.WaitForClose();
        }
        catch (Exception ex)
        {
            detachedWorkspaceWindows.Remove(entry);
            Console.Error.WriteLine($"detached workspace open failed: {ex.Message}");
        }
    }

    private static bool TryReadOpenExternalRequest(string message, out string url)
    {
        url = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkspaceWindowRequest>(message, WebMessageJsonOptions);
            if (parsed?.Type != "zeus.openExternal") return false;
            if (string.IsNullOrWhiteSpace(parsed.Url)) return false;
            if (!Uri.TryCreate(parsed.Url, UriKind.Absolute, out var uri)) return false;
            // Only http/https — never hand an arbitrary scheme/command to the OS opener.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            url = uri.AbsoluteUri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void OpenExternalUrl(string url)
    {
        // Cross-platform OS browser launch. The URL is already validated http/https
        // by the caller; ArgumentList (not a concatenated string) avoids any shell
        // interpretation of the URL.
        try
        {
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                psi = new ProcessStartInfo { FileName = "open", ArgumentList = { url }, UseShellExecute = false };
            else
                psi = new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url }, UseShellExecute = false };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"openExternal failed: {ex.Message}");
        }
    }

    private static int ResolveDesktopHttpPort()
    {
        var raw = Environment.GetEnvironmentVariable("ZEUS_DESKTOP_PORT");
        if (int.TryParse(raw, out var configured))
        {
            // Explicit 0 means "operator wants an OS-assigned port" — honour it.
            if (configured == 0) return 0;
            if (configured is > 0 and <= 65535 && IsLoopbackPortAvailable(configured))
                return configured;
            Console.WriteLine($"ZEUS_DESKTOP_PORT={configured} is unavailable; scanning the default range.");
            // fall through to the deterministic scan rather than a random port
        }

        // The loopback port is the web origin, and the webview keeps UI settings
        // in localStorage keyed to that origin. A random (OS-assigned) port makes
        // every launch a fresh origin, stranding the operator's saved layout and
        // preferences — the symptom is "all my settings disappeared". Prefer a
        // small DETERMINISTIC range (6061..6080) so the origin stays stable across
        // launches. Only fall back to a random port if the whole range is taken
        // (several Zeus instances already running), and say so loudly.
        for (var port = DefaultDesktopHttpPort; port < DefaultDesktopHttpPort + DesktopHttpPortScanCount; port++)
        {
            if (IsLoopbackPortAvailable(port)) return port;
        }

        Console.WriteLine(
            $"Desktop loopback ports {DefaultDesktopHttpPort}..{DefaultDesktopHttpPort + DesktopHttpPortScanCount - 1} " +
            "are all in use; falling back to an OS-assigned port. UI settings (localStorage) will not match a " +
            "previous session for this launch.");
        return 0;
    }

    private static bool IsLoopbackPortAvailable(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsAnyTcpPortAvailable(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.DualMode = true;
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (NotSupportedException)
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
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
            if (cur.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ReportStartupAddressInUse(Exception ex)
    {
        const string title = "Zeus is already using that port";
        var message =
            "Zeus could not start because one of its network ports is already in use. " +
            "Close the other Zeus instance, or launch with ZEUS_PORT / ZEUS_DESKTOP_PORT set to a free port.\n\n" +
            ex.GetBaseException().Message;
        Console.Error.WriteLine($"{title}: {message}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _ = MessageBoxW(IntPtr.Zero, message, title, 0x00000040);
    }

    // A startup failure other than port-in-use. The desktop/server paths run as
    // a GUI-subsystem binary with no console, so an uncaught exception would make
    // the process vanish with the window never appearing and nothing logged.
    // Persist the full exception to the shared startup log and show a dialog so
    // the operator gets an actionable message instead of a silent flash-and-close.
    private static void ReportStartupFatal(Exception ex)
    {
        var baseEx = ex.GetBaseException();
        var missingWebView2 = LooksLikeMissingWebView2(ex);

        // The full exception goes to the same zeus-startup.log the preflight
        // wrote, so the dependency report and the crash sit in one file.
        StartupDiagnostics.LogException("desktop startup failed", ex);

        string title;
        string message;
        if (missingWebView2)
        {
            // The single most common fresh-Windows cause: Photino renders the UI
            // through WebView2, which is absent on Windows 11 LTSC / IoT /
            // Enterprise N, debloated images, and some VMs. Point the operator
            // straight at the fix.
            title = "Zeus needs the Microsoft Edge WebView2 Runtime";
            message =
                "Zeus could not open its window because the Microsoft Edge WebView2 " +
                "Runtime is not installed on this PC.\n\n" +
                "Install it (free, from Microsoft), then launch Zeus again:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                $"Technical detail: {baseEx.GetType().Name}: {baseEx.Message}";
        }
        else
        {
            title = "Zeus failed to start";
            message =
                "Zeus hit an unexpected error while starting and had to close.\n\n" +
                $"{baseEx.GetType().Name}: {baseEx.Message}\n\n" +
                $"A full log was written to:\n{StartupDiagnostics.LogPath}";
        }

        // Console is detached on Windows desktop/server mode; on macOS/Linux this
        // still reaches the launching terminal.
        Console.Error.WriteLine($"{title}: {message}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _ = MessageBoxW(IntPtr.Zero, message, title, 0x00000010); // MB_ICONERROR
    }

    // Heuristic: does this exception chain look like a missing WebView2 runtime?
    // Photino surfaces the absence in different shapes across versions (a COM
    // HRESULT, a DllNotFound, or a message naming WebView2 / msedgewebview2), so
    // we match on the text across the whole inner-exception chain rather than a
    // single exception type.
    private static bool LooksLikeMissingWebView2(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var m = cur.Message;
            if (string.IsNullOrEmpty(m)) continue;
            if (m.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Edge WebView", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
                // HTTPS first and tagged: browsers only grant microphone TX on a
                // secure origin, so a LAN operator who wants to transmit voice must
                // use this row. The plain-HTTP row still works for RX-only / non-mic
                // use but is listed second so it isn't the obvious first pick (issue
                // #844 — operators were landing on http:// and getting "mic
                // unavailable"). Mirrors the console banner's "use this for
                // microphone TX" marker.
                lanRows.Append($"<li><span class='lbl'>LAN HTTPS</span><a class='url' href='#' data-url='https://{ip}:{lanHttpsPort}'>https://{ip}:{lanHttpsPort}</a><span class='tag'>microphone TX</span></li>");
                lanRows.Append($"<li><span class='lbl'>LAN HTTP</span><a class='url' href='#' data-url='http://{ip}:{httpPort}'>http://{ip}:{httpPort}</a></li>");
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
  .tag {{ margin-left:8px; padding:1px 7px; border-radius:9px; font-family:-apple-system, 'Segoe UI', system-ui, sans-serif; font-size:9px; font-weight:600; letter-spacing:0.6px; text-transform:uppercase; color:var(--panel-bot); background:var(--power); }}
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
  <div class='hint'>Browsers only allow microphone TX over HTTPS on a LAN address — open the <b>LAN HTTPS</b> URL above to transmit voice. HTTPS uses a self-signed certificate; accept the browser warning once on first connect. Closing this window also stops the server.</div>
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

        // See RunDesktop for why we don't hook AppDomain.ProcessExit — calling
        // window.Close() from that event re-enters a torn-down WebView2 apartment
        // on Windows and stalls process exit for ~30 s after the window closes.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };

        window.WaitForClose();

        Console.WriteLine("Status window closed; stopping backend.");
        ShutdownDesktopHost(app);
        return 0;
    }
}
