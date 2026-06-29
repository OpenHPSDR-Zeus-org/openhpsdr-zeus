// SPDX-License-Identifier: GPL-2.0-or-later
//
// #1128 — A stored input device that has gone stale must NOT block an
// output-only device change. Before the fix the endpoint validated both
// IDs unconditionally and rejected the whole request when the stale stored
// input was missing from the live device list.

namespace Zeus.Server.Tests;

public sealed class NativeAudioDeviceChangeTests
{
    private static MiniAudioDeviceSnapshot Snapshot(string[] inputs, string[] outputs) =>
        new(
            Inputs: inputs.Select(id => new MiniAudioDeviceInfo(id, id, IsDefault: false)).ToArray(),
            Outputs: outputs.Select(id => new MiniAudioDeviceInfo(id, id, IsDefault: false)).ToArray());

    [Fact]
    public void OutputOnlyChange_AllowsStaleStoredInput()
    {
        // The stored input ("stale-mic") is no longer in the live list — the
        // operator never asked to change input this call (requestedInput
        // matches currentInput). The decision must apply the output change
        // and leave the input alone.
        var snapshot = Snapshot(
            inputs: ["live-mic"],
            outputs: ["spk-a", "spk-b"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: "stale-mic",
            requestedOutput: "spk-b",
            currentInput: "stale-mic",
            currentOutput: "spk-a",
            snapshot);

        Assert.Null(decision.RejectReason);
        Assert.False(decision.ChangeInput);
        Assert.True(decision.ChangeOutput);
    }

    [Fact]
    public void InputOnlyChange_AllowsStaleStoredOutput()
    {
        var snapshot = Snapshot(
            inputs: ["mic-a", "mic-b"],
            outputs: ["live-spk"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: "mic-b",
            requestedOutput: "stale-spk",
            currentInput: "mic-a",
            currentOutput: "stale-spk",
            snapshot);

        Assert.Null(decision.RejectReason);
        Assert.True(decision.ChangeInput);
        Assert.False(decision.ChangeOutput);
    }

    [Fact]
    public void RealInputChangeToMissingDevice_Rejects()
    {
        var snapshot = Snapshot(
            inputs: ["live-mic"],
            outputs: ["live-spk"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: "ghost-mic",
            requestedOutput: "live-spk",
            currentInput: "live-mic",
            currentOutput: "live-spk",
            snapshot);

        Assert.NotNull(decision.RejectReason);
        Assert.Contains("inputDeviceId", decision.RejectReason);
        Assert.False(decision.ChangeInput);
        Assert.False(decision.ChangeOutput);
    }

    [Fact]
    public void RealOutputChangeToMissingDevice_Rejects()
    {
        var snapshot = Snapshot(
            inputs: ["live-mic"],
            outputs: ["live-spk"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: "live-mic",
            requestedOutput: "ghost-spk",
            currentInput: "live-mic",
            currentOutput: "live-spk",
            snapshot);

        Assert.NotNull(decision.RejectReason);
        Assert.Contains("outputDeviceId", decision.RejectReason);
        Assert.False(decision.ChangeInput);
        Assert.False(decision.ChangeOutput);
    }

    [Fact]
    public void NullRequestMatchingNullCurrent_IsNoOp()
    {
        var snapshot = Snapshot(inputs: ["mic"], outputs: ["spk"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: null,
            requestedOutput: null,
            currentInput: null,
            currentOutput: null,
            snapshot);

        Assert.Null(decision.RejectReason);
        Assert.False(decision.ChangeInput);
        Assert.False(decision.ChangeOutput);
    }

    [Fact]
    public void ClearingToSystemDefault_AppliesWithoutValidation()
    {
        // Operator picks "System default" (null) — there is no device ID to
        // validate, so even an empty live list (e.g. enumeration race)
        // doesn't reject the change.
        var snapshot = Snapshot(inputs: [], outputs: []);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: null,
            requestedOutput: null,
            currentInput: "old-mic",
            currentOutput: "old-spk",
            snapshot);

        Assert.Null(decision.RejectReason);
        Assert.True(decision.ChangeInput);
        Assert.True(decision.ChangeOutput);
    }

    [Fact]
    public void IdempotentReapply_IsNoOp()
    {
        var snapshot = Snapshot(inputs: ["mic"], outputs: ["spk"]);

        var decision = NativeAudioDeviceChange.Decide(
            requestedInput: "mic",
            requestedOutput: "spk",
            currentInput: "mic",
            currentOutput: "spk",
            snapshot);

        Assert.Null(decision.RejectReason);
        Assert.False(decision.ChangeInput);
        Assert.False(decision.ChangeOutput);
    }
}
