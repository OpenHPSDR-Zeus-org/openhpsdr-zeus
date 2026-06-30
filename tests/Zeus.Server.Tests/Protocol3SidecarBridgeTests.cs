using System.Text.Json.Nodes;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3SidecarBridgeTests
{
    [Fact]
    public void ValidateDiagnosticsSnapshot_AcceptsN9DspTenStreamSidecar()
    {
        var snapshot = Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
            Diagnostics(streams: 10, engine: "n9dsp", status: "ok"),
            new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
            expectedRxStreams: 10);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("n9dsp", snapshot.DspEngine);
        Assert.Equal(10, snapshot.RxActiveStreams);
        Assert.Equal(10, snapshot.RxExpectedStreams);
        Assert.Equal(10, snapshot.DspActiveChannels);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_AcceptsDegradedN9DspTenStreamSidecar()
    {
        var snapshot = Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
            Diagnostics(streams: 10, engine: "n9dsp", status: "degraded"),
            new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
            expectedRxStreams: 10);

        Assert.Equal("degraded", snapshot.Status);
        Assert.Equal("n9dsp", snapshot.DspEngine);
        Assert.Equal(10, snapshot.RxActiveStreams);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsNonN9DspEngine()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 10, engine: "wdsp", status: "ok"),
                new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("expected 'n9dsp'", ex.Message);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsSixStreamSidecar()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 6, engine: "n9dsp", status: "ok"),
                new Uri("http://127.0.0.1:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("requires 10", ex.Message);
    }

    [Fact]
    public void ValidateDiagnosticsSnapshot_RejectsNonLoopbackDiagnosticsUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Protocol3SidecarBridge.ValidateDiagnosticsSnapshot(
                Diagnostics(streams: 10, engine: "n9dsp", status: "ok"),
                new Uri("http://192.168.1.25:2074/api/diagnostics/v2"),
                expectedRxStreams: 10));

        Assert.Contains("loopback", ex.Message);
    }

    private static JsonObject Diagnostics(int streams, string engine, string status) => new()
    {
        ["protocol"] = "p3",
        ["source"] = "radioSession",
        ["p3"] = new JsonObject
        {
            ["status"] = status,
            ["rxExpectedStreams"] = streams,
            ["rxActiveStreams"] = streams,
            ["dsp"] = new JsonObject
            {
                ["engine"] = engine,
                ["activeChannels"] = streams,
            },
        },
    };
}
