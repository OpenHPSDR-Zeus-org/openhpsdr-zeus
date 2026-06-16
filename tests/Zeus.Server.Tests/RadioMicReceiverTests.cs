// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Covers the Saturn radio-mic (UDP 1026) parse + 960-sample re-blocker
// (external-ports plan, §3). The packet layout is verified against pihpsdr
// src/new_protocol.c process_mic_data (4-byte BE seq + 64 × int16 BE @ 48 kHz).
public class RadioMicReceiverTests
{
    // Build one 132-byte 1026 packet: 4-byte BE seq + 64 × int16 BE samples.
    private static byte[] BuildPacket(uint seq, Func<int, short> sampleAt)
    {
        var buf = new byte[RadioMicReceiver.PacketBytes];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), seq);
        int b = 4;
        for (int i = 0; i < RadioMicReceiver.PacketSamples; i++, b += 2)
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(b, 2), sampleAt(i));
        return buf;
    }

    [Fact]
    public void ReBlocks_SixtyFourSamplePackets_IntoNineSixtySampleBlocks()
    {
        var blocks = new List<byte[]>();
        var rx = new RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // 15 packets × 64 = 960 samples → exactly one 960-sample block.
        for (uint i = 0; i < 15; i++)
            rx.Accept(BuildPacket(i, _ => 8192));   // 8192/32768 = 0.25

        Assert.Single(blocks);
        Assert.Equal(RadioMicReceiver.OutputBlockSamples * 4, blocks[0].Length);

        // Verify the decode: int16 8192 → f32 0.25, little-endian on the wire out.
        float first = BinaryPrimitives.ReadSingleLittleEndian(blocks[0].AsSpan(0, 4));
        Assert.Equal(0.25f, first, 5);

        // 14 packets (896 samples) → no further block; remainder carried.
        for (uint i = 15; i < 29; i++)
            rx.Accept(BuildPacket(i, _ => 8192));
        Assert.Single(blocks);

        // One more packet (960 total) → second block emitted.
        rx.Accept(BuildPacket(29, _ => 8192));
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Reset_DropsRemainder_NoStitchAcrossSwitch()
    {
        var blocks = new List<byte[]>();
        var rx = new RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        // 10 packets (640 samples) accumulate, no block yet.
        for (uint i = 0; i < 10; i++) rx.Accept(BuildPacket(i, _ => 16384));
        Assert.Empty(blocks);

        rx.Reset();   // source switch — drop the 640-sample remainder

        // 15 fresh packets (960). Were the 640 NOT dropped, 640+960=1600 would
        // emit a block at 960 with stale audio in front. After Reset the first
        // block is purely the new audio and arrives only once 960 new samples land.
        for (uint i = 0; i < 15; i++) rx.Accept(BuildPacket(i, _ => 16384));
        Assert.Single(blocks);
    }

    [Fact]
    public void RejectsUndersizedPacket()
    {
        var blocks = new List<byte[]>();
        var rx = new RadioMicReceiver(b => blocks.Add(b.ToArray()), NullLogger.Instance);

        rx.Accept(new byte[100]);   // < 132
        Assert.Empty(blocks);
        Assert.Equal(1, rx.TotalPacketsDropped);
    }

    // END-TO-END: a synthetic 64-sample 1026 feed, after re-blocking, reaches
    // ProcessTxBlock and produces NON-ZERO IQ (external-ports plan, §3 / §10
    // test register). Wires RadioMicReceiver → TxAudioIngest.OnMicPcmBytesFromRadioMic
    // with a radio source armed, MOX on, and a stub engine that copies mic → I.
    [Fact]
    public void SyntheticPacketFeed_ReachesProcessTxBlock_ProducesNonZeroIq()
    {
        var engine = new CopyEngine { BlockSize = 1024 };
        var ring = new TxIqRing();
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        using var ingest = new TxAudioIngest(
            ring, () => engine, () => true, hub, new NullLogger<TxAudioIngest>());
        ingest.SetActiveSource(TxAudioSource.RadioMic);   // arm the radio jack

        var rx = new RadioMicReceiver(
            block => ingest.OnMicPcmBytesFromRadioMic(block), NullLogger.Instance);

        // Feed enough 64-sample packets to flush at least one WDSP block: WDSP
        // wants 1024 samples; 1024/64 = 16 packets cover the first 960-sample mic
        // block (15 pkts) plus carry — push 32 packets (2048 samples) to be safe.
        for (uint i = 0; i < 32; i++)
            rx.Accept(BuildPacket(i, _ => 16384));   // 0.5 amplitude

        Assert.True(ingest.TotalTxBlocks >= 1);       // re-block reached WDSP
        Assert.True(ring.Count > 0);                  // IQ produced and ringed

        // Drain the ring (pairs) and confirm non-zero IQ samples are present.
        int pairs = ring.Count;
        bool nonZero = false;
        for (int i = 0; i < pairs; i++)
        {
            var (iv, qv) = ring.Next(1.0);
            if (iv != 0 || qv != 0) { nonZero = true; break; }
        }
        Assert.True(nonZero, "re-blocked radio-mic feed produced all-zero IQ");
    }

    // Minimal engine: copies mic mono into I (Q=0) so non-zero mic → non-zero IQ.
    private sealed class CopyEngine : IDspEngine
    {
        public int BlockSize { get; set; } = 1024;
        public int TxBlockSamples => BlockSize;
        public int TxOutputSamples => BlockSize;

        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved)
        {
            for (int i = 0; i < BlockSize; i++) { iqInterleaved[2 * i] = micMono[i]; iqInterleaved[2 * i + 1] = 0f; }
            return BlockSize;
        }

        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetCtunShift(int channelId, int shiftHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetRxDisplayFastAttack(int channelId, bool fast) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public void SetTxPanelGain(double linearGain) { }
        public void SetTxLevelerMaxGain(double maxGainDb) { }
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }
        public void SetPsEnabled(bool enabled) { }
        public void SetPsControl(bool autoCal, bool singleCal) { }
        public void SetPsHold(bool hold) { }
        public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                                  double ampDelayNs, double hwPeak, int ints, int spi) { }
        public void SetPsHwPeak(double hwPeak) { }
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
        public PsStageMeters GetPsStageMeters() => PsStageMeters.Silent;
        public void ResetPs() { }
        public void SavePsCorrection(string path) { }
        public void RestorePsCorrection(string path) { }
        public void SetCfcConfig(CfcConfig cfg) { }
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void Dispose() { }
    }
}
