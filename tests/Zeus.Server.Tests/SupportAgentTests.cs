// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Zeus.Support.Contracts;
using Zeus.SupportAgent;

namespace Zeus.Server.Tests;

public class SidecarOptionsTests
{
    [Fact]
    public void Parse_Valid_PopulatesAllFields()
    {
        var ok = SidecarOptions.TryParse(
            ["--supervise-pid", "1234", "--session", "abc", "--app-log", "a.log",
             "--startup-log", "s.log", "--crash-dir", "c", "--app-version", "1.0"],
            out var opts, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.NotNull(opts);
        Assert.Equal(1234, opts!.SupervisePid);
        Assert.Equal("abc", opts.SessionToken);
        Assert.Equal("a.log", opts.AppLogPath);
        Assert.Equal("s.log", opts.StartupLogPath);
        Assert.Equal("c", opts.CrashDir);
        Assert.Equal("1.0", opts.AppVersion);
    }

    [Fact]
    public void Parse_MissingPid_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--crash-dir", "c"], out var opts, out var err));
        Assert.Null(opts);
        Assert.Contains("supervise-pid", err);
    }

    [Fact]
    public void Parse_MissingCrashDir_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--supervise-pid", "5"], out _, out var err));
        Assert.Contains("crash-dir", err);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("notanint")]
    public void Parse_BadPid_Fails(string pid)
    {
        Assert.False(SidecarOptions.TryParse(["--supervise-pid", pid, "--crash-dir", "c"], out _, out var err));
        Assert.Contains("supervise-pid", err);
    }

    [Fact]
    public void Parse_UnknownArg_Fails()
    {
        Assert.False(SidecarOptions.TryParse(["--bogus", "x"], out _, out var err));
        Assert.Contains("bogus", err);
    }
}

public class SupportAgentLogTailTests : IDisposable
{
    private readonly string _dir;
    public SupportAgentLogTailTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ReadLastLines_ReturnsTrailingLines()
    {
        var p = Path.Combine(_dir, "f.log");
        File.WriteAllLines(p, ["a", "b", "c", "d", "e"]);
        Assert.Equal(["c", "d", "e"], LogTail.ReadLastLines(p, 3));
    }

    [Fact]
    public void ReadLastLines_FewerThanRequested_ReturnsAll()
    {
        var p = Path.Combine(_dir, "f.log");
        File.WriteAllLines(p, ["a", "b"]);
        Assert.Equal(["a", "b"], LogTail.ReadLastLines(p, 10));
    }

    [Fact]
    public void ReadLastLines_MissingFile_IsEmpty()
        => Assert.Empty(LogTail.ReadLastLines(Path.Combine(_dir, "nope.log"), 5));

    [Fact]
    public void ReadAppLogTail_SpansPreviousRollWhenActiveShort()
    {
        var active = Path.Combine(_dir, "zeus-app.log");
        var roll = Path.Combine(_dir, "zeus-app.1.log");
        File.WriteAllLines(roll, ["A1", "A2", "A3", "A4", "A5"]);
        File.WriteAllLines(active, ["B1", "B2"]);

        // Want 4 lines: active has 2, so the previous roll fills the other 2.
        Assert.Equal(["A4", "A5", "B1", "B2"], LogTail.ReadAppLogTail(active, 4));
    }

    [Fact]
    public void ReadAppLogTail_ActiveAloneSatisfiesRequest()
    {
        var active = Path.Combine(_dir, "zeus-app.log");
        File.WriteAllLines(active, ["B1", "B2", "B3", "B4"]);
        Assert.Equal(["B3", "B4"], LogTail.ReadAppLogTail(active, 2));
    }
}

public class CrashCaptureTests : IDisposable
{
    private readonly string _dir;
    public CrashCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"zeus-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SidecarOptions Opts(string? appLog = null, string? startupLog = null) =>
        new(SupervisePid: 4321, SessionToken: "s", AppLogPath: appLog,
            StartupLogPath: startupLog, CrashDir: _dir, AppVersion: "9.9");

