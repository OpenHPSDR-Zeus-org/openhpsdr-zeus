// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Cat;

namespace Zeus.Server.Tests.Cat;

// End-to-end proof that a CatSerialPort, opened on a REAL serial device, speaks
// the Kenwood protocol: a virtual pty PAIR (socat) gives one end to
// CatSerialPort and the test drives the other end exactly as N1MM/WSJT-X would.
// Gated to Unix-with-socat; the protocol itself is also covered transport-
// agnostically by CatCommandHandlerTests.
public sealed class CatSerialPortIntegrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-cat-serial-int-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    [SkippableFact]
    public async Task RealSerialPair_RoundTripsKenwoodCommands()
    {
        Skip.IfNot(CatSerialTestSupport.PtyHarnessAvailable, "socat pty pair unavailable (POSIX + socat only)");
        using var pair = await SocatPtyPair.CreateAsync(CatSerialTestSupport.ResolveSocat()!);

        var (radio, tx, _pipeline, dispose) = CatSerialTestSupport.BuildRadio(_dbPath);
        using var _ = dispose;
        double latestDbm = -73.0;

        // Zeus side: open pty A, run the read loop.
        using var catPort = new CatSerialPort(
            pair.DeviceA, 115200, Parity.None, 8, StopBits.One,
            radio, tx, new CatOptions(), () => latestDbm, NullLogger<CatSerialPort>.Instance);
        catPort.Open();
        using var loopCts = new CancellationTokenSource();
        var loop = catPort.RunAsync(loopCts.Token);

        // Test (client) side: open pty B as a plain serial port.
        using var client = OpenClient(pair.DeviceB);

        // 1) ID; → the TS-2000 identity. Hamlib requires this first.
        client.Write("ID;");
        Assert.Equal("ID019;", await ReadResponseAsync(client));

        // 2) FA<freq>; is a set with no reply — assert the radio actually tuned.
        client.Write("FA00007074000;");
        await WaitForAsync(() => radio.Snapshot().VfoHz == 7_074_000);
        Assert.Equal(7_074_000, radio.Snapshot().VfoHz);

        // 3) FA; query → reads the freq back over the wire.
        client.Write("FA;");
        Assert.Equal("FA00007074000;", await ReadResponseAsync(client));

        loopCts.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* torn down */ }
    }

    internal static SerialPort OpenClient(string device)
    {
        var client = new SerialPort(device, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            Encoding = Encoding.ASCII,
        };
        client.Open();
        return client;
    }

    // Read until a ';' terminator (or timeout). The pty is raw, so bytes arrive
    // as written; accumulate until the Kenwood frame completes.
    internal static async Task<string> ReadResponseAsync(SerialPort port)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                int b = port.ReadByte();
                if (b < 0) continue;
                sb.Append((char)b);
                if (b == ';') return sb.ToString();
            }
            catch (TimeoutException)
            {
                await Task.Delay(10);
            }
        }
        throw new TimeoutException($"No terminated CAT response (got '{sb}')");
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
