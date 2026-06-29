// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using System.Xml.Linq;
using Zeus.Contracts;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests.Wsjtx;

public sealed class N1mmContactInfoEncoderTests
{
    private static LogEntry SampleEntry() => new(
        Id: "abc",
        QsoDateTimeUtc: new DateTime(2026, 6, 27, 14, 30, 15, DateTimeKind.Utc),
        Callsign: "K1ABC",
        Name: "Joe",
        FrequencyMhz: 14.074,
        Band: "20m",
        Mode: "FT8",
        RstSent: "-12",
        RstRcvd: "-08",
        Grid: "FN31",
        Country: "United States",
        Dxcc: 291,
        CqZone: 5,
        ItuZone: 8,
        State: "CT",
        Comment: "tnx",
        CreatedUtc: DateTime.UtcNow);

    [Fact]
    public void Encode_ProducesWellFormedContactInfoXml()
    {
        var xml = N1mmContactInfoEncoder.EncodeXml(SampleEntry(), "W1XYZ");
        var doc = XDocument.Parse(xml); // throws if not well-formed
        Assert.Equal("contactinfo", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void Encode_MapsLoadBearingFields()
    {
        var xml = N1mmContactInfoEncoder.EncodeXml(SampleEntry(), "W1XYZ");
        var root = XDocument.Parse(xml).Root!;

        string Get(string n) => root.Element(n)?.Value ?? "";

        Assert.Equal("K1ABC", Get("call"));
        Assert.Equal("W1XYZ", Get("mycall"));
        Assert.Equal("W1XYZ", Get("operator"));
        Assert.Equal("FT8", Get("mode"));
        Assert.Equal("FN31", Get("gridsquare"));
        Assert.Equal("-12", Get("snt"));
        Assert.Equal("-08", Get("rcv"));
        Assert.Equal("14", Get("band")); // N1MM MHz designator for 20m
        Assert.Equal("Joe", Get("name"));
        Assert.Equal("2026-06-27 14:30:15", Get("timestamp"));
    }

    [Fact]
    public void Encode_FrequencyIsTensOfHz()
    {
        // 14.074 MHz -> 14_074_000 Hz -> 1_407_400 tens-of-Hz (N1MM unit).
        var xml = N1mmContactInfoEncoder.EncodeXml(SampleEntry(), "W1XYZ");
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("1407400", root.Element("rxfreq")!.Value);
        Assert.Equal("1407400", root.Element("txfreq")!.Value);
    }

    [Fact]
    public void Encode_EscapesXmlSpecialCharacters()
    {
        var entry = SampleEntry() with { Comment = "a & b <test>" };
        var xml = N1mmContactInfoEncoder.EncodeXml(entry, "W1XYZ");
        // Must still parse and round-trip the literal value.
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("a & b <test>", root.Element("comment")!.Value);
    }

    [Fact]
    public void Encode_NoFrequency_EmitsZeroFreq()
    {
        var entry = SampleEntry() with { FrequencyMhz = null };
        var root = XDocument.Parse(N1mmContactInfoEncoder.EncodeXml(entry, "W1XYZ")).Root!;
        Assert.Equal("0", root.Element("txfreq")!.Value);
    }

    [Theory]
    [InlineData("20m", "14")]
    [InlineData("20M", "14")]
    [InlineData("20", "14")]
    [InlineData("160m", "1.8")]
    [InlineData("80m", "3.5")]
    [InlineData("40m", "7")]
    [InlineData("30m", "10")]
    [InlineData("10m", "28")]
    [InlineData("6m", "50")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeBand_MapsMetersToMhzDesignator(string? input, string expected)
        => Assert.Equal(expected, N1mmContactInfoEncoder.NormalizeBand(input));

    [Fact]
    public void NormalizeBand_UnknownLabel_DerivesFromFrequency()
        // Label we don't have in the table -> fall back to the QSO frequency.
        => Assert.Equal("14", N1mmContactInfoEncoder.NormalizeBand("twenty", 14.074));

    [Fact]
    public void NormalizeBand_UnknownLabelNoFreq_PassesThroughStripped()
        => Assert.Equal("999", N1mmContactInfoEncoder.NormalizeBand("999m"));

    [Fact]
    public void Encode_OutputIsUtf8()
    {
        var bytes = N1mmContactInfoEncoder.Encode(SampleEntry(), "W1XYZ");
        var s = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<contactinfo>", s);
    }
}
