// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Protocol2.Tests;

public class TxIqQuantizerTests
{
    [Theory]
    [InlineData(1.0f, 8_388_607)]
    [InlineData(2.5f, 8_388_607)]
    [InlineData(-1.0f, -8_388_607)]
    [InlineData(-2.5f, -8_388_607)]
    [InlineData(0.5f, 4_194_304)]
    public void Int24Clamp_ClampsToSigned24BitDomain(float sample, int expected)
    {
        Assert.Equal(expected, Protocol2Client.Int24Clamp(sample));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Int24Clamp_ZerosNonFiniteSamples(float sample)
    {
        Assert.Equal(0, Protocol2Client.Int24Clamp(sample));
    }

    [Fact]
    public void TxIqDiagnosticsSnapshot_StartsEmptyAndStopped()
    {
        var client = new Protocol2Client(NullLogger<Protocol2Client>.Instance);

        var diag = client.TxIqDiagnosticsSnapshot();

        Assert.Equal(0, diag.InputComplexSamples);
        Assert.Equal(0, diag.PacketsQueued);
        Assert.Equal(0, diag.PacketsSent);
        Assert.Equal(0, diag.QueuedPackets);
        Assert.Equal(0, diag.QueueWriteFailures);
        Assert.Equal(0, diag.SendFailures);
        Assert.Equal(0, diag.ResetDrainedPackets);
        Assert.Equal(0, diag.ScratchComplexSamples);
        Assert.Equal(0u, diag.NextSequence);
        Assert.Equal(0, diag.LastPacketsPerSecond);
        Assert.Equal(0, diag.LastFifoModelSamples);
        Assert.Null(diag.LastRateTimestampUtc);
        Assert.False(diag.SenderRunning);
    }

    [Fact]
    public void SendTxIq_CapsQueuedPacketsToRealtimeWindow()
    {
        var client = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        using var sock = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        typeof(Protocol2Client)
            .GetField("_sock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, sock);
        typeof(Protocol2Client)
            .GetField("_rxTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, Task.CompletedTask);
        var iq = new float[240 * 2 * 40];
        Array.Fill(iq, 0.1f);

        client.SendTxIq(iq);
        var diag = client.TxIqDiagnosticsSnapshot();

        Assert.Equal(40, diag.PacketsQueued);
        Assert.Equal(32, diag.QueuedPackets);
        Assert.Equal(40u, diag.NextSequence);
    }
}
