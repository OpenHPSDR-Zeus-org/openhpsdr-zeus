using System.Text;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the SPAKE2+ implementation to RFC 9383 Appendix C, Vector 1
/// (ciphersuite P256-SHA256-HKDF-SHA256-HMAC-SHA256). Every intermediate —
/// shares, Z, V, transcript-derived keys, and both confirmation MACs — is checked
/// byte-for-byte against the RFC. Because this crypto gates a transmitter, "it
/// runs" is not enough; it must match the standard exactly.
/// </summary>
public sealed class Spake2PlusTests
{
    // RFC 9383 Appendix C, Vector 1.
    // RFC 9383 Appendix C context (56 bytes, no trailing space — the TT hex shows
    // "...Vectors" immediately followed by the next field's length prefix).
    private static readonly byte[] Context =
        Encoding.ASCII.GetBytes("SPAKE2+-P256-SHA256-HKDF-SHA256-HMAC-SHA256 Test Vectors");
    private static readonly byte[] IdProver = Encoding.ASCII.GetBytes("client");
    private static readonly byte[] IdVerifier = Encoding.ASCII.GetBytes("server");

    private const string W0 = "bb8e1bbcf3c48f62c08db243652ae55d3e5586053fca77102994f23ad95491b3";
    private const string W1 = "7e945f34d78785b8a3ef44d0df5a1a97d6b3b460409a345ca7830387a74b1dba";
    private const string L = "04eb7c9db3d9a9eb1f8adab81b5794c1f13ae3e225efbe91ea487425854c7fc00f00bfedcbd09b2400142d40a14f2064ef31dfaa903b91d1faea7093d835966efd";
    private const string X = "d1232c8e8693d02368976c174e2088851b8365d0d79a9eee709c6a05a2fad539";
    private const string Y = "717a72348a182085109c8d3917d6c43d59b224dc6a7fc4f0483232fa6516d8b3";
    private const string ShareP = "04ef3bd051bf78a2234ec0df197f7828060fe9856503579bb1733009042c15c0c1de127727f418b5966afadfdd95a6e4591d171056b333dab97a79c7193e341727";
    private const string ShareV = "04c0f65da0d11927bdf5d560c69e1d7d939a05b0e88291887d679fcadea75810fb5cc1ca7494db39e82ff2f50665255d76173e09986ab46742c798a9a68437b048";
    private const string Z = "04bbfce7dd7f277819c8da21544afb7964705569bdf12fb92aa388059408d50091a0c5f1d3127f56813b5337f9e4e67e2ca633117a4fbd559946ab474356c41839";
    private const string V = "0458bf27c6bca011c9ce1930e8984a797a3419797b936629a5a937cf2f11c8b9514b82b993da8a46e664f23db7c01edc87faa530db01c2ee405230b18997f16b68";
    private const string KMain = "4c59e1ccf2cfb961aa31bd9434478a1089b56cd11542f53d3576fb6c2a438a29";
    private const string KConfirmP = "871ae3f7b78445e34438fb284504240239031c39d80ac23eb5ab9be5ad6db58a";
    private const string KConfirmV = "ccd53c7c1fa37b64a462b40db8be101cedcf838950162902054e644b400f1680";
    private const string KShared = "0c5f8ccd1413423a54f6c1fb26ff01534a87f893779c6e68666d772bfd91f3e7";
    private const string ConfirmP = "926cc713504b9b4d76c9162ded04b5493e89109f6d89462cd33adc46fda27527";
    private const string ConfirmV = "9747bcc4f8fe9f63defee53ac9b07876d907d55047e6ff2def2e7529089d3e68";

    private static Spake2Plus Prover() => new(Spake2Role.Prover, Context, IdProver, IdVerifier);
    private static Spake2Plus Verifier() => new(Spake2Role.Verifier, Context, IdProver, IdVerifier);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void ProverShare_MatchesRfc9383Vector()
    {
        var shareP = Prover().StartProver(
            Spake2Plus.ScalarFromHex(W0), Spake2Plus.ScalarFromHex(W1), Spake2Plus.ScalarFromHex(X));
        Assert.Equal(ShareP, Hex(shareP));
    }

    [Fact]
    public void VerifierShare_MatchesRfc9383Vector()
    {
        var shareV = Verifier().StartVerifier(
            Spake2Plus.ScalarFromHex(W0), Spake2Plus.DecodeL(Convert.FromHexString(L)),
            Spake2Plus.ScalarFromHex(Y));
        Assert.Equal(ShareV, Hex(shareV));
    }

