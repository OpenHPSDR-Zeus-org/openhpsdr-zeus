using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Proves the ADR-0008 hard invariant: a remote session is LOCKED by default and
/// nothing radio-related is permitted until the password proves out. These are
/// the structural guarantees the WebRTC transport (Phase 1+) is built on.
/// </summary>
public sealed class RemoteSessionTests
{
    [Fact]
    public void StartsLocked_RefusesEgressAndControl()
    {
        var s = new RemoteSession(DenyAllAuthGate.Instance);

        Assert.Equal(RemoteSessionState.Locked, s.State);
        Assert.False(s.IsUnlocked);
        Assert.False(s.TryEgress());          // no audio/display/IQ/meters
        Assert.False(s.TryDispatchControl());  // no VFO/mode/TX
    }

    [Fact]
    public async Task DenyAllGate_NeverUnlocks_ClosesOnAttempt_LeaksNothing()
    {
        var s = new RemoteSession(DenyAllAuthGate.Instance);

        var outcome = await s.SubmitAuthAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(RemoteSessionAction.Close, outcome.Action);
        Assert.Equal(RemoteSessionState.Closed, s.State);
        Assert.False(s.TryEgress());
        Assert.False(s.TryDispatchControl());
    }

    [Fact]
    public async Task Unlocks_OnlyWhenGateApproves_ThenArmsDataPaths()
    {
        var s = new RemoteSession(new UnlockOnNthGate(unlockOn: 2));

        // First proof step: still LOCKED, nothing armed.
        var first = await s.SubmitAuthAsync(new byte[] { 0 });
        Assert.Equal(RemoteSessionAction.Reply, first.Action);
        Assert.False(s.IsUnlocked);
        Assert.False(s.TryEgress());

        // Second step: gate approves → UNLOCKED, data paths armed.
        var second = await s.SubmitAuthAsync(new byte[] { 0 });
        Assert.Equal(RemoteSessionAction.Unlock, second.Action);
        Assert.True(s.IsUnlocked);
        Assert.True(s.TryEgress());
        Assert.True(s.TryDispatchControl());
    }

    [Fact]
    public async Task AuthAfterUnlock_IsRejectedAsAbuse()
    {
        var s = new RemoteSession(new UnlockOnNthGate(unlockOn: 1));
        await s.SubmitAuthAsync(new byte[] { 0 });
        Assert.True(s.IsUnlocked);

        var again = await s.SubmitAuthAsync(new byte[] { 0 });

        Assert.Equal(RemoteSessionAction.Close, again.Action);
        Assert.Equal(RemoteSessionState.Closed, s.State);
        Assert.False(s.TryEgress());
    }

    [Fact]
    public async Task GateException_FailsClosed()
    {
        var s = new RemoteSession(new ThrowingGate());

        var outcome = await s.SubmitAuthAsync(new byte[] { 0 });

        Assert.Equal(RemoteSessionAction.Close, outcome.Action);
        Assert.Equal(RemoteSessionState.Closed, s.State);
    }

    private sealed class UnlockOnNthGate(int unlockOn) : IRemoteAuthGate
    {
        private int _n;
        public ValueTask<RemoteAuthStep> StepAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default)
            => ValueTask.FromResult(++_n >= unlockOn ? RemoteAuthStep.Unlock() : RemoteAuthStep.Continue(default));
    }

    private sealed class ThrowingGate : IRemoteAuthGate
    {
        public ValueTask<RemoteAuthStep> StepAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
