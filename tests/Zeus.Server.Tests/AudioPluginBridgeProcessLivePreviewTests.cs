using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Gate-logic regression for <see cref="AudioPluginBridge.ProcessLivePreview"/>.
/// The realtime preview tap must short-circuit when:
///   1. The preview flag is off (no plugins / non-Wdsp engine).
///   2. MOX is on (the WDSP TX path is the canonical chain runner).
///   3. TX monitor is on (TX path runs the chain via ProcessTxBlock).
/// When the gate passes, the bridge must invoke each slotted plugin's
/// Process once with <c>ctx.Mox == false</c> so downstream plugins can
/// distinguish "preview meter update" from "on-air audio".
/// </summary>
public class AudioPluginBridgeProcessLivePreviewTests
{
    [Fact]
    public void Preview_Disabled_Does_Not_Invoke_Plugin()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            previewEnabled: false,
            engineIsWdsp: true);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(0, spy.ProcessCallCount);
    }

    [Fact]
    public void Mox_On_Short_Circuits_Preview()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => true,            // <- MOX engaged
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(0, spy.ProcessCallCount);
    }

    [Fact]
    public void Monitor_On_Short_Circuits_Preview()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => true,        // <- audition mode
            log: NullLogger<AudioPluginBridge>.Instance);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(0, spy.ProcessCallCount);
    }

    [Fact]
    public void Gate_Pass_Invokes_Plugin_With_Mox_False()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(1, spy.ProcessCallCount);
        Assert.False(spy.LastCtxMox);
        Assert.Equal(48_000, spy.LastSampleRate);
        Assert.Equal(1, spy.LastChannels);
        Assert.Equal(256, spy.LastFrames);
    }

    [Fact]
    public void Empty_Chain_Gate_Pass_Does_Not_Throw()
    {
        // No plugins attached. The bridge should still complete the
        // ProcessLivePreview call without throwing — the underlying
        // AudioChain just pass-throughs.
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        // Note: Chain.MasterBypassed defaults to false; with zero slots it
        // is a no-op pass-through inside AudioChain.Process (all slots null).

        var ex = Record.Exception(() => RunPreview(bridge, 256));
        Assert.Null(ex);
    }

    [Fact]
    public void Preview_Mirrors_Live_Mic_Block_Size()
    {
        // Mic capture delivers 960-sample blocks (20 ms @ 48 kHz). The
        // preview path stack-allocates buffers sized to the mic block.
        // Verify the plugin sees the same frame count regardless of
        // the declared BlockSize hint in AudioPluginRequirements.
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 960);

        Assert.Equal(1, spy.ProcessCallCount);
        Assert.Equal(960, spy.LastFrames);
        Assert.False(spy.LastCtxMox);
    }

    // -- Audition wiring -----------------------------------------------

    [Fact]
    public void Audition_Enabled_Publishes_Chain_Output_To_Sink()
    {
        // Pass-through plugin so we can assert what the sink received.
        var spy = new SpyPlugin();
        var sink = new SpyAuditionSink(enabledInitial: true);
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            audition: sink);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(1, sink.PublishCallCount);
        Assert.Equal(256, sink.LastPublishLength);
        Assert.Equal(48_000, sink.LastSampleRate);
    }

    [Fact]
    public void Audition_Disabled_Skips_Sink_But_Still_Updates_Meters()
    {
        // Meter-only path — the chain still runs (plugins see Process)
        // but the audition sink should never see the output. The
        // IsEnabled short-circuit also keeps the cost of the audition
        // tap to a single virtual call.
        var spy = new SpyPlugin();
        var sink = new SpyAuditionSink(enabledInitial: false);
        var bridge = new AudioPluginBridge(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            audition: sink);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(1, spy.ProcessCallCount);     // chain still ran (meters animated)
        Assert.Equal(0, sink.PublishCallCount);    // but audition was not published
    }

    [Fact]
    public void Audition_Skipped_When_Mox_On_Even_If_Enabled()
    {
        // Existing MOX gate wins: even with audition turned on, the
        // preview path short-circuits on MOX and the audition sink
        // sees nothing for the duration of TX.
        var spy = new SpyPlugin();
        var sink = new SpyAuditionSink(enabledInitial: true);
        var bridge = new AudioPluginBridge(
            isMoxOn: () => true,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            audition: sink);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunPreview(bridge, 256);

        Assert.Equal(0, spy.ProcessCallCount);
        Assert.Equal(0, sink.PublishCallCount);
    }

    [Fact]
    public void TciRemoteTxActive_Bypasses_InsertChain_In_TxPath()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => true,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            isTciTxAudioActive: () => true);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunTxProcess(bridge, 256);

        Assert.Equal(0, spy.ProcessCallCount); // fully bypassed for remote source
    }

    [Fact]
    public void TciRemoteTxInactive_Runs_InsertChain_In_TxPath()
    {
        var spy = new SpyPlugin();
        var bridge = new AudioPluginBridge(
            isMoxOn: () => true,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            isTciTxAudioActive: () => false);
        bridge.Chain.SetSlot(0, spy);
        bridge.Chain.MasterBypassed = false;

        RunTxProcess(bridge, 256);

        Assert.Equal(1, spy.ProcessCallCount);
        Assert.True(spy.LastCtxMox);
        Assert.Equal(48000, spy.LastSampleRate);
    }

    private static void RunPreview(AudioPluginBridge bridge, int frames)
    {
        Span<float> mic = stackalloc float[frames];
        for (int i = 0; i < frames; i++) mic[i] = 0.5f * MathF.Sin(i * 0.01f);
        bridge.ProcessLivePreview(mic, sampleRate: 48_000);
    }

    private static void RunTxProcess(AudioPluginBridge bridge, int frames)
    {
        Span<float> input = stackalloc float[frames];
        Span<float> output = stackalloc float[frames];
        for (int i = 0; i < frames; i++) input[i] = 0.5f;
        bridge.ProcessTxForTest(input, output, frames);
    }

    private sealed class SpyAuditionSink : IAuditionAudioSink
    {
        public int PublishCallCount;
        public int LastPublishLength;
        public int LastSampleRate;
        public SpyAuditionSink(bool enabledInitial) { IsEnabled = enabledInitial; }
        public bool IsEnabled { get; private set; }
        public void SetEnabled(bool enabled) { IsEnabled = enabled; }
        public void PublishAudition(ReadOnlySpan<float> monoSamples, int sampleRate)
        {
            PublishCallCount++;
            LastPublishLength = monoSamples.Length;
            LastSampleRate = sampleRate;
        }
    }

    private sealed class SpyPlugin : IAudioPlugin
    {
        public int ProcessCallCount;
        public bool LastCtxMox;
        public int LastSampleRate;
        public int LastChannels;
        public int LastFrames;

        public string DisplayName => "spy";
        public AudioPluginRequirements Requirements => new(48000, 1, 256);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            ProcessCallCount++;
            LastCtxMox = ctx.Mox;
            LastSampleRate = ctx.SampleRate;
            LastChannels = ctx.Channels;
            LastFrames = ctx.Frames;
            input.CopyTo(output);
        }
    }
}
