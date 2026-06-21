using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities.Encoders;
// Alias the BouncyCastle EC types — System.Security.Cryptography also defines
// ECPoint/ECCurve, and we need that namespace for SHA256/HKDF/HMAC.
using ECCurve = Org.BouncyCastle.Math.EC.ECCurve;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Zeus.Server.Hosting.Remote;

/// <summary>Which half of the SPAKE2+ exchange this instance plays.</summary>
public enum Spake2Role
{
    /// <summary>The client that knows the password (holds w0 + w1).</summary>
    Prover,
    /// <summary>The radio that stored the verifier (holds w0 + L). This is Zeus.Server.</summary>
    Verifier,
}

/// <summary>Thrown on any SPAKE2+ protocol/validation failure. Fails closed — leaks nothing.</summary>
public sealed class Spake2Exception(string message) : Exception(message);

/// <summary>
/// Outcome of a completed SPAKE2+ exchange. <see cref="SharedKey"/> is the
/// authenticated session key (K_shared / Ke); <see cref="LocalConfirm"/> is the
/// MAC this side sends, and <see cref="ExpectedPeerConfirm"/> is the MAC it must
/// receive (constant-time compared) before trusting the key.
/// </summary>
public sealed class Spake2PlusOutcome
{
    public required byte[] SharedKey { get; init; }
    public required byte[] LocalConfirm { get; init; }
    public required byte[] ExpectedPeerConfirm { get; init; }

    // Intermediates exposed only to tests (InternalsVisibleTo) for RFC 9383
    // Appendix C vector verification.
    internal byte[] Transcript { get; init; } = [];
    internal byte[] KMain { get; init; } = [];
    internal byte[] KConfirmP { get; init; } = [];
    internal byte[] KConfirmV { get; init; } = [];
    internal byte[] ConfirmP { get; init; } = [];
    internal byte[] ConfirmV { get; init; } = [];
    internal byte[] ShareP { get; init; } = [];
    internal byte[] ShareV { get; init; } = [];
    internal byte[] ZEnc { get; init; } = [];
    internal byte[] VEnc { get; init; } = [];
}

/// <summary>
/// SPAKE2+ (RFC 9383), ciphersuite P256-SHA256-HKDF-SHA256-HMAC-SHA256. The
/// augmented PAKE behind the remote-access session password (ADR-0008): the
/// server stores only the verifier (w0, L = w1·P); the client proves knowledge
/// of the password without ever sending it, and a malicious broker cannot MITM.
///
/// EC point math uses BouncyCastle (already in the dependency graph via
/// SIPSorcery); the KDF/MAC/hash use the BCL. Correctness is pinned to the RFC
/// 9383 Appendix C test vectors in <c>Spake2PlusTests</c>.
/// </summary>
public sealed class Spake2Plus
{
    private static readonly X9ECParameters Params = SecNamedCurves.GetByName("secp256r1");
    private static readonly ECCurve Curve = Params.Curve;
    private static readonly ECPoint G = Params.G;
    private static readonly BigInteger Order = Params.N;

