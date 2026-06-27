// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Issue #1053 — ANAN-10E (HermesII) running Protocol 2 firmware had no line-in
// audio because RadioService.PushAudioFrontEnd dispatched the external-port
// encoder by the BOARD'S DEFAULT protocol (HermesII -> Protocol 1) instead of
// the LIVE transport. The P1 encoder's P2-byte surfaces are zero, so the
// TxSpecific byte-50 "switch to line-in" bit was never set and the radio's
// codec stayed on its default mic input. These tests pin the live-protocol
// dispatch so the regression can't return.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioFrontEndProtocolDispatchTests : IDisposable
{
    private readonly string _dbPath;

    public AudioFrontEndProtocolDispatchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-audiodispatch-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa", ".dsp", ".audio" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private PaSettingsStore NewPaStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");

    private DspSettingsStore NewDspStore() =>
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");

    private AudioSettingsStore NewAudioStore() =>
        new AudioSettingsStore(NullLogger<AudioSettingsStore>.Instance, _dbPath + ".audio");

    [Fact]
    public void HermesII_OnProtocol2_RadioLineIn_PushesP2LineInBit()
    {
        // Simulate an ANAN-10E running Protocol 2 firmware with the operator's
        // persisted source set to RadioLineIn. The wire byte-50 must carry the
        // line-in select bit (0x01) and byte-51 must carry the gain — without
        // this, the radio's TLV320 codec stays on its default mic input and the
        // user reports "no audio on line in" (issue #1053).
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        prefs.Set(HpsdrBoardKind.HermesII, overrideDetection: true);

        using var audio = NewAudioStore();
        audio.Set(new AudioSourceSelection(
            Source: TxAudioSource.RadioLineIn, MicBoost: false, MicBias: false, LineInGain: 17));

        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore(),
            preferredRadioStore: prefs,
            audioStore: audio);

        AudioFrontEndPush? lastPush = null;
        radio.AudioFrontEndChanged += p => lastPush = p;

        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);
        radio.ReplayAudioFrontEnd();

        Assert.NotNull(lastPush);
        Assert.Equal(TxAudioSource.RadioLineIn, lastPush!.Source);
        Assert.Equal((byte)0x01, lastPush.MicControlByte);
        Assert.Equal((byte)17, lastPush.LineInGain);
    }

    [Fact]
    public void HermesII_OnProtocol2_Host_KeepsBytesZero()
    {
        // The Host default must remain byte-identical to today on P2 — the
        // override forces the P2 encoder but Host returns literal 0 on every
        // surface (no param read), so a stale persisted gain can never leak.
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        prefs.Set(HpsdrBoardKind.HermesII, overrideDetection: true);

        using var audio = NewAudioStore();
        audio.Set(new AudioSourceSelection(
            Source: TxAudioSource.Host, MicBoost: true, MicBias: true, LineInGain: 31));

        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore(),
            preferredRadioStore: prefs,
            audioStore: audio);

        AudioFrontEndPush? lastPush = null;
        radio.AudioFrontEndChanged += p => lastPush = p;

        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);
        radio.ReplayAudioFrontEnd();

        Assert.NotNull(lastPush);
        Assert.Equal(TxAudioSource.Host, lastPush!.Source);
        Assert.Equal((byte)0x00, lastPush.MicControlByte);
        Assert.Equal((byte)0x00, lastPush.LineInGain);
    }

    [Fact]
    public void HermesII_OnProtocol1_RadioLineIn_KeepsP2BytesZero()
    {
        // On Protocol 1 the line-in select rides the 0x12 codec frame (handled
        // separately by IProtocol1Client.SetAudioFrontEnd); the P2 byte-50/51
        // surfaces stay zero. This is the byte-identical-to-today invariant for
        // the P1 path — the fix must not change it.
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        prefs.Set(HpsdrBoardKind.HermesII, overrideDetection: true);

        using var audio = NewAudioStore();
        audio.Set(new AudioSourceSelection(
            Source: TxAudioSource.RadioLineIn, MicBoost: false, MicBias: false, LineInGain: 17));

        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore(),
            preferredRadioStore: prefs,
            audioStore: audio);

        AudioFrontEndPush? lastPush = null;
        radio.AudioFrontEndChanged += p => lastPush = p;

        // No MarkProtocol2Connected — _p2Active stays false, so dispatch falls
        // back to DefaultProtocolFor(HermesII) == Protocol1.
        radio.ReplayAudioFrontEnd();

        Assert.NotNull(lastPush);
        Assert.Equal(TxAudioSource.RadioLineIn, lastPush!.Source);
        Assert.Equal((byte)0x00, lastPush.MicControlByte);
        Assert.Equal((byte)0x00, lastPush.LineInGain);
    }
}
