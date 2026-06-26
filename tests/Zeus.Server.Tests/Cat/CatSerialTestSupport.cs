// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests.Cat;

// Shared harness for the serial-CAT integration tests: a socat-backed virtual
// pty pair and a minimal connected RadioService/TxService. Gated to POSIX hosts
// with socat installed (the macOS dev box; CI Linux if socat is present).
internal static class CatSerialTestSupport
{
    public static string? ResolveSocat()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/socat", "/usr/local/bin/socat", "/usr/bin/socat" })
            if (File.Exists(p)) return p;
        return null;
    }

    public static bool PtyHarnessAvailable =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && ResolveSocat() is not null;

    // A connected RadioService + TxService + DspPipelineService backed by a temp
    // prefs DB, matching CatCommandHandlerTests.Build so the serial path drives
    // the real seams.
    public static (RadioService Radio, TxService Tx, DspPipelineService Pipeline, IDisposable Dispose) BuildRadio(string dbPath)
    {
        var lf = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");
        var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (radio, tx, pipeline, new DisposableBag(dspStore, paStore));
    }

    private sealed class DisposableBag(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) { try { i.Dispose(); } catch { } } }
    }
}

// Spawns `socat -d -d pty,raw,echo=0 pty,raw,echo=0`, parses the two device
// paths it prints on stderr, and keeps the process alive for the test.
internal sealed class SocatPtyPair : IDisposable
{
    private readonly Process _proc;
    public string DeviceA { get; }
    public string DeviceB { get; }

    private SocatPtyPair(Process proc, string a, string b)
    {
        _proc = proc;
        DeviceA = a;
        DeviceB = b;
    }

    public static async Task<SocatPtyPair> CreateAsync(string socatPath)
    {
        var psi = new ProcessStartInfo(socatPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("pty,raw,echo=0");
        psi.ArgumentList.Add("pty,raw,echo=0");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start socat");

        var devices = new List<string>();
        var rx = new Regex(@"PTY is (\S+)");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (devices.Count < 2 && DateTime.UtcNow < deadline)
        {
            var line = await proc.StandardError.ReadLineAsync();
            if (line is null) break;
            var m = rx.Match(line);
            if (m.Success) devices.Add(m.Groups[1].Value);
        }

        if (devices.Count < 2)
        {
            try { proc.Kill(); } catch { }
            throw new InvalidOperationException("socat did not report two pty devices");
        }

        // socat needs a beat to finish wiring the pair before we open them.
        await Task.Delay(200);
        return new SocatPtyPair(proc, devices[0], devices[1]);
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}
