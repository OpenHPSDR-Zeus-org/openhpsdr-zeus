using Xunit;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DspPipelineRx2RoutingTests
{
    private static StateDto State(RxMode mode) => new(
        Status: ConnectionStatus.Connected,
        Endpoint: "192.168.1.25:1024",
        VfoHz: 14_200_000,
        Mode: mode,
        FilterLowHz: 300,
        FilterHighHz: 2600,
        SampleRate: 384_000,
        RadioLoHz: 14_200_000,
        // RX2 tuning lives in the canonical Receivers[1] entry (VFO-B fields gone).
        Receivers: new ReceiverDto[]
        {
            new(Index: 0, Enabled: true, AdcSource: 0, VfoHz: 14_200_000, Mode: mode,
                FilterLowHz: 300, FilterHighHz: 2600, FilterPresetName: "VAR1", AfGainDb: 0,
                SampleRateHz: 384_000, Muted: false),
            new(Index: 1, Enabled: true, AdcSource: 0, VfoHz: 14_250_000, Mode: mode,
                FilterLowHz: 300, FilterHighHz: 2600, FilterPresetName: "VAR1", AfGainDb: 0,
                SampleRateHz: 384_000, Muted: false),
        });

    [Fact]
    public void Rx2CtunShift_Protocol2TrueDdc_UsesRx2DdcCenter()
    {
        var state = State(RxMode.USB);

        int shift = DspPipelineService.ComputeRx2CtunShiftHz(
            state,
            rx2LoHz: 14_240_000,
            protocol2: true);

        Assert.Equal(10_000, shift);
    }

    [Fact]
    public void Rx2CtunShift_NonProtocol2_UsesPrimaryReceiverCenter()
    {
        var state = State(RxMode.USB);

        int shift = DspPipelineService.ComputeRx2CtunShiftHz(
            state,
            rx2LoHz: 14_240_000,
            protocol2: false);

        Assert.Equal(50_000, shift);
    }

    [Fact]
    public void Rx2CtunShift_AppliesCwPitchBeforeChoosingCenter()
    {
        var state = State(RxMode.CWU);

        int shift = DspPipelineService.ComputeRx2CtunShiftHz(
            state,
            rx2LoHz: 14_240_000,
            protocol2: true);

        Assert.Equal(9_400, shift);
    }

    [Fact]
    public void Protocol1IqFeed_WithRx2Channel_FeedsPrimaryAndRx2()
    {
        var engine = new RecordingEngine();
        double[] iq = [1.0, -1.0, 0.25, -0.25];

        DspPipelineService.FeedProtocol1Iq(engine, channel: 0, rx2Channel: 1, iq);

        Assert.Equal([0, 1], engine.FeedChannels);
        Assert.Equal(iq, engine.FeedSamples[0]);
        Assert.Equal(iq, engine.FeedSamples[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Protocol1IqFeed_WithoutDistinctRx2Channel_FeedsPrimaryOnly(int rx2Channel)
    {
        var engine = new RecordingEngine();
        double[] iq = [0.5, -0.5];

        DspPipelineService.FeedProtocol1Iq(engine, channel: 0, rx2Channel, iq);

        Assert.Equal([0], engine.FeedChannels);
        Assert.Equal(iq, engine.FeedSamples[0]);
    }

    [Fact]
    public void MixRxAudioN_Rx1Muted_PlaysSecondaryAtFullAmplitude()
    {
        // "RX2 only" in the per-RX mute model = mute RX1. RX1's samples are
        // already zeroed by the caller; rx1Muted drops it from the divisor so the
        // single unmuted secondary passes through at full amplitude (NOT halved).
        float[] rx1 = new float[3];                  // RX1 muted → pre-zeroed
        float[] rx2 = [0.40f, -0.50f, 0.30f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 3,
            new[] { new DspPipelineService.RxAudioSlice(rx2, rx2.Length) },
            rx1Muted: true);

        Assert.Equal(3, count);
        Assert.Equal(0.40f, rx1[0], 5);
        Assert.Equal(-0.50f, rx1[1], 5);
        Assert.Equal(0.30f, rx1[2], 5);
    }

    [Fact]
    public void MixRxAudioN_Rx1Muted_TwoSecondaries_AveragesOnlyThem()
    {
        // RX1 muted, two unmuted secondaries → divide by 2 (RX1 excluded).
        float[] rx1 = new float[2];
        float[] rx2 = [0.40f, 0.20f];
        float[] rx3 = [0.20f, 0.40f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 2,
            new[]
            {
                new DspPipelineService.RxAudioSlice(rx2, rx2.Length),
                new DspPipelineService.RxAudioSlice(rx3, rx3.Length),
            },
            rx1Muted: true);

        Assert.Equal(2, count);
        Assert.Equal(0.30f, rx1[0], 5);   // (0.40+0.20)/2 — RX1 not in divisor
        Assert.Equal(0.30f, rx1[1], 5);   // (0.20+0.40)/2
    }

    [Fact]
    public void MixRxAudioN_Rx1Muted_NoUnmutedReceivers_ReturnsSilence()
    {
        // Everything muted (RX1 muted, no contributing slices) → silence.
        float[] rx1 = new float[3];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 3,
            System.ReadOnlySpan<DspPipelineService.RxAudioSlice>.Empty,
            rx1Muted: true);

        Assert.Equal(0, count);
    }

    [Fact]
    public void MixRxAudioN_SingleSlice_MatchesLegacyHalfMix()
    {
        // One non-empty slice (RX2) must reproduce the old 0.5*(rx1+rx2) mix,
        // including the diluted tail where only RX1 is present.
        float[] rx1 = [0.10f, -0.20f, 0.30f, 0.40f];
        float[] rx2 = [0.40f, -0.50f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 4,
            new[] { new DspPipelineService.RxAudioSlice(rx2, rx2.Length) });

        Assert.Equal(4, count);
        Assert.Equal(0.25f, rx1[0], 5);   // (0.10 + 0.40)/2
        Assert.Equal(-0.35f, rx1[1], 5);  // (-0.20 - 0.50)/2
        Assert.Equal(0.15f, rx1[2], 5);   // (0.30 + 0)/2  — tail still halved
        Assert.Equal(0.20f, rx1[3], 5);   // (0.40 + 0)/2
    }

    [Fact]
    public void MixRxAudioN_ThreeReceivers_AveragesAllPresent()
    {
        // RX1 + RX2 + RX3 all full-length → divide by 3.
        float[] rx1 = [0.30f, 0.60f];
        float[] rx2 = [0.30f, 0.00f];
        float[] rx3 = [0.30f, 0.30f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 2,
            new[]
            {
                new DspPipelineService.RxAudioSlice(rx2, rx2.Length),
                new DspPipelineService.RxAudioSlice(rx3, rx3.Length),
            });

        Assert.Equal(2, count);
        Assert.Equal(0.30f, rx1[0], 5);   // (0.30+0.30+0.30)/3
        Assert.Equal(0.30f, rx1[1], 5);   // (0.60+0.00+0.30)/3
    }

    [Fact]
    public void MixRxAudioN_EmptySlicesExcludedFromDivisor()
    {
        // A secondary that produced no samples this tick must not dilute RX1:
        // contributor count is 1, so RX1 passes through untouched.
        float[] rx1 = [0.50f, -0.50f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 2,
            new[]
            {
                new DspPipelineService.RxAudioSlice(System.Array.Empty<float>(), 0),
                new DspPipelineService.RxAudioSlice([0.10f, 0.10f], 0),
            });

        Assert.Equal(2, count);
        Assert.Equal(0.50f, rx1[0], 5);
        Assert.Equal(-0.50f, rx1[1], 5);
    }

    [Fact]
    public void MixRxAudioN_Rx1Silent_PassesSecondariesThrough()
    {
        // RX1 produced nothing this tick (rx1Count==0); the two secondaries are
        // averaged and passed through at the longer block length.
        float[] rx1 = new float[3];
        float[] rx2 = [0.40f, 0.40f, 0.40f];
        float[] rx3 = [0.20f, 0.20f];

        int count = DspPipelineService.MixRxAudioN(
            rx1,
            rx1Count: 0,
            new[]
            {
                new DspPipelineService.RxAudioSlice(rx2, rx2.Length),
                new DspPipelineService.RxAudioSlice(rx3, rx3.Length),
            });

        Assert.Equal(3, count);
        Assert.Equal(0.30f, rx1[0], 5);   // (0.40+0.20)/2
        Assert.Equal(0.30f, rx1[1], 5);   // (0.40+0.20)/2
        Assert.Equal(0.20f, rx1[2], 5);   // (0.40 only)/2 — divisor is contributor count (2)
    }

    private sealed class RecordingEngine : IDspEngine
    {
        public List<int> FeedChannels { get; } = [];
        public List<double[]> FeedSamples { get; } = [];

        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public bool IsTxMonitorOn => false;

        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples)
        {
            FeedChannels.Add(channelId);
            FeedSamples.Add(interleavedIqSamples.ToArray());
        }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetCtunShift(int channelId, int shiftHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetAgcThresh(int channelId, double threshDbm) { }
        public double GetAgcTop(int channelId) => 0.0;
        public double GetAgcThresh(int channelId) => 0.0;
        public void SetAgc(int channelId, AgcConfig cfg) { }
        public void SetSquelch(int channelId, SquelchConfig cfg) { }
        public void SetTxLeveling(int channelId, TxLevelingConfig cfg) { }
        public void SetRxDisplayFastAttack(int channelId, bool fast) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public bool LoadNr3Model(string? modelFilePath) => false;
        public void SetNotches(IReadOnlyList<NotchDto> notches) { }
        public void SetNotchTuneFrequencyHz(double loHz) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public void ConfigureTxDisplayAnalyzer(int fftSize, int windowType, double avgTauSec) { }
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public void SetRxBandpassWindow(int channelId, BandpassWindow window) { }
        public void SetTxBandpassWindow(BandpassWindow window) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
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
        public void Dispose() { }
    }
}
