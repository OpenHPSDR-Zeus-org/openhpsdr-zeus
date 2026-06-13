namespace Zeus.Plugins.Contracts;

/// <summary>
/// SDK ABI / version constants. Plugins compare their manifest's
/// <c>sdk.abi</c> against <see cref="Current"/> and refuse to load on mismatch.
/// </summary>
public static class AbiVersion
{
    /// <summary>
    /// Binary contract version. Bump when any interface in this assembly
    /// changes shape (new required method, changed signature, removed type).
    /// Plugins built against a different value MUST be refused by the host.
    /// </summary>
    public const int Current = 1;

    /// <summary>
    /// SemVer string for the contracts package. Bump minor on additive
    /// change (default-interface methods, new optional manifest fields).
    /// Bump major in lockstep with <see cref="Current"/>.
    /// </summary>
    public const string SdkVersion = "1.2.0";
}