    // RFC 9382/9383 constant points (compressed SEC1); discrete log unknown.
    private static readonly ECPoint M = Curve.DecodePoint(
        Hex.Decode("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
    private static readonly ECPoint N = Curve.DecodePoint(
        Hex.Decode("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    private readonly Spake2Role _role;
    private readonly byte[] _context;
    private readonly byte[] _idProver;
    private readonly byte[] _idVerifier;

    private BigInteger? _w0;
    private BigInteger? _w1;   // prover only
    private ECPoint? _l;       // verifier only
    private BigInteger? _scalar; // x (prover) or y (verifier)
    private byte[]? _share;

    public Spake2Plus(Spake2Role role, byte[] context, byte[] idProver, byte[] idVerifier)
    {
        _role = role;
        _context = context;
        _idProver = idProver;
        _idVerifier = idVerifier;
    }

    /// <summary>Decode an uncompressed-SEC1 registration record L into a point.</summary>
    public static ECPoint DecodeL(byte[] encoded) => Validated(Curve.DecodePoint(encoded));

    /// <summary>
    /// Prover (client) first message: shareP = x·P + w0·M. <paramref name="x"/>
    /// is for test vectors only; production passes null for a random scalar.
    /// </summary>
    public byte[] StartProver(BigInteger w0, BigInteger w1, BigInteger? x = null)
    {
        if (_role != Spake2Role.Prover) throw new Spake2Exception("role mismatch");
        _w0 = w0;
        _w1 = w1;
        _scalar = x ?? RandomScalar();
        _share = G.Multiply(_scalar).Add(M.Multiply(w0)).Normalize().GetEncoded(false);
        return _share;
    }

    /// <summary>
    /// Verifier (radio) first message: shareV = y·P + w0·N. <paramref name="y"/>
    /// is for test vectors only; production passes null for a random scalar.
    /// </summary>
    public byte[] StartVerifier(BigInteger w0, ECPoint l, BigInteger? y = null)
    {
        if (_role != Spake2Role.Verifier) throw new Spake2Exception("role mismatch");
        _w0 = w0;
        _l = l;
        _scalar = y ?? RandomScalar();
        _share = G.Multiply(_scalar).Add(N.Multiply(w0)).Normalize().GetEncoded(false);
        return _share;
    }

    /// <summary>
    /// Process the peer's share, derive Z/V, build the transcript, and produce the
    /// session key + confirmation MACs. Throws (fails closed) on any invalid input.
    /// </summary>
    public Spake2PlusOutcome Process(byte[] peerShare)
    {
        if (_w0 is null || _scalar is null || _share is null)
            throw new Spake2Exception("Start* not called");

        var peer = Validated(Curve.DecodePoint(peerShare));

        ECPoint z, v;
        byte[] shareP, shareV;
        if (_role == Spake2Role.Prover)
        {
            // peer = shareV (Y). Z = x·(Y − w0·N); V = w1·(Y − w0·N).
            var t = Validated(peer.Subtract(N.Multiply(_w0)));
            z = t.Multiply(_scalar).Normalize();
            v = t.Multiply(_w1!).Normalize();
            shareP = _share;
            shareV = peer.GetEncoded(false);
        }
        else
        {
            // peer = shareP (X). Z = y·(X − w0·M); V = y·L.
            var t = Validated(peer.Subtract(M.Multiply(_w0)));
            z = t.Multiply(_scalar).Normalize();
            v = _l!.Multiply(_scalar).Normalize();
            shareP = peer.GetEncoded(false);
            shareV = _share;
        }
        if (z.IsInfinity || v.IsInfinity)
            throw new Spake2Exception("degenerate shared point");

        var zEnc = z.GetEncoded(false);
        var vEnc = v.GetEncoded(false);

        var tt = BuildTranscript(shareP, shareV, zEnc, vEnc, _w0!);
        var kMain = SHA256.HashData(tt);

        var confirmKeys = HKDF.DeriveKey(HashAlgorithmName.SHA256, kMain, 64,
            salt: null, info: Encoding.ASCII.GetBytes("ConfirmationKeys"));
        var kConfirmP = confirmKeys[..32];
        var kConfirmV = confirmKeys[32..];
        var kShared = HKDF.DeriveKey(HashAlgorithmName.SHA256, kMain, 32,
            salt: null, info: Encoding.ASCII.GetBytes("SharedKey"));

        // RFC 9383 §3.4: confirmP = MAC(K_confirmP, shareV); confirmV = MAC(K_confirmV, shareP).
        var confirmP = HMACSHA256.HashData(kConfirmP, shareV);
        var confirmV = HMACSHA256.HashData(kConfirmV, shareP);

        var (local, expected) = _role == Spake2Role.Prover
            ? (confirmP, confirmV)
            : (confirmV, confirmP);

        return new Spake2PlusOutcome
        {
            SharedKey = kShared,
            LocalConfirm = local,
            ExpectedPeerConfirm = expected,
            Transcript = tt,
            KMain = kMain,
            KConfirmP = kConfirmP,
            KConfirmV = kConfirmV,
            ConfirmP = confirmP,
            ConfirmV = confirmV,
            ShareP = shareP,
            ShareV = shareV,
            ZEnc = zEnc,
            VEnc = vEnc,
        };
    }

    /// <summary>Constant-time check of the peer's confirmation MAC.</summary>
    public static bool VerifyPeerConfirm(Spake2PlusOutcome outcome, byte[] received)
        => CryptographicOperations.FixedTimeEquals(outcome.ExpectedPeerConfirm, received);

    // TT = len(Context)||Context || len(idProver)||idProver || len(idVerifier)||idVerifier
    //   || len(M)||M || len(N)||N || len(shareP)||shareP || len(shareV)||shareV
    //   || len(Z)||Z || len(V)||V || len(w0)||w0     (RFC 9383 §3.3)
    private byte[] BuildTranscript(byte[] shareP, byte[] shareV, byte[] zEnc, byte[] vEnc, BigInteger w0)
    {
        using var ms = new MemoryStream();
        AppendLenPrefixed(ms, _context);
        AppendLenPrefixed(ms, _idProver);
        AppendLenPrefixed(ms, _idVerifier);
        AppendLenPrefixed(ms, M.GetEncoded(false));
        AppendLenPrefixed(ms, N.GetEncoded(false));
        AppendLenPrefixed(ms, shareP);
        AppendLenPrefixed(ms, shareV);
        AppendLenPrefixed(ms, zEnc);
        AppendLenPrefixed(ms, vEnc);
        AppendLenPrefixed(ms, FixedScalar(w0)); // 32-byte big-endian
        return ms.ToArray();
    }

    private static void AppendLenPrefixed(Stream s, byte[] value)
    {
        Span<byte> len = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)value.Length);
        s.Write(len);
        s.Write(value);
    }

    /// <summary>32-byte big-endian encoding of a scalar (P-256 field width).</summary>
    private static byte[] FixedScalar(BigInteger w)
    {
        var raw = w.ToByteArrayUnsigned(); // big-endian, minimal
        if (raw.Length == 32) return raw;
        if (raw.Length > 32) throw new Spake2Exception("scalar too large");
        var padded = new byte[32];
        Array.Copy(raw, 0, padded, 32 - raw.Length, raw.Length);
        return padded;
    }

    private static ECPoint Validated(ECPoint p)
    {
        var n = p.Normalize();
        if (n.IsInfinity || !n.IsValid())
            throw new Spake2Exception("invalid EC point");
        return n;
    }

    private static BigInteger RandomScalar()
    {
        // Sample uniformly in [1, n). Oversample then reduce to avoid modulo bias.
        Span<byte> buf = stackalloc byte[48]; // > order width
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            var candidate = new BigInteger(1, buf.ToArray()).Mod(Order);
            if (candidate.SignValue > 0)
                return candidate;
        }
    }

    /// <summary>Parse a hex scalar (test/registration helper).</summary>
    public static BigInteger ScalarFromHex(string hex) => new(hex, 16);

    // --- Registration helpers (SPAKE2+ verifier derivation) -----------------

    /// <summary>Reduce a wide (bias-avoiding) byte string to a scalar mod n.</summary>
    public static BigInteger ReduceToScalar(ReadOnlySpan<byte> wide)
        => new BigInteger(1, wide.ToArray()).Mod(Order);

    /// <summary>Reconstruct a stored scalar (e.g. w0) from its 32-byte encoding.</summary>
    public static BigInteger ScalarFromBytes(ReadOnlySpan<byte> bytes)
        => new BigInteger(1, bytes.ToArray()).Mod(Order);

    /// <summary>32-byte big-endian encoding of a scalar (storage helper).</summary>
    public static byte[] EncodeScalar(BigInteger w) => FixedScalar(w);

    /// <summary>The SPAKE2+ registration record L = w1·P, uncompressed-SEC1 encoded.</summary>
    public static byte[] EncodeL(BigInteger w1) => G.Multiply(w1).Normalize().GetEncoded(false);
}