    [Fact]
    public void BuildRecord_CapturesLogTailsAndMetadata()
    {
        var appLog = Path.Combine(_dir, "zeus-app.log");
        File.WriteAllLines(appLog, ["one", "two", "three"]);

        var rec = CrashCapture.BuildRecord(Opts(appLog), exitCode: -1073741819, nowUnixMs: 1_700_000_000_000);

        Assert.Equal(SupportCrashRecord.CurrentSchemaVersion, rec.SchemaVersion);
        Assert.Equal(4321, rec.Pid);
        Assert.Equal(-1073741819, rec.ExitCode);
        Assert.True(rec.Crashed);
        Assert.Equal("9.9", rec.AppVersion);
        Assert.Contains("three", rec.AppLogTail);
        Assert.False(string.IsNullOrEmpty(rec.Platform));
    }

    [Fact]
    public void WriteCrashRecord_WritesDeserialisableJson()
    {
        var path = CrashCapture.WriteCrashRecord(Opts(), exitCode: 5, nowUnixMs: 1_700_000_000_000);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var back = JsonSerializer.Deserialize(File.ReadAllText(path!), SupportIpcJsonContext.Default.SupportCrashRecord);
        Assert.NotNull(back);
        Assert.Equal(4321, back!.Pid);
        Assert.Equal(5, back.ExitCode);
        Assert.True(back.Crashed);
    }

    [Fact]
    public void WriteCrashRecord_PrunesToBoundedHistory()
    {
        // Seed more than the retention cap with time-sortable names.
        for (int i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(_dir, SupportPaths.CrashRecordFileName(1_000_000_000_000 + i, 1)), "{}");

        CrashCapture.WriteCrashRecord(Opts(), exitCode: 0, nowUnixMs: 1_700_000_000_000);

        var remaining = Directory.GetFiles(_dir, "crash-*.json").Length;
        Assert.True(remaining <= 25, $"expected <=25 retained crash records, found {remaining}");
    }
}

public class SupportPathsTests
{
    [Fact]
    public void CleanExitMarkerName_IsPerPid()
        => Assert.Equal("clean-exit-777.marker", SupportPaths.CleanExitMarkerName(777));

    [Fact]
    public void CrashRecordFileName_IsTimeSortable()
    {
        var early = SupportPaths.CrashRecordFileName(1_000_000_000_000, 1);
        var late = SupportPaths.CrashRecordFileName(1_700_000_000_000, 1);
        Assert.True(string.CompareOrdinal(early, late) < 0);
        Assert.StartsWith("crash-", early);
        Assert.EndsWith(".json", early);
    }
}

/// <summary>
/// A scriptable <see cref="ISupportBrokerClient"/> for the presence/crash tests:
/// records every call and lets each method's success be forced.
/// </summary>
internal sealed class FakeBrokerClient : ISupportBrokerClient
{
    public int Registers;
    public int Heartbeats;
    public int Drops;
    public int Uploads;
    public string? LastUploadedJson;

    public bool RegisterResult = true;
    public bool UploadResult = true;

    public Task<bool> RegisterAsync(CancellationToken ct) { Registers++; return Task.FromResult(RegisterResult); }
    public Task<bool> HeartbeatAsync(CancellationToken ct) { Heartbeats++; return Task.FromResult(true); }
    public Task<bool> DropAsync(CancellationToken ct) { Drops++; return Task.FromResult(true); }
    public Task<bool> UploadCrashAsync(string json, CancellationToken ct)
    {
        Uploads++;
        LastUploadedJson = json;
        return Task.FromResult(UploadResult);
    }
}

public class SidecarOptionsRemoteFlagsTests
{
    [Fact]
    public void Parse_RemoteFlags_PopulatesPresenceFields()
    {
        var ok = SidecarOptions.TryParse(
            ["--supervise-pid", "10", "--crash-dir", "c",
             "--broker-url", "wss://example.test/signal?role=host",
             "--operator-callsign", "n9war", "--remote-diagnostics", "on",
             "--auto-share-crash", "on"],
            out var opts, out var err);

        Assert.True(ok, err);
        Assert.Equal("wss://example.test/signal?role=host", opts!.BrokerUrl);
        Assert.Equal("n9war", opts.OperatorCallsign);
        Assert.True(opts.RemoteDiagnosticsEnabled);
        Assert.True(opts.AutoShareOnCrash);
    }

