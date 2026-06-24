// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

// RadeModem covers two contracts:
//   - native ABSENT (CI without the zeus_rade artifact for the RID): RX passes
//     audio through unchanged, TX SILENCES (never transmits raw mic on a RADE
//     frequency), and nothing throws.
//   - native PRESENT (win-x64 ships zeus_rade.dll): a full managed TX -> RX
//     loopback proves the C# wiring (resamplers, rings, P/Invoke marshalling)
//     carries the proven native modem end-to-end — it must SYNC and decode.
public class RadeModemTests
{
    [Fact]
    public void Inactive_ProcessRx_IsNoOp()
    {
        using var modem = new RadeModem();
        var block = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var expected = (float[])block.Clone();
        modem.ProcessRxInPlace(block); // not active
        Assert.Equal(expected, block);
    }

    [Fact]
    public void RxText_NullBeforeAnyDecode()
    {
        using var modem = new RadeModem();
        Assert.Null(modem.RxText);
    }

    [Fact]
    public void Activate_Deactivate_TogglesActive()
    {
        using var modem = new RadeModem();
        modem.Activate();
        Assert.True(modem.Active);
        modem.Deactivate();
        Assert.False(modem.Active);
    }

    [Fact]
    public void SetTxText_AndFlush_DoNotThrow()
    {
        using var modem = new RadeModem();
        modem.SetTxText("N9WAR");
        modem.Activate();
        modem.SetTxText("EI6LF");   // change while active
        modem.EmitEoo();            // no-op when native missing
        modem.FlushTx();
    }

    [SkippableFact]
    public void WhenNativeMissing_ActiveTx_Silences_AndRxPassesThrough()
    {
        using var modem = new RadeModem();
        Skip.If(modem.RadeAvailable, "zeus_rade native present — silence/passthrough path not exercised.");

        modem.Activate();
        Assert.True(modem.Active);

        var rx = MakeTone(480);
        var rxExpected = (float[])rx.Clone();
        modem.ProcessRxInPlace(rx);
        Assert.Equal(rxExpected, rx); // RX passthrough — buffer untouched

        var tx = MakeTone(480);
        modem.ProcessTxInPlace(tx);
        Assert.All(tx, s => Assert.Equal(0f, s)); // TX silenced (no raw mic on RADE freq)
    }

    [SkippableFact]
    public void Loopback_EncodeThenDecode_Syncs()
    {
        using var modem = new RadeModem();
        Skip.IfNot(modem.RadeAvailable, "zeus_rade native library not present for this RID.");

        modem.SetTxText("N9WAR");
        modem.Activate();

        // ~8 s of synthetic voiced speech @48k, encoded block by block. The TX hot
        // path replaces each block with the RADE modem waveform (real SSB audio).
        const int fs = 48000, block = 4096;
        int blocks = (8 * fs) / block;
        var modemAudio = new List<float>(blocks * block);
        double phase = 0;
        bool txNonSilent = false;
        for (int b = 0; b < blocks; b++)
        {
            var buf = MakeSpeech(block, ref phase);
            modem.ProcessTxInPlace(buf);
            foreach (var s in buf) { if (MathF.Abs(s) > 1e-4f) txNonSilent = true; }
            modemAudio.AddRange(buf);
        }
        Assert.True(txNonSilent, "TX produced only silence — encode path not wired.");

        // Feed the encoded modem audio straight back into the RX hot path. The
        // decoder must acquire lock (Synced) — the managed chain carries the same
        // waveform the native loopback already proved decodable.
        var arr = modemAudio.ToArray();
        bool synced = false;
        bool decodedAudio = false;
        for (int off = 0; off + block <= arr.Length; off += block)
        {
            var seg = new float[block];
            Array.Copy(arr, off, seg, 0, block);
            modem.ProcessRxInPlace(seg);
            if (modem.Synced) synced = true;
            foreach (var s in seg) { if (MathF.Abs(s) > 1e-4f) decodedAudio = true; }
        }
        Assert.True(synced, "RADE RX never synced on its own TX output.");
        Assert.True(decodedAudio, "RADE RX produced no decoded audio.");
    }

    private static float[] MakeTone(int n)
    {
        var b = new float[n];
        for (int i = 0; i < n; i++) b[i] = 0.2f * MathF.Sin(i * 0.1f);
        return b;
    }

    // Voiced-ish 16 kHz-content speech at 48 kHz: a low pitch + harmonics + slow
    // tremolo. Content is irrelevant to modem sync (RADE locks on pilots) but it
    // exercises the LPCNet analyzer with realistic input.
    private static float[] MakeSpeech(int n, ref double phase)
    {
        const double f0 = 130.0, fs = 48000.0;
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (phase += 1.0 / fs);
            double trem = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 3.0 * t);
            double s = 0;
            for (int h = 1; h <= 6; h++) s += (1.0 / h) * Math.Sin(2 * Math.PI * f0 * h * t);
            b[i] = (float)(trem * s * 0.18);
        }
        return b;
    }
}
