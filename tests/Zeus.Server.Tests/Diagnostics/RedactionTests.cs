// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public class RedactionTests
{
    // --- Secret key/value pairs ----------------------------------------------

    [Theory]
    [InlineData("password=hunter2", "password=***")]
    [InlineData("pwd: s3cr3t", "pwd: ***")]
    [InlineData("token=abc.def.ghi", "token=***")]
    [InlineData("api_key = AKIA12345", "api_key = ***")]
    [InlineData("apikey:zzz999", "apikey:***")]
    [InlineData("Authorization: Bearer xyz", "Authorization: Bearer ***")] // scheme kept, credential masked
    [InlineData("Authorization=Bearer abc123def", "Authorization=Bearer ***")]
    [InlineData("secret=topsecret", "secret=***")]
    public void Scrub_MasksSecretValues(string input, string expected)
    {
        Assert.Equal(expected, Redaction.Scrub(input));
    }

    [Fact]
    public void Scrub_MasksSecret_KeepsSurroundingContext()
    {
        var result = Redaction.Scrub("connecting with password=hunter2 to radio");
        Assert.Contains("password=***", result);
        Assert.Contains("connecting with", result);
        Assert.Contains("to radio", result);
        Assert.DoesNotContain("hunter2", result);
    }

    // --- Email ----------------------------------------------------------------

    [Fact]
    public void Scrub_MasksEmail()
    {
        var result = Redaction.Scrub("operator kb2uka80@gmail.com reported an issue");
        Assert.DoesNotContain("kb2uka80@gmail.com", result);
        Assert.Contains("***@***", result);
    }

    // --- IPv4 -----------------------------------------------------------------

    [Theory]
    [InlineData("radio at 192.168.4.10 timed out", "192.168.4.10")]
    [InlineData("discovered 10.70.120.219", "10.70.120.219")]
    [InlineData("0.0.0.0 and 255.255.255.255", "255.255.255.255")]
    public void Scrub_MasksIpv4(string input, string leaked)
    {
        var result = Redaction.Scrub(input);
        Assert.DoesNotContain(leaked, result);
        Assert.Contains("x.x.x.x", result);
    }

    // --- IPv6 -----------------------------------------------------------------

    [Theory]
    [InlineData("fe80::1ff:fe23:4567:890a")]
    [InlineData("2001:db8:85a3::8a2e:370:7334")]
    [InlineData("[2001:db8::1]")]
    public void Scrub_MasksIpv6(string input)
    {
        var result = Redaction.Scrub("host " + input + " offline");
        Assert.Contains("[ipv6]", result);
        Assert.DoesNotContain("db8", result);
    }

    // --- MAC ------------------------------------------------------------------

    [Theory]
    [InlineData("00:1a:2b:3c:4d:5e")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    public void Scrub_MasksMac(string mac)
    {
        var result = Redaction.Scrub("nic " + mac + " up");
        Assert.Contains("xx:xx:xx:xx:xx:xx", result);
        Assert.DoesNotContain(mac, result);
    }

    // --- Home-directory username ---------------------------------------------

    [Theory]
    [InlineData("/Users/kb2uka_mac/Programs/zeus.db", "/Users/<user>/Programs/zeus.db")]
    [InlineData("/home/doug/.config/zeus", "/home/<user>/.config/zeus")]
    [InlineData(@"C:\Users\Doug\AppData\zeus.db", @"C:\Users\<user>\AppData\zeus.db")]
    public void Scrub_MasksHomeUsername(string input, string expected)
    {
        Assert.Equal(expected, Redaction.Scrub(input));
    }

    // --- Maidenhead grid ------------------------------------------------------

    [Theory]
    [InlineData("grid FN31pr", "FN31")]
    [InlineData("locator JO62qm tonight", "JO62")]
    public void Scrub_TruncatesPreciseGridToFourChars(string input, string keptField)
    {
        var result = Redaction.Scrub(input);
        Assert.Contains(keptField, result);
        // The 6-char form must be gone; coarse 4-char survives.
        Assert.DoesNotContain(keptField + "pr", result);
        Assert.DoesNotContain(keptField + "qm", result);
    }

    [Fact]
    public void Scrub_LeavesFourCharGridIntact()
    {
        Assert.Equal("grid FN31", Redaction.Scrub("grid FN31"));
    }

    // --- Negative cases: diagnosis-critical data MUST survive -----------------

    [Theory]
    [InlineData("KB2UKA")]
    [InlineData("EI6LF")]
    [InlineData("N9WAR")]
    [InlineData("MW0LGE")]
    public void Scrub_KeepsCallsigns(string callsign)
    {
        Assert.Equal("worked " + callsign, Redaction.Scrub("worked " + callsign));
    }

    [Theory]
    [InlineData("tuned to 14.046 MHz")]
    [InlineData("VFO A 14046000 Hz")]
    [InlineData("drive=48 peak=0.655")]
    public void Scrub_KeepsFrequencyAndDriveInfo(string line)
    {
        Assert.Equal(line, Redaction.Scrub(line));
    }

    [Theory]
    [InlineData("board=HermesLite2")]
    [InlineData("OrionMkII variant G2")]
    [InlineData("ANAN-7000DLE firmware 2.6")]
    [InlineData("connected to Hermes-Lite 2")]
    public void Scrub_KeepsBoardAndFirmware(string line)
    {
        Assert.Equal(line, Redaction.Scrub(line));
    }

    [Fact]
    public void Scrub_DoesNotMangleTimestamps()
    {
        // A bare HH:mm:ss timestamp must not be mistaken for an IPv6 address.
        var line = "10:11:12.345 INFO RadioService connected";
        Assert.Equal(line, Redaction.Scrub(line));
    }

    [Fact]
    public void Scrub_DoesNotMaskOrdinaryWords()
    {
        // Words that vaguely resemble a grid (wrong shape) must be left alone.
        var line = "the letter arrived and version 12.3 shipped";
        Assert.Equal(line, Redaction.Scrub(line));
    }

    [Fact]
    public void Scrub_NullOrEmpty_ReturnedUnchanged()
    {
        Assert.Equal("", Redaction.Scrub(""));
        Assert.Null(Redaction.Scrub(null!));
    }

    [Fact]
    public void ScrubAll_PreservesOrderAndScrubsEach()
    {
        var input = new[] { "password=abc", "callsign KB2UKA", "ip 192.168.0.1" };
        var result = Redaction.ScrubAll(input);
        Assert.Equal(3, result.Count);
        Assert.Equal("password=***", result[0]);
        Assert.Equal("callsign KB2UKA", result[1]);
        Assert.Equal("ip x.x.x.x", result[2]);
    }
}