    [Fact]
    public void Parse_RemoteFlags_DefaultOff()
    {
        Assert.True(SidecarOptions.TryParse(["--supervise-pid", "10", "--crash-dir", "c"], out var opts, out _));
        Assert.False(opts!.RemoteDiagnosticsEnabled);
        Assert.False(opts.AutoShareOnCrash);
        Assert.Null(opts.BrokerUrl);
        Assert.Null(opts.OperatorCallsign);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("off", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Parse_BoolFlag_AcceptsAliases(string value, bool expected)
    {
        Assert.True(SidecarOptions.TryParse(
            ["--supervise-pid", "1", "--crash-dir", "c", "--auto-share-crash", value], out var opts, out _));
        Assert.Equal(expected, opts!.AutoShareOnCrash);
    }

    [Fact]
    public void Parse_BadBoolFlag_Fails()
    {
        Assert.False(SidecarOptions.TryParse(
            ["--supervise-pid", "1", "--crash-dir", "c", "--remote-diagnostics", "maybe"], out _, out var err));
        Assert.Contains("remote-diagnostics", err);
    }
}

public class BrokerEndpointsTests
{
    [Fact]
    public void FromBrokerUrl_DerivesHttpsOriginFromWss()
    {
        var ep = BrokerEndpoints.FromBrokerUrl("wss://remote.openhpsdrzeus.com/signal?role=host");
        Assert.NotNull(ep);
        Assert.Equal("https://remote.openhpsdrzeus.com/", ep!.Origin.ToString());
        Assert.Equal("https://remote.openhpsdrzeus.com/presence/register", ep.PresenceRegister.ToString());
        Assert.Equal("https://remote.openhpsdrzeus.com/presence/heartbeat", ep.PresenceHeartbeat.ToString());
        Assert.Equal("https://remote.openhpsdrzeus.com/presence/drop", ep.PresenceDrop.ToString());
        Assert.Equal("https://remote.openhpsdrzeus.com/crash", ep.Crash.ToString());
    }

    [Fact]
    public void FromBrokerUrl_WsBecomesHttp_AndPreservesNonDefaultPort()
    {
        var ep = BrokerEndpoints.FromBrokerUrl("ws://127.0.0.1:8787/signal");
        Assert.NotNull(ep);
        Assert.Equal("http://127.0.0.1:8787/", ep!.Origin.ToString());
        Assert.Equal("http://127.0.0.1:8787/crash", ep.Crash.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.test")]
    public void FromBrokerUrl_InvalidOrUnsupported_ReturnsNull(string? url)
        => Assert.Null(BrokerEndpoints.FromBrokerUrl(url));
}

public class PresenceClientTests
{
    [Fact]
    public async Task Tick_AvailableFromStart_RegistersThenHeartbeats()
    {
        var broker = new FakeBrokerClient();
        var presence = new PresenceClient(broker, initiallyAvailable: true);

        await presence.TickAsync(CancellationToken.None); // registers
        await presence.TickAsync(CancellationToken.None); // heartbeats
        await presence.TickAsync(CancellationToken.None); // heartbeats

        Assert.Equal(1, broker.Registers);
        Assert.Equal(2, broker.Heartbeats);
        Assert.Equal(0, broker.Drops);
    }

    [Fact]
    public async Task Tick_NotAvailable_NeverRegisters()
    {
        var broker = new FakeBrokerClient();
        var presence = new PresenceClient(broker, initiallyAvailable: false);

        await presence.TickAsync(CancellationToken.None);
        await presence.TickAsync(CancellationToken.None);

        Assert.Equal(0, broker.Registers);
        Assert.Equal(0, broker.Heartbeats);
        Assert.Equal(0, broker.Drops);
    }

    [Fact]
    public async Task Tick_AvailabilityGoesOff_DropsOnceThenStaysQuiet()
    {
        var broker = new FakeBrokerClient();
        var presence = new PresenceClient(broker, initiallyAvailable: true);

        await presence.TickAsync(CancellationToken.None); // register
        await presence.TickAsync(CancellationToken.None); // heartbeat
        presence.SetAvailable(false);
        await presence.TickAsync(CancellationToken.None); // drop
        await presence.TickAsync(CancellationToken.None); // quiet
        await presence.TickAsync(CancellationToken.None); // quiet

        Assert.Equal(1, broker.Registers);
        Assert.Equal(1, broker.Heartbeats);
        Assert.Equal(1, broker.Drops);
    }

    [Fact]
    public async Task Tick_RegisterFails_RetriesRegisterUntilItSucceeds()
    {
        var broker = new FakeBrokerClient { RegisterResult = false };
        var presence = new PresenceClient(broker, initiallyAvailable: true);

        await presence.TickAsync(CancellationToken.None); // register fails
        await presence.TickAsync(CancellationToken.None); // retries register (still not heartbeat)
        Assert.Equal(2, broker.Registers);
        Assert.Equal(0, broker.Heartbeats);

        broker.RegisterResult = true;
        await presence.TickAsync(CancellationToken.None); // register succeeds
        await presence.TickAsync(CancellationToken.None); // now heartbeats
        Assert.Equal(3, broker.Registers);
        Assert.Equal(1, broker.Heartbeats);
    }

    [Fact]
    public async Task Run_DropsOnShutdownWhenRegistered()
    {
        var broker = new FakeBrokerClient();
        // Immediate, non-blocking delay so the loop spins through ticks fast.
        var presence = new PresenceClient(
            broker, initiallyAvailable: true, interval: TimeSpan.Zero,
            delay: (_, ct) => Task.Delay(1, ct));

        using var cts = new CancellationTokenSource();
        var run = presence.RunAsync(cts.Token);
        // Let it register before cancelling.
        await Task.Delay(50);
        cts.Cancel();
        await run;

        Assert.True(broker.Registers >= 1);
        Assert.True(broker.Drops >= 1, "expected a best-effort drop on shutdown");
    }

    [Fact]
    public void DefaultInterval_IsWellInsideExpiry()
    {
        // 30s cadence vs the broker's 90s expiry — at least a 3x safety margin.
        Assert.True(PresenceClient.DefaultHeartbeatInterval <= TimeSpan.FromSeconds(30));
    }
}

public class CrashAutoShareTests
{
    [Fact]
    public async Task FlagOff_NeverTouchesBroker()
    {
        var broker = new FakeBrokerClient();
        var outcome = await CrashAutoShare.TryShareAsync(
            autoShareEnabled: false, crashRecordJson: "{\"x\":1}", broker, CancellationToken.None);

        Assert.Equal(CrashShareOutcome.SkippedNotOptedIn, outcome);
        Assert.Equal(0, broker.Uploads);
    }

    [Fact]
    public async Task FlagOn_NoBroker_SkipsNoBroker()
    {
        var outcome = await CrashAutoShare.TryShareAsync(
            autoShareEnabled: true, crashRecordJson: "{\"x\":1}", broker: null, CancellationToken.None);
        Assert.Equal(CrashShareOutcome.SkippedNoBroker, outcome);
    }

    [Fact]
    public async Task FlagOn_NoRecord_SkipsNoRecord()
    {
        var broker = new FakeBrokerClient();
        var outcome = await CrashAutoShare.TryShareAsync(
            autoShareEnabled: true, crashRecordJson: "  ", broker, CancellationToken.None);
        Assert.Equal(CrashShareOutcome.SkippedNoRecord, outcome);
        Assert.Equal(0, broker.Uploads);
    }

    [Fact]
    public async Task FlagOn_WithRecord_Uploads()
    {
        var broker = new FakeBrokerClient();
        var outcome = await CrashAutoShare.TryShareAsync(
            autoShareEnabled: true, crashRecordJson: "{\"pid\":1}", broker, CancellationToken.None);

        Assert.Equal(CrashShareOutcome.Uploaded, outcome);
        Assert.Equal(1, broker.Uploads);
        Assert.Equal("{\"pid\":1}", broker.LastUploadedJson);
    }

    [Fact]
    public async Task FlagOn_UploadRejected_ReportsFailure()
    {
        var broker = new FakeBrokerClient { UploadResult = false };
        var outcome = await CrashAutoShare.TryShareAsync(
            autoShareEnabled: true, crashRecordJson: "{\"pid\":1}", broker, CancellationToken.None);
        Assert.Equal(CrashShareOutcome.UploadFailed, outcome);
    }
}

public class HttpSupportBrokerClientIdentityTests
{
    private static BrokerEndpoints Endpoints() =>
        BrokerEndpoints.FromBrokerUrl("wss://remote.openhpsdrzeus.com/signal?role=host")!;

    [Fact]
    public void BlankSeed_IsNotConfigured()
    {
        using var http = new HttpClient();
        var broker = new HttpSupportBrokerClient(http, Endpoints(), "", "", "test-platform", "1.0");
        Assert.False(broker.IsConfigured);
    }

    [Fact]
    public void UpdateIdentity_RequiresBothCallsignAndKey()
    {
        using var http = new HttpClient();
        var broker = new HttpSupportBrokerClient(http, Endpoints(), "", "", "test-platform", "1.0");

        broker.UpdateIdentity("N9WAR", null);
        Assert.False(broker.IsConfigured); // callsign without key

        broker.UpdateIdentity(null, "KEY");
        Assert.False(broker.IsConfigured); // key without callsign

        broker.UpdateIdentity("N9WAR", "KEY");
        Assert.True(broker.IsConfigured); // both present

        broker.UpdateIdentity("N9WAR", "");
        Assert.False(broker.IsConfigured); // key cleared (sign-out)
    }

    [Fact]
    public async Task RegisterAndHeartbeat_EmitMetadataJsonBody()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var broker = new HttpSupportBrokerClient(
            http, Endpoints(), "N9WAR", "KEY", "win32-test", "9.9");
        broker.UpdateMetadata("ANAN-G2", "G2", radioConnected: true);

        Assert.True(await broker.RegisterAsync(CancellationToken.None));
        Assert.NotNull(handler.LastBody);
        using (var doc = JsonDocument.Parse(handler.LastBody!))
        {
            var root = doc.RootElement;
            Assert.Equal("win32-test", root.GetProperty("platform").GetString());
            Assert.Equal("9.9", root.GetProperty("appVersion").GetString());
            Assert.Equal("ANAN-G2", root.GetProperty("radioBoard").GetString());
            Assert.Equal("G2", root.GetProperty("radioModel").GetString());
            Assert.True(root.GetProperty("radioConnected").GetBoolean());
        }
        Assert.EndsWith("/presence/register", handler.LastUri!.AbsolutePath);

        Assert.True(await broker.HeartbeatAsync(CancellationToken.None));
        Assert.EndsWith("/presence/heartbeat", handler.LastUri!.AbsolutePath);
        Assert.NotNull(handler.LastBody);
    }

    [Fact]
    public async Task Register_NullRadio_EmitsJsonNulls()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var broker = new HttpSupportBrokerClient(
            http, Endpoints(), "N9WAR", "KEY", "win32-test", "9.9");
        // No radio connected: board/model null, connected false.
        broker.UpdateMetadata(null, null, radioConnected: false);

        Assert.True(await broker.RegisterAsync(CancellationToken.None));
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("radioBoard").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("radioModel").ValueKind);
        Assert.False(root.GetProperty("radioConnected").GetBoolean());
    }

    [Fact]
    public async Task Drop_StaysBodyless()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var broker = new HttpSupportBrokerClient(
            http, Endpoints(), "N9WAR", "KEY", "win32-test", "9.9");

        Assert.True(await broker.DropAsync(CancellationToken.None));
        Assert.EndsWith("/presence/drop", handler.LastUri!.AbsolutePath);
        Assert.Null(handler.LastBody); // drop carries no body
    }

