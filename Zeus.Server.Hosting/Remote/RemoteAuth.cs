namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Lifecycle of a remote session. ADR-0008 hard invariant: a session begins
/// <see cref="Locked"/> and does nothing radio-related until the operator's
/// session password proves out, transitioning it to <see cref="Unlocked"/>.
/// Any violation moves it to <see cref="Closed"/>.
/// </summary>
public enum RemoteSessionState
{
    Locked,
    Unlocked,
    Closed,
}

/// <summary>
/// Result of one auth step. A multi-message proof (e.g. SPAKE2) returns
/// <see cref="Continue"/> while still locked until it finally returns
/// <see cref="Unlock"/>, or <see cref="Rejected"/> to fail closed.
/// </summary>
public readonly record struct RemoteAuthStep(bool Unlocked, bool Reject, ReadOnlyMemory<byte> Reply)
{
    public static RemoteAuthStep Continue(ReadOnlyMemory<byte> reply) => new(false, false, reply);
    public static RemoteAuthStep Unlock(ReadOnlyMemory<byte> reply = default) => new(true, false, reply);
    public static readonly RemoteAuthStep Rejected = new(false, true, default);
}

/// <summary>
/// Verifies the operator's session password end-to-end at the radio (ADR-0008).
/// The real PAKE/SPAKE2 verifier lands in Phase 4; until then
/// <see cref="DenyAllAuthGate"/> is the default so the deny-by-default invariant
/// holds the moment the transport exists.
/// </summary>
public interface IRemoteAuthGate
{
    /// <summary>Process one inbound auth message; may reply, unlock, or reject.</summary>
    ValueTask<RemoteAuthStep> StepAsync(ReadOnlyMemory<byte> clientMessage, CancellationToken ct = default);
}

/// <summary>
/// Phase-1 default until Phase 4 wires the password verifier: rejects every
/// attempt, so a remote session can never leave <see cref="RemoteSessionState.Locked"/>.
/// "Nothing without the password" is the as-built behaviour, not a TODO.
/// </summary>
public sealed class DenyAllAuthGate : IRemoteAuthGate
{
    public static readonly DenyAllAuthGate Instance = new();

    public ValueTask<RemoteAuthStep> StepAsync(ReadOnlyMemory<byte> clientMessage, CancellationToken ct = default)
        => ValueTask.FromResult(RemoteAuthStep.Rejected);
}

/// <summary>Action the transport must take after an auth submission.</summary>
public enum RemoteSessionAction
{
    /// <summary>Send <see cref="RemoteSessionOutcome.Reply"/>; session stays LOCKED.</summary>
    Reply,
    /// <summary>Send the reply (if any), then arm the radio data paths.</summary>
    Unlock,
    /// <summary>Send the reply (if any), then tear the session down. Leak nothing.</summary>
    Close,
}

public readonly record struct RemoteSessionOutcome(RemoteSessionAction Action, ReadOnlyMemory<byte> Reply);

/// <summary>
/// Enforces the ADR-0008 hard invariant for one remote session. Begins
/// <see cref="RemoteSessionState.Locked"/>; the only thing permitted while locked
/// is the password proof. Data-plane egress and control dispatch are refused
/// until <see cref="RemoteSessionState.Unlocked"/>. Any auth rejection, exception,
/// or out-of-order message closes the session without revealing radio state.
///
/// This type is deliberately transport-agnostic — Phase 1's WebRTC transport (and
/// any future transport) holds one and consults the guards below before doing
/// anything.
/// </summary>
public sealed class RemoteSession
{
    private readonly IRemoteAuthGate _gate;

    public RemoteSession(IRemoteAuthGate gate)
        => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public RemoteSessionState State { get; private set; } = RemoteSessionState.Locked;

    /// <summary>True only once authenticated.</summary>
    public bool IsUnlocked => State == RemoteSessionState.Unlocked;

    /// <summary>
    /// Guard the transport MUST call before egressing any data-plane frame
    /// (audio / display / IQ / meters). Returns false while LOCKED or Closed.
    /// Never throws.
    /// </summary>
    public bool TryEgress() => State == RemoteSessionState.Unlocked;

    /// <summary>
    /// Guard for inbound control verbs (VFO / mode / TX / …). Refused unless
    /// UNLOCKED. Never throws.
    /// </summary>
    public bool TryDispatchControl() => State == RemoteSessionState.Unlocked;

    /// <summary>
    /// Feed an inbound auth-channel message — the only thing the client may send
    /// while LOCKED. Drives the gate and returns what the transport should do.
    /// </summary>
    public async ValueTask<RemoteSessionOutcome> SubmitAuthAsync(
        ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        if (State != RemoteSessionState.Locked)
        {
            // Auth traffic after unlock (or on a closed session) is protocol abuse.
            State = RemoteSessionState.Closed;
            return new RemoteSessionOutcome(RemoteSessionAction.Close, default);
        }

        RemoteAuthStep step;
        try
        {
            step = await _gate.StepAsync(message, ct).ConfigureAwait(false);
        }
        catch
        {
            State = RemoteSessionState.Closed;
            return new RemoteSessionOutcome(RemoteSessionAction.Close, default);
        }

        if (step.Reject)
        {
            State = RemoteSessionState.Closed;
            return new RemoteSessionOutcome(RemoteSessionAction.Close, step.Reply);
        }

        if (step.Unlocked)
        {
            State = RemoteSessionState.Unlocked;
            return new RemoteSessionOutcome(RemoteSessionAction.Unlock, step.Reply);
        }

        return new RemoteSessionOutcome(RemoteSessionAction.Reply, step.Reply);
    }

    public void Close() => State = RemoteSessionState.Closed;
}
