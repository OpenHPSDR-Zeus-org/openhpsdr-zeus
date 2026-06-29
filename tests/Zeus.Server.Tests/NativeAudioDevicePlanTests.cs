// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tests;

public sealed class NativeAudioDevicePlanTests
{
    private static readonly string[] Inputs = ["mic-a", "mic-b"];
    private static readonly string[] Outputs = ["spk-a", "spk-b"];

    // #1128 core case: operator changes ONLY the output while the previously
    // configured input mic is gone. The unchanged stale input must not block
    // the output change.
    [Fact]
    public void StaleUnchangedInput_DoesNotBlockOutputChange()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: "mic-gone", requestedInput: "mic-gone", availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: "spk-b", availableOutputIds: Outputs);

        Assert.False(plan.ApplyInput);
        Assert.Null(plan.InputError);
        Assert.True(plan.ApplyOutput);
        Assert.Null(plan.OutputError);
    }

    [Fact]
    public void ChangingInputToPresentDevice_Applies()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: null, requestedInput: "mic-b", availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: "spk-a", availableOutputIds: Outputs);

        Assert.True(plan.ApplyInput);
        Assert.Null(plan.InputError);
        Assert.False(plan.ApplyOutput);   // unchanged
    }

    [Fact]
    public void ChangingInputToMissingDevice_Errors()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: "mic-a", requestedInput: "mic-x", availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: "spk-a", availableOutputIds: Outputs);

        Assert.False(plan.ApplyInput);
        Assert.NotNull(plan.InputError);
    }

    [Fact]
    public void ChangingToOsDefault_AlwaysApplies()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: "mic-a", requestedInput: null, availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: null, availableOutputIds: Outputs);

        Assert.True(plan.ApplyInput);
        Assert.Null(plan.InputError);
        Assert.True(plan.ApplyOutput);
        Assert.Null(plan.OutputError);
    }

    [Fact]
    public void NoOpRequest_AppliesNothing()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: "mic-a", requestedInput: "mic-a", availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: "spk-a", availableOutputIds: Outputs);

        Assert.False(plan.ApplyInput);
        Assert.False(plan.ApplyOutput);
        Assert.Null(plan.InputError);
        Assert.Null(plan.OutputError);
    }

    // RX-only desktop builds (no mic service): a supplied input id is ignored
    // rather than rejected, and the output change still goes through.
    [Fact]
    public void NoMicService_IgnoresInputAndAppliesOutput()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: false, currentInput: null, requestedInput: "mic-x", availableInputIds: [],
            hasSink: true, currentOutput: null, requestedOutput: "spk-a", availableOutputIds: Outputs);

        Assert.False(plan.ApplyInput);
        Assert.Null(plan.InputError);
        Assert.True(plan.ApplyOutput);
    }

    [Fact]
    public void ChangingOutputToMissingDevice_Errors()
    {
        var plan = NativeAudioDevicePlan.Plan(
            hasMic: true, currentInput: "mic-a", requestedInput: "mic-a", availableInputIds: Inputs,
            hasSink: true, currentOutput: "spk-a", requestedOutput: "spk-x", availableOutputIds: Outputs);

        Assert.False(plan.ApplyOutput);
        Assert.NotNull(plan.OutputError);
    }
}