    /// <summary>Captures the URI + request body of the last POST, replies 200 OK.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

/// <summary>
/// The lazy-identity gating that fixes the P3c presence bug: a "remote diagnostics
/// on" posture must NOT advertise until a usable QRZ identity (callsign + session
/// key) has arrived over IPC; once it has, presence comes online without a restart.
/// </summary>
public class PresenceCoordinatorTests
{
    private static (HttpSupportBrokerClient Broker, PresenceClient Presence, PresenceCoordinator Coord) Build(
        bool initialAutoShare = false)
    {
        var http = new HttpClient();
        var broker = new HttpSupportBrokerClient(
            http, BrokerEndpoints.FromBrokerUrl("wss://remote.openhpsdrzeus.com/signal")!, "", "",
            "test-platform", "1.0");
        var presence = new PresenceClient(broker, initiallyAvailable: false);
        var coord = new PresenceCoordinator(broker, presence, initialAutoShare);
        return (broker, presence, coord);
    }

    private static SupportIpcListener.SupportState State(
        bool remoteDiag, string? callsign, string? key, bool autoShare = false) =>
        new(callsign, remoteDiag, autoShare, key);

    [Fact]
    public void RemoteDiagOn_ButNoIdentity_StaysUnavailable()
    {
        var (_, presence, coord) = Build();
        coord.Apply(State(remoteDiag: true, callsign: "N9WAR", key: null)); // no session key yet
        Assert.False(presence.IsAvailable);
    }

