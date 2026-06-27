using LiteDB;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Persists the single remote-access SPAKE2+ verifier (ADR-0008) in a one-row
/// LiteDB collection sharing zeus-prefs.db, mirroring the other settings stores.
/// Stores only the verifier (salt, w0, L) and Argon2id params — never the
/// password, never w1. A stolen DB does not yield the password.
/// </summary>
public sealed class RemotePasswordStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RemotePasswordEntry> _col;
    private readonly ILogger<RemotePasswordStore> _log;
    private readonly object _sync = new();

    public RemotePasswordStore(ILogger<RemotePasswordStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _col = _db.GetCollection<RemotePasswordEntry>("remote_password");
    }

    /// <summary>True when the operator has set a remote-access password.</summary>
    public bool HasPassword()
    {
        lock (_sync)
            return _col.FindAll().Any();
    }

    /// <summary>Set or replace the remote-access password (runs Argon2id registration).</summary>
    public void Set(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("password is empty", nameof(password));

        var v = Spake2PlusRegistration.Register(password);
        lock (_sync)
        {
            _col.DeleteAll();
            _col.Insert(new RemotePasswordEntry
            {
                Salt = v.Salt,
                W0 = v.W0,
                L = v.L,
                Iterations = v.Iterations,
                MemoryKib = v.MemoryKib,
                Parallelism = v.Parallelism,
            });
        }
        _log.LogInformation("remote-access password set (verifier stored; password not persisted)");
    }

    /// <summary>Remove the password — disables remote access (no-password-no-remote).</summary>
    public void Clear()
    {
        lock (_sync)
            _col.DeleteAll();
        _log.LogInformation("remote-access password cleared");
    }

    /// <summary>
    /// The stored verifier for the auth gate: w0 (scalar), L (point), and the
    /// salt + Argon2 params the client needs to re-derive. Null if unset.
    /// </summary>
    public RemoteVerifierMaterial? GetVerifier()
    {
        lock (_sync)
        {
            var e = _col.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new RemoteVerifierMaterial(
                W0: Spake2Plus.ScalarFromBytes(e.W0),
                L: Spake2Plus.DecodeL(e.L),
                Salt: e.Salt,
                Iterations: e.Iterations,
                MemoryKib: e.MemoryKib,
                Parallelism: e.Parallelism);
        }
    }

    public void Dispose() => _dbLease.Dispose();

    public sealed class RemotePasswordEntry
    {
        public int Id { get; set; }
        public byte[] Salt { get; set; } = [];
        public byte[] W0 { get; set; } = [];
        public byte[] L { get; set; } = [];
        public int Iterations { get; set; }
        public int MemoryKib { get; set; }
        public int Parallelism { get; set; }
    }
}

/// <summary>Decoded verifier material handed to the auth gate at session start.</summary>
public sealed record RemoteVerifierMaterial(
    BigInteger W0, ECPoint L, byte[] Salt, int Iterations, int MemoryKib, int Parallelism);

/// <summary>Body for <c>POST /api/remote/password</c>.</summary>
public sealed record RemotePasswordRequest(string Password);