    [Fact]
    public void ProverProcess_DerivesEveryIntermediate_PerRfc9383()
    {
        var prover = Prover();
        prover.StartProver(Spake2Plus.ScalarFromHex(W0), Spake2Plus.ScalarFromHex(W1), Spake2Plus.ScalarFromHex(X));
        var o = prover.Process(Convert.FromHexString(ShareV));

        Assert.Equal(Z, Hex(o.ZEnc));
        Assert.Equal(V, Hex(o.VEnc));
        Assert.Equal(KMain, Hex(o.KMain));
        Assert.Equal(KConfirmP, Hex(o.KConfirmP));
        Assert.Equal(KConfirmV, Hex(o.KConfirmV));
        Assert.Equal(KShared, Hex(o.SharedKey));
        Assert.Equal(ConfirmP, Hex(o.ConfirmP));
        Assert.Equal(ConfirmV, Hex(o.ConfirmV));
        // Prover sends confirmP, expects confirmV.
        Assert.Equal(ConfirmP, Hex(o.LocalConfirm));
        Assert.Equal(ConfirmV, Hex(o.ExpectedPeerConfirm));
    }

    [Fact]
    public void VerifierProcess_DerivesEveryIntermediate_PerRfc9383()
    {
        var verifier = Verifier();
        verifier.StartVerifier(Spake2Plus.ScalarFromHex(W0), Spake2Plus.DecodeL(Convert.FromHexString(L)), Spake2Plus.ScalarFromHex(Y));
        var o = verifier.Process(Convert.FromHexString(ShareP));

        Assert.Equal(Z, Hex(o.ZEnc));
        Assert.Equal(V, Hex(o.VEnc));
        Assert.Equal(KShared, Hex(o.SharedKey));
        // Verifier sends confirmV, expects confirmP.
        Assert.Equal(ConfirmV, Hex(o.LocalConfirm));
        Assert.Equal(ConfirmP, Hex(o.ExpectedPeerConfirm));
    }

    [Fact]
    public void FullExchange_BothSidesAgree_AndConfirmsVerify()
    {
        var w0 = Spake2Plus.ScalarFromHex(W0);
        var w1 = Spake2Plus.ScalarFromHex(W1);
        var l = Spake2Plus.DecodeL(Convert.FromHexString(L));

        var prover = Prover();
        var verifier = Verifier();
        var sp = prover.StartProver(w0, w1, Spake2Plus.ScalarFromHex(X));
        var sv = verifier.StartVerifier(w0, l, Spake2Plus.ScalarFromHex(Y));

        var po = prover.Process(sv);
        var vo = verifier.Process(sp);

        Assert.Equal(Hex(po.SharedKey), Hex(vo.SharedKey));
        Assert.True(Spake2Plus.VerifyPeerConfirm(po, vo.LocalConfirm)); // prover accepts verifier's MAC
        Assert.True(Spake2Plus.VerifyPeerConfirm(vo, po.LocalConfirm)); // verifier accepts prover's MAC
    }

    [Fact]
    public void RandomScalars_CorrectPassword_Succeeds()
    {
        var w0 = Spake2Plus.ScalarFromHex(W0);
        var w1 = Spake2Plus.ScalarFromHex(W1);
        var l = Spake2Plus.DecodeL(Convert.FromHexString(L));

        var prover = Prover();
        var verifier = Verifier();
        var sp = prover.StartProver(w0, w1);   // random x
        var sv = verifier.StartVerifier(w0, l); // random y

        var po = prover.Process(sv);
        var vo = verifier.Process(sp);

        Assert.Equal(Hex(po.SharedKey), Hex(vo.SharedKey));
        Assert.True(Spake2Plus.VerifyPeerConfirm(po, vo.LocalConfirm));
        Assert.True(Spake2Plus.VerifyPeerConfirm(vo, po.LocalConfirm));
    }

    [Fact]
    public void WrongPassword_ConfirmsDoNotVerify()
    {
        var w0 = Spake2Plus.ScalarFromHex(W0);
        var w1 = Spake2Plus.ScalarFromHex(W1);
        var l = Spake2Plus.DecodeL(Convert.FromHexString(L));
        var wrongW0 = w0.Add(Org.BouncyCastle.Math.BigInteger.One); // verifier's stored secret differs

        var prover = Prover();
        var verifier = Verifier();
        var sp = prover.StartProver(w0, w1);
        var sv = verifier.StartVerifier(wrongW0, l);

        var po = prover.Process(sv);
        var vo = verifier.Process(sp);

        Assert.NotEqual(Hex(po.SharedKey), Hex(vo.SharedKey));
        Assert.False(Spake2Plus.VerifyPeerConfirm(po, vo.LocalConfirm));
        Assert.False(Spake2Plus.VerifyPeerConfirm(vo, po.LocalConfirm));
    }

    [Fact]
    public void InvalidPeerShare_FailsClosed()
    {
        var verifier = Verifier();
        verifier.StartVerifier(Spake2Plus.ScalarFromHex(W0), Spake2Plus.DecodeL(Convert.FromHexString(L)));
        // Identity / garbage share must be rejected, not processed.
        Assert.Throws<Spake2Exception>(() => verifier.Process(new byte[] { 0x00 }));
    }
}
