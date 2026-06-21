using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Org.BouncyCastle.Math;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// App-wide SPAKE2+ parameters. The browser prover MUST use byte-identical
/// values, so they live in one place and are mirrored in the TS client.
/// </summary>
public static class RemoteAuthConstants
{
    public static byte[] Context => Encoding.UTF8.GetBytes("zeus-remote-access/v1");
    public static byte[] IdProver => [];   // empty identities; Context binds the app
    public static byte[] IdVerifier => [];

    public const int DefaultIterations = 3;
    public const int DefaultMemoryKib = 65536;   // 64 MiB — memory-hard
    public const int DefaultParallelism = 1;
    public const int SaltBytes = 16;
    private const int ScalarWideBytes = 48;       // 384 bits → reduce mod n, low bias
    public const int Argon2OutputBytes = 2 * ScalarWideBytes;
}

/// <summary>
/// The stored SPAKE2+ registration record. Contains no password and no w1 — only
/// the verifier (w0, L = w1·P) plus the salt and Argon2id parameters needed to
/// re-derive on the client at login.
/// </summary>
public sealed record Spake2PlusVerifier(
    byte[] Salt, byte[] W0, byte[] L, int Iterations, int MemoryKib, int Parallelism);

/// <summary>
/// SPAKE2+ password registration (ADR-0008): Argon2id over the password yields
/// the scalars (w0, w1); the server keeps only (w0, L = w1·P). Deterministic —
/// the browser runs the identical Argon2id over the same salt+params to prove
/// knowledge without ever sending the password.
/// </summary>
public static class Spake2PlusRegistration
{
    /// <summary>Derive (w0, w1) from password + salt. Mirrored by the TS client.</summary>
    public static (BigInteger W0, BigInteger W1) DeriveScalars(
        string password, byte[] salt, int iterations, int memoryKib, int parallelism)
    {
        var input = BuildPasswordInput(password);
        try
        {
            using var argon = new Argon2id(input)
            {
                Salt = salt,
                Iterations = iterations,
                MemorySize = memoryKib,
                DegreeOfParallelism = parallelism,
            };
            var wide = argon.GetBytes(RemoteAuthConstants.Argon2OutputBytes);
            try
            {
                var half = wide.Length / 2;
                var w0 = Spake2Plus.ReduceToScalar(wide.AsSpan(0, half));
                var w1 = Spake2Plus.ReduceToScalar(wide.AsSpan(half));
                return (w0, w1);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wide);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>Register a new password → stored verifier. Never returns w1 or the password.</summary>
    public static Spake2PlusVerifier Register(
        string password,
        int iterations = RemoteAuthConstants.DefaultIterations,
        int memoryKib = RemoteAuthConstants.DefaultMemoryKib,
        int parallelism = RemoteAuthConstants.DefaultParallelism)
    {
        var salt = RandomNumberGenerator.GetBytes(RemoteAuthConstants.SaltBytes);
        var (w0, w1) = DeriveScalars(password, salt, iterations, memoryKib, parallelism);
        return new Spake2PlusVerifier(
            salt,
            Spake2Plus.EncodeScalar(w0),
            Spake2Plus.EncodeL(w1),
            iterations, memoryKib, parallelism);
    }

    // RFC 9383 §3.2: PBKDF input = len(pw)||pw || len(idProver)||idProver || len(idVerifier)||idVerifier
    private static byte[] BuildPasswordInput(string password)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try
        {
            using var ms = new MemoryStream();
            AppendLenPrefixed(ms, pw);
            AppendLenPrefixed(ms, RemoteAuthConstants.IdProver);
            AppendLenPrefixed(ms, RemoteAuthConstants.IdVerifier);
            return ms.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    private static void AppendLenPrefixed(Stream s, byte[] value)
    {
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)value.Length);
        s.Write(len);
        s.Write(value);
    }
}
