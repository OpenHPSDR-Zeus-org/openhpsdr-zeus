using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// The real <see cref="IRemoteAuthGate"/> (ADR-0008): runs the SPAKE2+ verifier
/// against the operator's stored password verifier (w0, L). Replaces
/// <see cref="DenyAllAuthGate"/> once a password is set. The gate is the SERVER
/// (verifier) half; the browser is the prover.
///
/// Two client messages drive it, strictly in order:
///   step 0: client → shareP (X). Gate replies shareV (Y), stays LOCKED.
///   step 1: client → confirmP.   Gate verifies (constant-time); on success it
///           replies confirmV and UNLOCKS, else it rejects (fails closed).
/// After unlock, <see cref="SessionKey"/> is the authenticated shared key, usable
/// to bind/derive the transport keys.
/// </summary>
public sealed class Spake2PlusAuthGate : IRemoteAuthGate
{
    private readonly Spake2Plus _spake;
    private readonly BigInteger _w0;
    private readonly ECPoint _l;

    private int _step;
    private Spake2PlusOutcome? _outcome;

    public Spake2PlusAuthGate(byte[] context, byte[] idProver, byte[] idVerifier, BigInteger w0, ECPoint l)
    {
        _spake = new Spake2Plus(Spake2Role.Verifier, context, idProver, idVerifier);
        _w0 = w0;
        _l = l;
    }

    /// <summary>The authenticated session key, available only after a successful unlock.</summary>
    public byte[]? SessionKey => _step >= 2 ? _outcome?.SharedKey : null;

    public ValueTask<RemoteAuthStep> StepAsync(ReadOnlyMemory<byte> clientMessage, CancellationToken ct = default)
    {
        try
        {
            switch (_step)
            {
                case 0:
                {
                    // client message = shareP (X). Compute our share + the full outcome.
                    var shareV = _spake.StartVerifier(_w0, _l); // fresh random y per session
                    _outcome = _spake.Process(clientMessage.ToArray());
                    _step = 1;
                    return new ValueTask<RemoteAuthStep>(RemoteAuthStep.Continue(shareV));
                }
                case 1:
                {
                    // client message = confirmP. Verify in constant time.
                    if (_outcome is null
                        || !Spake2Plus.VerifyPeerConfirm(_outcome, clientMessage.ToArray()))
                        return new ValueTask<RemoteAuthStep>(RemoteAuthStep.Rejected);

                    _step = 2;
                    return new ValueTask<RemoteAuthStep>(RemoteAuthStep.Unlock(_outcome.LocalConfirm));
                }
                default:
                    return new ValueTask<RemoteAuthStep>(RemoteAuthStep.Rejected);
            }
        }
        catch
        {
            // Any malformed share / invalid point / protocol misuse fails closed.
            return new ValueTask<RemoteAuthStep>(RemoteAuthStep.Rejected);
        }
    }
}