    [Fact]
    public void IdentityArrivesWhileOn_BringsPresenceOnline()
    {
        var (_, presence, coord) = Build();

        // Switch is on but QRZ identity hasn't landed → not advertising (the exact
        // bug: operator toggled on, but presence never registered).
        coord.Apply(State(remoteDiag: true, callsign: "N9WAR", key: null));
        Assert.False(presence.IsAvailable);

        // QRZ login completes → identity pushed over IPC → presence comes online,
        // no restart.
        coord.Apply(State(remoteDiag: true, callsign: "N9WAR", key: "SESSION"));
        Assert.True(presence.IsAvailable);
    }

    [Fact]
    public void SwitchOff_WithIdentity_StaysUnavailable()
    {
        var (_, presence, coord) = Build();
        coord.Apply(State(remoteDiag: false, callsign: "N9WAR", key: "SESSION"));
        Assert.False(presence.IsAvailable);
    }

    [Fact]
    public void SignOut_DropsAvailability()
    {
        var (_, presence, coord) = Build();
        coord.Apply(State(remoteDiag: true, callsign: "N9WAR", key: "SESSION"));
        Assert.True(presence.IsAvailable);

        // QRZ logout → identity cleared → presence drops even though the switch is on.
        coord.Apply(State(remoteDiag: true, callsign: null, key: null));
        Assert.False(presence.IsAvailable);
    }

