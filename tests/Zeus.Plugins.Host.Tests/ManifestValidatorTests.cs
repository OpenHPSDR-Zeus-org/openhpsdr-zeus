using Zeus.Plugins.Contracts;
using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Tests;

public class ManifestValidatorTests
{
    private static PluginManifest Make(
        string id = "com.example.test",
        string version = "1.0.0",
        int sdkAbi = AbiVersion.Current,
        string sdkMin = AbiVersion.SdkVersion,
        string assembly = "Test.dll")
        => new()
        {
            Id = id,
            Name = "Test",
            Version = version,
            Sdk = new SdkRequirement { Abi = sdkAbi, MinVersion = sdkMin },
            Entrypoint = new EntryPoint { Assembly = assembly },
        };

    [Fact]
    public void HappyPath_NoErrors()
    {
        Assert.Empty(ManifestValidator.Validate(Make()));
    }

    [Theory]
    [InlineData("Invalid-Id")]      // uppercase
    [InlineData(".leading.dot")]    // bad prefix
    [InlineData("trailing.")]       // bad suffix
    [InlineData("")]
    public void RejectsInvalidIds(string id)
    {
        var errors = ManifestValidator.Validate(Make(id: id));
        Assert.Contains(errors, e => e.Contains("invalid id", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("abc")]
    public void RejectsBadVersion(string v)
    {
        var errors = ManifestValidator.Validate(Make(version: v));
        Assert.Contains(errors, e => e.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsAbsoluteAssemblyPath()
    {
        var errors = ManifestValidator.Validate(Make(assembly: "/etc/passwd.dll"));
        Assert.Contains(errors, e => e.Contains("relative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsParentTraversalAssemblyPath()
    {
        var errors = ManifestValidator.Validate(Make(assembly: "../escape.dll"));
        Assert.Contains(errors, e => e.Contains("relative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsNonDllAssembly()
    {
        var errors = ManifestValidator.Validate(Make(assembly: "Test.exe"));
        Assert.Contains(errors, e => e.Contains(".dll", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsVst3PathTraversal()
    {
        var m = Make();
        m = m with { Audio = new AudioBlock { Vst3Path = "../etc/payload.vst3" } };
        var errors = ManifestValidator.Validate(m);
        Assert.Contains(errors, e => e.Contains("vst3Path", StringComparison.Ordinal));
    }

    [Fact]
    public void IsAbiCompatible_ExactMatch_True()
    {
        // Host at the current SDK version vs a plugin built for it (Make() defaults
        // sdkMin to AbiVersion.SdkVersion) — reference the constant, not a frozen
        // literal, so this stays correct across additive SDK minor bumps.
        Assert.True(ManifestValidator.IsAbiCompatible(Make(), 1, AbiVersion.SdkVersion));
    }

    [Fact]
    public void IsAbiCompatible_MinorMinNewerThanHost_False()
    {
        var m = Make(sdkMin: "1.5.0");
        Assert.False(ManifestValidator.IsAbiCompatible(m, 1, "1.0.0"));
    }

    [Fact]
    public void IsAbiCompatible_MinorMinOlderThanHost_True()
    {
        var m = Make(sdkMin: "1.0.0");
        Assert.True(ManifestValidator.IsAbiCompatible(m, 1, "1.5.0"));
    }

    [Fact]
    public void IsAbiCompatible_AbiMismatch_False()
    {
        var m = Make(sdkAbi: 2);
        Assert.False(ManifestValidator.IsAbiCompatible(m, 1, "1.0.0"));
    }

    [Fact]
    public void IsAbiCompatible_MajorMismatch_False()
    {
        // Same abi number but a different major in the SemVer means
        // the plugin was built against a different breaking-change tier
        // — refuse. (Spec: major bumps in lockstep with abi.)
        var m = Make(sdkAbi: 1, sdkMin: "2.0.0");
        Assert.False(ManifestValidator.IsAbiCompatible(m, 1, "1.5.0"));
    }
}
