using System.Text;
using Org.BouncyCastle.Math;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// End-to-end test of the remote-access password gate: a SPAKE2+ prover (the
/// browser's role) drives a <see cref="RemoteSession"/> backed by
/// <see cref="Spake2PlusAuthGate"/>. Proves the ADR-0008 invariant holds in
/// practice — the right password unlocks and arms the data path; the wrong
/// password leaves the session LOCKED and closed.
/// </summary>
public sealed class Spake2PlusAuthGateTests
{
    private static readonly byte[] Context = Encoding.ASCII.GetBytes("zeus-remote-access/v1");
    private static readonly byte[] IdProver = Encoding.ASCII.GetBytes("client");
    private static readonly byte[] IdVerifier = Encoding.ASCII.GetBytes("server");

    // Reuse the RFC 9383 verifier scalars as a stand-in for "the registered
    // password" (Argon2id registration that produces these lands with the UI).
    private const string W0 = "bb8e1bbcf3c48f62c08db243652ae55d3e5586053fca77102994f23ad95491b3";
    private const string W1 = "7e945f34d78785b8a3ef44d0df5a1a97d6b3b460409a345ca7830387a74b1dba";
    private const string L = "04eb7c9db3d9a9eb1f8adab81b5794c1f13ae3e225efbe91ea487425854c7fc00f00bfedcbd09b2400142d40a14f2064ef31dfaa903b91d1faea7093d835966efd";

    private static Spake2Plus Prover() => new(Spake2Role.Prover, Context, IdProver, IdVerifier);

    private static Spake2PlusAuthGate Gate(BigInteger w0) =>
        new(Context, IdProver, IdVerifier, w0, Spake2Plus.DecodeL(Convert.FromHexString(L)));

    [Fact]
    public async Task CorrectPassword_UnlocksSession_AndArmsDataPath()
    {
        var w0 = Spake2Plus.ScalarFromHex(W0);
        var gate = Gate(w0);
        var session = new RemoteSession(gate);
        var prover = Prover();

        // Locked at the start: nothing is permitted.
        Assert.False(session.TryEgress());

        // step 0: client → shareP, server → shareV (still locked).
        var shareP = prover.StartProver(w0, Spake2Plus.ScalarFromHex(W1));
        var r0 = await session.SubmitAuthAsync(shareP);
        Assert.Equal(RemoteSessionAction.Reply, r0.Action);
        Assert.False(session.IsUnlocked);
        Assert.False(session.TryEgress());

        // client completes SPAKE2+ from the server's shareV.
        var proverOutcome = prover.Process(r0.Reply.ToArray());

        // step 1: client → confirmP, server verifies → UNLOCK + confirmV.
        var r1 = await session.SubmitAuthAsync(proverOutcome.LocalConfirm);
        Assert.Equal(RemoteSessionAction.Unlock, r1.Action);
        Assert.True(session.IsUnlocked);
        Assert.True(session.TryEgress());          // audio/display/IQ now allowed
        Assert.True(session.TryDispatchControl());  // VFO/mode now allowed

        // Mutual auth: client accepts the server's confirmV, and both agree on the key.
        Assert.True(Spake2Plus.VerifyPeerConfirm(proverOutcome, r1.Reply.ToArray()));
        Assert.Equal(
            Convert.ToHexString(proverOutcome.SharedKey),
            Convert.ToHexString(gate.SessionKey!));
    }

    [Fact]
    public async Task WrongPassword_StaysLocked_AndCloses()
    {
        var clientW0 = Spake2Plus.ScalarFromHex(W0);
        // Server registered a DIFFERENT password verifier.
        var serverW0 = clientW0.Add(BigInteger.One);

        var session = new RemoteSession(Gate(serverW0));
        var prover = Prover();

        var shareP = prover.StartProver(clientW0, Spake2Plus.ScalarFromHex(W1));
        var r0 = await session.SubmitAuthAsync(shareP);
        Assert.Equal(RemoteSessionAction.Reply, r0.Action); // share exchange always replies

        var proverOutcome = prover.Process(r0.Reply.ToArray());
        var r1 = await session.SubmitAuthAsync(proverOutcome.LocalConfirm);

        // Wrong password → confirm mismatch → session closed, nothing armed.
        Assert.Equal(RemoteSessionAction.Close, r1.Action);
        Assert.Equal(RemoteSessionState.Closed, session.State);
        Assert.False(session.IsUnlocked);
        Assert.False(session.TryEgress());
    }

    [Fact]
    public async Task GarbageFirstMessage_FailsClosed()
    {
        var session = new RemoteSession(Gate(Spake2Plus.ScalarFromHex(W0)));

        var r = await session.SubmitAuthAsync(new byte[] { 0xde, 0xad });

        Assert.Equal(RemoteSessionAction.Close, r.Action);
        Assert.False(session.TryEgress());
    }
}