    [Fact]
    public void AutoShare_TracksLatestState_AndSeedsFromLaunch()
    {
        var (_, _, coord) = Build(initialAutoShare: true);
        Assert.True(coord.AutoShareOnCrash); // launch-time seed

        coord.Apply(State(remoteDiag: false, callsign: null, key: null, autoShare: false));
        Assert.False(coord.AutoShareOnCrash);

        coord.Apply(State(remoteDiag: true, callsign: "N9WAR", key: "K", autoShare: true));
        Assert.True(coord.AutoShareOnCrash);
    }

    [Fact]
    public void Apply_ForwardsRadioMetadataToBroker()
    {
        var broker = new FakeMutableBroker();
        var presence = new PresenceClient(broker, initiallyAvailable: false);
        var coord = new PresenceCoordinator(broker, presence);

        // A connected radio: board + variant model + connected flag must reach the broker.
        coord.Apply(new SupportIpcListener.SupportState(
            QrzCallsign: "N9WAR", RemoteDiagnosticsEnabled: true, AutoShareOnCrash: false,
            QrzSessionKey: "K",
            RadioBoard: "ANAN-G2", RadioModel: "G2", RadioConnected: true));

        Assert.Equal("ANAN-G2", broker.LastRadioBoard);
        Assert.Equal("G2", broker.LastRadioModel);
        Assert.True(broker.LastRadioConnected);
        Assert.Equal(1, broker.MetadataUpdates);

        // Disconnect: nulls + false propagate so the broker clears the radio.
        coord.Apply(new SupportIpcListener.SupportState(
            QrzCallsign: "N9WAR", RemoteDiagnosticsEnabled: true, AutoShareOnCrash: false,
            QrzSessionKey: "K",
            RadioBoard: null, RadioModel: null, RadioConnected: false));

        Assert.Null(broker.LastRadioBoard);
        Assert.Null(broker.LastRadioModel);
        Assert.False(broker.LastRadioConnected);
        Assert.Equal(2, broker.MetadataUpdates);
    }
}

/// <summary>A mutable-identity fake broker for coordinator/pipe tests; counts calls thread-safely.</summary>
internal sealed class FakeMutableBroker : IMutableSupportBrokerClient
{
    private string _callsign = "";
    private string _sessionKey = "";
    private int _registers;
    private int _drops;

