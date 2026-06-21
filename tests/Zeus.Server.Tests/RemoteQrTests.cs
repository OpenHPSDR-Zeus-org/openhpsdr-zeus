using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

public sealed class RemoteQrTests
{
    [Fact]
    public void AddressFor_BuildsGoUrl_Uppercased()
        => Assert.Equal("https://openhpsdrzeus.com/go/EI6LF", RemoteQr.AddressFor("ei6lf"));

    [Fact]
    public void AddressFor_EscapesPortableCallsign()
        => Assert.Equal("https://openhpsdrzeus.com/go/EI6LF%2FP", RemoteQr.AddressFor("EI6LF/P"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddressFor_NullWhenNoCallsign(string? callsign)
        => Assert.Null(RemoteQr.AddressFor(callsign));

    [Fact]
    public void Svg_RendersScannableQr()
    {
        var svg = RemoteQr.Svg(RemoteQr.AddressFor("EI6LF")!);

        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains("</svg>", svg);
        Assert.True(svg.Length > 200, "SVG QR should contain real module geometry");
    }

    [Fact]
    public void Svg_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => RemoteQr.Svg("  "));
}
