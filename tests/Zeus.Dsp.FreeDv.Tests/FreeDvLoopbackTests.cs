// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// End-to-end native loopback: drives a real FreeDV TX modem and a real RX
/// modem through the full Zeus path (48 kHz block in -> resample to 8 kHz ->
/// freedv_tx/comp -> modem audio -> resample to 48 kHz -> ... -> RX resample ->
/// freedv_rx -> speech) and asserts the RX side SYNCS to the transmitted
/// signal. Sync proves the whole chain works: P/Invoke marshalling, the 48k<->8k
/// polyphase resampler, the lock-free ring buffering, and the modem framing.
///
/// SkippableFact: requires the codec2 native library to be present (built into
/// runtimes/{rid}/native). Skips on machines/CI legs without it.
/// </summary>
public class FreeDvLoopbackTests
{
    [SkippableTheory]
    [InlineData(FreeDvSubmode.Mode700D)]
    [InlineData(FreeDvSubmode.Mode700E)]
    public void NativeLoopback_TxToRx_AchievesSync(FreeDvSubmode submode)
    {
        using var tx = new FreeDvModem();
        Skip.IfNot(tx.NativeAvailable, "codec2 native library not present — native loopback skipped.");
        using var rx = new FreeDvModem();

        tx.SetSubmode(submode);
        rx.SetSubmode(submode);
        // Squelch off on RX so a synced-but-low-SNR decode still yields samples;
        // we assert on the modem sync flag, not on audio level.
        rx.SetSquelch(enabled: false, threshDb: null);
        tx.Activate();
        rx.Activate();

        const int blockSize = 960;   // 20 ms @ 48 kHz
        const int txBlocks = 250;    // ~5 s of transmit — ample for OFDM acquisition

        // 1) Run a speech-like signal through TX, collecting the 48 kHz modem audio.
        var modemAudio = new List<float>(txBlocks * blockSize);
        var block = new float[blockSize];
        for (int b = 0; b < txBlocks; b++)
        {
            FillSpeechLike(block, b);
            tx.ProcessTxInPlace(block); // in place: block now holds modem audio (buffered)
            modemAudio.AddRange(block);
        }

        // 2) Feed the modem audio into RX; watch for sync.
        bool synced = false;
        int syncedBlock = -1;
        var rxBlock = new float[blockSize];
        int total = modemAudio.Count;
        for (int i = 0, n = 0; i + blockSize <= total; i += blockSize, n++)
        {
            modemAudio.CopyTo(i, rxBlock, 0, blockSize);
            rx.ProcessRxInPlace(rxBlock);
            if (rx.Synced)
            {
                synced = true;
                syncedBlock = n;
                break;
            }
        }

        Assert.True(
            synced,
            $"FreeDV {submode} RX did not sync to the looped-back TX modem signal " +
            $"after {total / blockSize} blocks. Sync flag never set — the e2e chain " +
            "(resampler / P/Invoke / modem framing) is broken.");
        Assert.InRange(syncedBlock, 0, total / blockSize);
    }

    [SkippableFact]
    public void NativeLoopback_TextSidechannel_RoundTrips()
    {
        using var tx = new FreeDvModem();
        Skip.IfNot(tx.NativeAvailable, "codec2 native library not present — text loopback skipped.");
        using var rx = new FreeDvModem();

        tx.SetSubmode(FreeDvSubmode.Mode700D);
        rx.SetSubmode(FreeDvSubmode.Mode700D);
        rx.SetSquelch(enabled: false, threshDb: null);
        tx.Activate();
        rx.Activate();
        tx.SetTxText("N9WAR");

        const int blockSize = 960;   // 20 ms @ 48 kHz
        // The txt channel is very low rate (a few bits per OFDM frame), so allow a
        // generous window for sync acquisition + one full "N9WAR\r" to arrive.
        const int txBlocks = 2500;   // ~50 s of transmit; the RX loop breaks early

        var modemAudio = new List<float>(txBlocks * blockSize);
        var block = new float[blockSize];
        for (int b = 0; b < txBlocks; b++)
        {
            FillSpeechLike(block, b);
            tx.ProcessTxInPlace(block);
            modemAudio.AddRange(block);
        }

        string? rxText = null;
        var rxBlock = new float[blockSize];
        int total = modemAudio.Count;
        for (int i = 0; i + blockSize <= total; i += blockSize)
        {
            modemAudio.CopyTo(i, rxBlock, 0, blockSize);
            rx.ProcessRxInPlace(rxBlock);
            rxText = rx.RxText;
            if (rxText is not null && rxText.Contains("N9WAR")) break;
        }

        Assert.True(
            rxText is not null && rxText.Contains("N9WAR"),
            $"FreeDV text sidechannel did not round-trip 'N9WAR' (got: '{rxText ?? "<null>"}'). " +
            "The freedv_set_callback_txt TX/RX wiring or varicode path is broken.");
    }

    [SkippableFact]
    public void NativeModem_ReportsExpectedSampleRates()
    {
        using var m = new FreeDvModem();
        Skip.IfNot(m.NativeAvailable, "codec2 native library not present.");
        m.SetSubmode(FreeDvSubmode.Mode700D);
        m.Activate();
        // 700-series voice: 8 kHz speech, 8 kHz modem.
        Assert.Equal(8000, m.SpeechSampleRateHz);
        Assert.Equal(8000, m.ModemSampleRateHz);
        Assert.StartsWith("codec2", m.LibraryVersion);
    }

    // A crude voiced-speech-like excitation: a low-frequency "pitch" fundamental
    // plus a couple of formant-band tones, mild amplitude. The exact content is
    // irrelevant to modem sync (which keys off the OFDM pilots), but it keeps the
    // Codec2 vocoder fed with non-silence so frames are produced.
    private static void FillSpeechLike(float[] buf, int blockIndex)
    {
        const int fs = 48000;
        double t0 = (double)blockIndex * buf.Length / fs;
        for (int i = 0; i < buf.Length; i++)
        {
            double t = t0 + (double)i / fs;
            double s = 0.30 * Math.Sin(2 * Math.PI * 140 * t)   // pitch
                     + 0.20 * Math.Sin(2 * Math.PI * 700 * t)   // F1-ish
                     + 0.12 * Math.Sin(2 * Math.PI * 1400 * t); // F2-ish
            buf[i] = (float)(s * 0.6);
        }
    }
}