    public int Registers => Volatile.Read(ref _registers);
    public int Drops => Volatile.Read(ref _drops);

    // Last radio metadata forwarded via UpdateMetadata, captured for assertions.
    public string? LastRadioBoard;
    public string? LastRadioModel;
    public bool LastRadioConnected;
    private int _metadataUpdates;
    public int MetadataUpdates => Volatile.Read(ref _metadataUpdates);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_callsign) && !string.IsNullOrWhiteSpace(_sessionKey);

    public void UpdateIdentity(string? callsign, string? sessionKey)
    {
        _callsign = callsign ?? "";
        _sessionKey = sessionKey ?? "";
    }

    public void UpdateMetadata(string? radioBoard, string? radioModel, bool radioConnected)
    {
        LastRadioBoard = radioBoard;
        LastRadioModel = radioModel;
        LastRadioConnected = radioConnected;
        Interlocked.Increment(ref _metadataUpdates);
    }

    public Task<bool> RegisterAsync(CancellationToken ct) { Interlocked.Increment(ref _registers); return Task.FromResult(true); }
    public Task<bool> HeartbeatAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<bool> DropAsync(CancellationToken ct) { Interlocked.Increment(ref _drops); return Task.FromResult(true); }
    public Task<bool> UploadCrashAsync(string json, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// End-to-end over a REAL OS named pipe: the backend (client) connecting to the
/// sidecar's <see cref="SupportIpcListener"/> (server) and pushing a SupportHello
/// must drive the <see cref="PresenceCoordinator"/> to register broker presence —
/// the exact path that was missing before P3c. Proves the wire transport + framing
/// + listener dispatch + lazy-identity gating + presence loop together.
/// </summary>
public class SupportIpcPipeHandshakeTests
{
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A short, per-test-unique session token. macOS caps a Unix-domain-socket
    /// path at 104 chars, and .NET realises a named pipe as
    /// "{TMPDIR}/CoreFxPipe_{pipeName}". On CI the agent TMPDIR
    /// ("/var/folders/../T/") is already ~49 chars, so a full 32-hex GUID token
    /// (pipe name "zeus-support-{32 hex}") tips the total past 104 and throws
    /// ArgumentOutOfRangeException. 8 hex chars keep the path comfortably under
    /// the limit while staying unique across the handful of concurrent pipe tests.
    /// </summary>
    private static string ShortSessionToken() => Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public async Task BackendHello_OnPlusIdentity_RegistersPresenceOverRealPipe()
    {
        var token = ShortSessionToken();
        var pipeName = SupportIpc.PipeNameForSession(token);
        var broker = new FakeMutableBroker();
        // Fast, non-blocking cadence so the presence loop ticks promptly after the
        // coordinator flips availability.
        var presence = new PresenceClient(
            broker, initiallyAvailable: false,
            interval: TimeSpan.FromMilliseconds(20), delay: (_, ct) => Task.Delay(10, ct));
        var coord = new PresenceCoordinator(broker, presence);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presenceTask = presence.RunAsync(cts.Token);
        var listener = new SupportIpcListener(token, coord.Apply);
        var listenerTask = listener.RunAsync(cts.Token);

        // Act as the backend bridge: connect to the sidecar's pipe and push a
        // SupportHello with the switch ON and a full QRZ identity.
        await using (var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(10_000, cts.Token);
            await SupportIpcFraming.WriteAsync(client, new SupportHello(
                ProtocolVersion: SupportIpc.ProtocolVersion, BackendPid: 1,
                AppVersion: "v", Platform: "p", QrzCallsign: "N9WAR",
                RemoteDiagnosticsEnabled: true, AutoShareOnCrash: false,
                AppLogPath: "a", StartupLogPath: "s", QrzSessionKey: "SESSION"), cts.Token);

            await WaitUntilAsync(() => broker.Registers >= 1, TimeSpan.FromSeconds(10));
        }

        Assert.True(broker.Registers >= 1, "presence should register once on+identity arrives over the pipe");

        cts.Cancel();
        await Task.WhenAny(Task.WhenAll(presenceTask, listenerTask), Task.Delay(2000));
    }

    [Fact]
    public async Task BackendHello_OnButNoIdentity_DoesNotRegister()
    {
        var token = ShortSessionToken();
        var pipeName = SupportIpc.PipeNameForSession(token);
        var broker = new FakeMutableBroker();
        var presence = new PresenceClient(
            broker, initiallyAvailable: false,
            interval: TimeSpan.FromMilliseconds(20), delay: (_, ct) => Task.Delay(10, ct));
        var coord = new PresenceCoordinator(broker, presence);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var presenceTask = presence.RunAsync(cts.Token);
        var listener = new SupportIpcListener(token, coord.Apply);
        var listenerTask = listener.RunAsync(cts.Token);

        await using (var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(10_000, cts.Token);
            // Switch ON but NO session key — must stay silent (the safety property).
            await SupportIpcFraming.WriteAsync(client, new SupportHello(
                ProtocolVersion: SupportIpc.ProtocolVersion, BackendPid: 1,
                AppVersion: "v", Platform: "p", QrzCallsign: "N9WAR",
                RemoteDiagnosticsEnabled: true, AutoShareOnCrash: false,
                AppLogPath: "a", StartupLogPath: "s", QrzSessionKey: null), cts.Token);

            // Give the loop ample time to (wrongly) register if the gate were broken.
            await Task.Delay(400);
        }

        Assert.Equal(0, broker.Registers);

        cts.Cancel();
        await Task.WhenAny(Task.WhenAll(presenceTask, listenerTask), Task.Delay(2000));
    }
}

public class ProcessSupervisorTests
{
    // A short-lived child that sleeps briefly then exits with a known code, so the
    // supervisor reliably attaches before it dies. Cross-platform via the OS shell.
    private static Process StartDummy(int exitCode, bool linger)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = linger
                ? $"ping -n 2 127.0.0.1 >nul & exit {exitCode}"
                : $"exit {exitCode}";
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"");
        }
        else
        {
            var cmd = linger ? $"sleep 0.6; exit {exitCode}" : $"exit {exitCode}";
            psi = new ProcessStartInfo("/bin/sh", $"-c \"{cmd}\"");
        }
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task WaitForExit_ReturnsExitCodeOfSupervisedProcess()
    {
        using var child = StartDummy(exitCode: 7, linger: true);
        var result = await ProcessSupervisor.WaitForExitAsync(child.Id, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.True(result.ProcessFound);

        // The supervisor attaches by PID via Process.GetProcessById — it is NOT the
        // process that started the child. On Windows the exit code is still readable
        // through the open process handle; on Unix only the real parent can reap a
        // child's status (waitpid), so ExitCode is unavailable and the production
        // crash record records null. This mirrors the live sidecar, which always
        // attaches to a backend PID it never started.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Equal(7, result.ExitCode);
        else
            Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task WaitForExit_ProcessAlreadyGone_ReportsNotFound()
    {
        using var child = StartDummy(exitCode: 3, linger: false);
        child.WaitForExit(); // ensure it is fully gone before we attach
        var goneId = child.Id;

        var result = await ProcessSupervisor.WaitForExitAsync(goneId, CancellationToken.None);
        Assert.False(result.ProcessFound);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task WaitForExit_Cancellation_ReportsCancelled()
    {
        using var child = StartDummy(exitCode: 0, linger: true);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var result = await ProcessSupervisor.WaitForExitAsync(child.Id, cts.Token);
        Assert.True(result.Cancelled);

        try { child.Kill(); } catch { }
    }
}
