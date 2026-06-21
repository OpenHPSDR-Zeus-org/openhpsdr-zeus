using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Proves the full remote-access password flow: a typed password is registered
/// into a SPAKE2+ verifier (Argon2id), and a client that re-derives from the
/// same password + stored salt unlocks the session — while a wrong password
/// does not. Light Argon2 params keep the tests fast; production uses 64 MiB.
/// </summary>
public sealed class Spake2PlusRegistrationTests
{
    private const int Iter = 1;
    private const int MemKib = 8192; // 8 MiB for tests
    private const int Par = 1;

    private static Spake2Plus Prover() => new(
        Spake2Role.Prover, RemoteAuthConstants.Context,
        RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier);

    private static Spake2PlusAuthGate GateFor(Spake2PlusVerifier v) => new(
        RemoteAuthConstants.Context, RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier,
        Spake2Plus.ScalarFromBytes(v.W0), Spake2Plus.DecodeL(v.L));

    [Fact]
    public void DeriveScalars_IsDeterministic()
    {
        var salt = new byte[16];
        var a = Spake2PlusRegistration.DeriveScalars("pw", salt, Iter, MemKib, Par);
        var b = Spake2PlusRegistration.DeriveScalars("pw", salt, Iter, MemKib, Par);
        Assert.Equal(a.W0, b.W0);
        Assert.Equal(a.W1, b.W1);
    }

    [Fact]
    public void Register_StoresVerifierMatchingRederivation()
    {
        var v = Spake2PlusRegistration.Register("pw", Iter, MemKib, Par);
        var (w0, w1) = Spake2PlusRegistration.DeriveScalars("pw", v.Salt, v.Iterations, v.MemoryKib, v.Parallelism);

        Assert.Equal(v.W0, Spake2Plus.EncodeScalar(w0));
        Assert.Equal(v.L, Spake2Plus.EncodeL(w1));
    }

    [Fact]
    public async Task FullFlow_CorrectPassword_Unlocks()
    {
        const string password = "correct horse battery staple";
        var v = Spake2PlusRegistration.Register(password, Iter, MemKib, Par);

        var session = new RemoteSession(GateFor(v));
        var (cw0, cw1) = Spake2PlusRegistration.DeriveScalars(password, v.Salt, v.Iterations, v.MemoryKib, v.Parallelism);

        var prover = Prover();
        var shareP = prover.StartProver(cw0, cw1);
        var r0 = await session.SubmitAuthAsync(shareP);
        var proverOutcome = prover.Process(r0.Reply.ToArray());
        var r1 = await session.SubmitAuthAsync(proverOutcome.LocalConfirm);

        Assert.Equal(RemoteSessionAction.Unlock, r1.Action);
        Assert.True(session.IsUnlocked);
        Assert.True(Spake2Plus.VerifyPeerConfirm(proverOutcome, r1.Reply.ToArray()));
    }

    [Fact]
    public async Task FullFlow_WrongPassword_StaysLocked()
    {
        var v = Spake2PlusRegistration.Register("the real password", Iter, MemKib, Par);

        var session = new RemoteSession(GateFor(v));
        var (cw0, cw1) = Spake2PlusRegistration.DeriveScalars("a different password", v.Salt, v.Iterations, v.MemoryKib, v.Parallelism);

        var prover = Prover();
        var shareP = prover.StartProver(cw0, cw1);
        var r0 = await session.SubmitAuthAsync(shareP);
        var proverOutcome = prover.Process(r0.Reply.ToArray());
        var r1 = await session.SubmitAuthAsync(proverOutcome.LocalConfirm);

        Assert.Equal(RemoteSessionAction.Close, r1.Action);
        Assert.False(session.IsUnlocked);
    }
}
