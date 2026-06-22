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
// Serial bring-up mirrors DeskHPSDR's launch_serial_rigctl
// (https://github.com/dl1bz/deskhpsdr, Heiko DL1BZ, GPL-2.0-or-later): open
// the g2-front line, let the Arduino bootloader settle, then drive the
// ANDROMEDA ZZZS/ZZZI handshake. See ATTRIBUTIONS.md for provenance.

using System.IO.Ports;
using System.Text;

namespace Zeus.Server.FrontPanel;

/// <summary>
/// Background bridge between an ANAN G2 / G2-Ultra hardware front panel
/// (a serial ANDROMEDA controller) and Zeus's radio services. It opens the
/// panel's serial line, decodes button / encoder / VFO events into radio
/// actions via <see cref="G2PanelActionRouter"/>, and pushes LED state back
/// with <c>ZZZI</c> reports.
///
/// <para>Device resolution is presence-gated: with no <c>DevicePath</c>
/// configured it auto-detects the <c>g2-front-*</c> by-id symlink, and on a
/// host with no panel it simply idles and re-probes. So it is safe to leave
/// registered everywhere; it only does work on a machine the panel is wired
/// to (typically the G2's internal Pi).</para>
/// </summary>
public sealed class G2FrontPanelService : BackgroundService
{
    private readonly G2PanelOptions _opts;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly G2PanelActionRouter _router;
    private readonly ILogger<G2FrontPanelService> _log;

    private readonly AndromedaParser _parser = new();
    private readonly object _writeLock = new();
    private SerialPort? _port;

    // ANDROMEDA console type announced via ZZZS. 5 = G2 Ultra (Mk2). Actions
    // are only routed for type 5; an unrecognised type is logged and ignored
    // so a different panel's button map can never mis-fire MOX/TUNE.
    private int _panelType;

    // Last LED state pushed to the panel (index = LED number), -1 = unknown so
    // the first poll always writes. G2-Ultra LEDs: 1=MOX, 2=TUNE, 3=PS,
    // 6=RIT, 7=XIT, 9=LOCK.
    private readonly int[] _lastLed = new int[16];

    public G2FrontPanelService(
        IConfiguration config,
        RadioService radio,
        TxService tx,
        BandMemoryStore bandMemory,
        ILogger<G2FrontPanelService> log,
        ILoggerFactory loggerFactory)
    {
        _opts = new G2PanelOptions();
        config.GetSection(G2PanelOptions.Section).Bind(_opts);
        _radio = radio;
        _tx = tx;
        _log = log;
        _router = new G2PanelActionRouter(radio, tx, bandMemory,
            loggerFactory.CreateLogger<G2PanelActionRouter>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("g2panel.disabled (G2FrontPanel:Enabled=false)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var dev = ResolveDevice();
            if (dev is null)
            {
                // No panel on this host — idle and re-probe. Quiet by design.
                await DelaySafe(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                await RunPanelAsync(dev.Value.Path, dev.Value.Baud, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "g2panel.session.error device={Dev}; reconnecting", dev.Value.Path);
                await DelaySafe(TimeSpan.FromSeconds(5), stoppingToken);
            }
            finally
            {
                ClosePort();
            }
        }
    }

    private (string Path, int Baud)? ResolveDevice()
    {
        if (!string.IsNullOrWhiteSpace(_opts.DevicePath))
            return (_opts.DevicePath!, _opts.Baud > 0 ? _opts.Baud : 9600);

        // Auto-detect the udev-published symlink (Linux / G2 Pi).
        foreach (var (path, baud) in G2PanelOptions.KnownSymlinks)
        {
            try { if (File.Exists(path)) return (path, _opts.Baud > 0 ? _opts.Baud : baud); }
            catch { /* path probing is best-effort */ }
        }
        return null;
    }

    private async Task RunPanelAsync(string path, int baud, CancellationToken ct)
    {
        _panelType = 0;
        Array.Fill(_lastLed, -1);

        var port = new SerialPort(path, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 500,
            NewLine = ";",
        };
        port.Open();
        _port = port;
        _log.LogInformation("g2panel.open device={Dev} baud={Baud}", path, baud);

        // Opening the line resets Arduino-class panels into the bootloader for
        // ~0.5 s; wait before the first byte so ZZZS isn't lost.
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);

        // Ask the panel to identify itself; the ZZZS reply sets _panelType.
        Send("ZZZS;");

        var ledLoop = LedPollLoop(ct);
        try
        {
            await ReadLoop(port, ct);
        }
        finally
        {
            try { await ledLoop; } catch { /* surfaced by ReadLoop */ }
        }
    }

    private async Task ReadLoop(SerialPort port, CancellationToken ct)
    {
        var buf = new byte[256];
        var stream = port.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            }
            catch (TimeoutException) { continue; }

            if (n <= 0) { await Task.Delay(20, ct); continue; }

            // ANDROMEDA is 7-bit ASCII; decode straight to chars.
            var text = Encoding.ASCII.GetString(buf, 0, n);
            _parser.Feed(text, OnEvent);
        }
    }

    private void OnEvent(PanelEvent ev)
    {
        if (ev is PanelEvent.Version ver)
        {
            _panelType = ver.Type;
            _log.LogInformation("g2panel.version type={Type} raw={Raw}", ver.Type, ver.Raw);
            if (ver.Type != 5)
                _log.LogWarning("g2panel.unsupported type={Type} (only G2-Ultra type-5 actions are mapped)", ver.Type);
            // Push current LED state immediately on (re)identification.
            RefreshLeds();
            return;
        }

        // Don't route buttons/encoders until we know we're talking to a
        // G2-Ultra — a wrong button map could mis-key the transmitter.
        if (_panelType != 5) return;

        _router.Dispatch(ev);
        // Immediate LED feedback after an action (e.g. MOX/TUNE/CTUN edge).
        RefreshLeds();
    }

    private async Task LedPollLoop(CancellationToken ct)
    {
        // Periodic LED reconciliation mirrors deskhpsdr's 500 ms andromeda
        // timer; covers state changes the panel didn't originate.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_panelType == 5) RefreshLeds();
        }
    }

    // Compute the G2-Ultra LED set from current radio state and emit ZZZI for
    // any that changed. LEDs Zeus has no state for (ATU=4, active-RX=8) stay
    // off — see the gap table.
    private void RefreshLeds()
    {
        var s = _radio.Snapshot();
        SetLed(1, _tx.IsMoxOn);        // MOX
        SetLed(2, _tx.IsTunOn);        // TUNE
        SetLed(3, s.PsEnabled);        // PureSignal (readback only)
        SetLed(6, s.RitEnabled);       // RIT
        SetLed(7, s.XitEnabled);       // XIT
        SetLed(9, s.VfoLocked);        // LOCK
    }

    private void SetLed(int led, bool on)
    {
        int v = on ? 1 : 0;
        if (_lastLed[led] == v) return;
        _lastLed[led] = v;
        Send(AndromedaParser.LedCommand(led, on));
    }

    private void Send(string cmd)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return;
        try
        {
            lock (_writeLock)
            {
                if (port.IsOpen) port.Write(cmd);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "g2panel.write.failed cmd={Cmd}", cmd);
        }
    }

    private void ClosePort()
    {
        var port = _port;
        _port = null;
        if (port is null) return;
        try { if (port.IsOpen) port.Close(); } catch { /* ignore */ }
        port.Dispose();
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        ClosePort();
        base.Dispose();
    }
}
