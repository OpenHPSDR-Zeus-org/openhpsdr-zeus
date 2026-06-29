// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.CloudLog;

namespace Zeus.Server.Tests.CloudLog;

public sealed class CloudLogTests
{
    private const string Adif = "<CALL:5>K1ABC <MODE:3>FT8 <EOR>";

    // ---- Wavelog ---------------------------------------------------------

    [Fact]
    public async Task Wavelog_PostsJsonToApiQso_WithKeyAndProfile()
    {
        var handler = new StubHandler((_, _) => Reply(HttpStatusCode.OK, "{\"id\":42}"));
        var client = new WavelogClient(new SingleClientFactory(handler), NullLogger<WavelogClient>.Instance);
        var cfg = new WavelogConfig(Enabled: true, BaseUrl: "https://log.example.com", StationProfileId: "3");

        var result = await client.PublishAsync(cfg, "KEY123", Adif);

        Assert.True(result.Success);
        Assert.Equal("42", result.RemoteId);
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal("https://log.example.com/api/qso", req.Url);
        Assert.Equal("application/json", req.ContentType);
        Assert.Contains("\"key\":\"KEY123\"", req.Body);
        Assert.Contains("\"station_profile_id\":\"3\"", req.Body);
        Assert.Contains("\"type\":\"adif\"", req.Body);
        Assert.Contains("EOR", req.Body);
    }

    [Fact]
    public async Task Wavelog_RetriesIndexPhpOn404()
    {
        int call = 0;
        var handler = new StubHandler((_, _) =>
            ++call == 1 ? Reply(HttpStatusCode.NotFound, "") : Reply(HttpStatusCode.OK, "{}"));
        var client = new WavelogClient(new SingleClientFactory(handler), NullLogger<WavelogClient>.Instance);
        var cfg = new WavelogConfig(Enabled: true, BaseUrl: "https://log.example.com/", StationProfileId: "1");

        var result = await client.PublishAsync(cfg, "KEY", Adif);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://log.example.com/api/qso", handler.Requests[0].Url);
        Assert.Equal("https://log.example.com/index.php/api/qso", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Wavelog_MissingCredentials_SkipsWithoutSending()
    {
        var handler = new StubHandler((_, _) => Reply(HttpStatusCode.OK, "{}"));
        var client = new WavelogClient(new SingleClientFactory(handler), NullLogger<WavelogClient>.Instance);

        var result = await client.PublishAsync(
            new WavelogConfig(Enabled: true, BaseUrl: "https://x", StationProfileId: "1"), apiKey: "", Adif);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://x", false, "https://x/api/qso")]
    [InlineData("https://x/", false, "https://x/api/qso")]
    [InlineData("https://x", true, "https://x/index.php/api/qso")]
    public void Wavelog_BuildQsoUrl(string baseUrl, bool indexPhp, string expected)
        => Assert.Equal(expected, WavelogClient.BuildQsoUrl(baseUrl, indexPhp));

    // ---- Club Log --------------------------------------------------------

    [Fact]
    public async Task ClubLog_PostsFormUrlEncodedToRealtime()
    {
        var handler = new StubHandler((_, _) => Reply(HttpStatusCode.OK, "QSO OK"));
        var client = new ClubLogClient(new SingleClientFactory(handler), NullLogger<ClubLogClient>.Instance);
        var cfg = new ClubLogConfig(Enabled: true, Email: "me@example.com", Callsign: "k1abc");

        var result = await client.PublishAsync(cfg, "pw", "api", Adif);

        Assert.True(result.Success);
        var req = handler.Requests[0];
        Assert.Equal("https://clublog.org/realtime.php", req.Url);
        Assert.Equal("application/x-www-form-urlencoded", req.ContentType);
        Assert.Contains("email=me%40example.com", req.Body);
        Assert.Contains("callsign=K1ABC", req.Body); // upper-cased
        Assert.Contains("api=api", req.Body);
        Assert.Contains("password=pw", req.Body);
    }

    [Fact]
    public void ClubLog_403_IsHardStop()
    {
        var client = new ClubLogClient(new SingleClientFactory(new StubHandler((_, _) => Reply(HttpStatusCode.OK, ""))),
            NullLogger<ClubLogClient>.Instance);
        var r = client.MapResponse(HttpStatusCode.Forbidden, "auth failed");
        Assert.False(r.Success);
        Assert.True(r.HardStop);
    }

    [Theory]
    [InlineData(200, true, false)]
    [InlineData(400, false, false)]
    [InlineData(500, false, false)]
    public void ClubLog_StatusMapping(int code, bool success, bool hardStop)
    {
        var client = new ClubLogClient(new SingleClientFactory(new StubHandler((_, _) => Reply(HttpStatusCode.OK, ""))),
            NullLogger<ClubLogClient>.Instance);
        var r = client.MapResponse((HttpStatusCode)code, "body");
        Assert.Equal(success, r.Success);
        Assert.Equal(hardStop, r.HardStop);
    }

    // ---- CloudLogService: per-secret credential update -------------------

    [Fact]
    public async Task SetClubLogCredentials_PasswordOnlyUpdate_KeepsApiKey()
    {
        using var tmp = new TempDb();
        using var b = BuildService(tmp);
        b.Service.SetConfig(new CloudLogConfig(
            new WavelogConfig(), new ClubLogConfig(Enabled: true, Email: "a@b.com", Callsign: "K1ABC")));

        // Save both secrets.
        await b.Service.SetClubLogCredentialsAsync("pw1", "api1");
        var s1 = await b.Service.GetStatusAsync();
        Assert.True(s1.ClubLog.HasPassword);
        Assert.True(s1.ClubLog.HasApiKey);

        // Rotate ONLY the password (apiKey omitted -> null -> unchanged).
        await b.Service.SetClubLogCredentialsAsync("pw2", apiKey: null);
        var s2 = await b.Service.GetStatusAsync();
        Assert.True(s2.ClubLog.HasPassword);
        Assert.True(s2.ClubLog.HasApiKey); // survived the password-only rotation

        // Rotate ONLY the API key (password omitted -> null -> unchanged).
        await b.Service.SetClubLogCredentialsAsync(password: null, "api2");
        var s3 = await b.Service.GetStatusAsync();
        Assert.True(s3.ClubLog.HasPassword); // survived
        Assert.True(s3.ClubLog.HasApiKey);
    }

    [Fact]
    public async Task SetClubLogCredentials_EmptyStringClearsThatSecretOnly()
    {
        using var tmp = new TempDb();
        using var b = BuildService(tmp);
        b.Service.SetConfig(new CloudLogConfig(
            new WavelogConfig(), new ClubLogConfig(Enabled: true, Email: "a@b.com", Callsign: "K1ABC")));

        await b.Service.SetClubLogCredentialsAsync("pw1", "api1");
        await b.Service.SetClubLogCredentialsAsync("", apiKey: null); // empty clears pw, null leaves api key

        var s = await b.Service.GetStatusAsync();
        Assert.False(s.ClubLog.HasPassword); // cleared
        Assert.True(s.ClubLog.HasApiKey);    // untouched
    }

    [Fact]
    public async Task Publish_ClubLog403_DisablesClubLogInPersistedConfig()
    {
        using var tmp = new TempDb();
        var clubHandler = new StubHandler((_, _) => Reply(HttpStatusCode.Forbidden, "auth failed"));
        using var b = BuildService(tmp, clubHandler);
        b.Service.SetConfig(new CloudLogConfig(
            new WavelogConfig(), new ClubLogConfig(Enabled: true, Email: "a@b.com", Callsign: "K1ABC")));
        await b.Service.SetClubLogCredentialsAsync("pw", "api");

        await b.Service.PublishAsync(SampleLogEntry());

        Assert.True(clubHandler.Requests.Count >= 1);       // it really tried
        Assert.False(b.Service.GetConfig().ClubLog.Enabled); // hard-stop disabled it
        Assert.False(b.Store.Get()!.ClubLog.Enabled);        // and persisted the disable
    }

    private static LogEntry SampleLogEntry() => new(
        Id: "x", QsoDateTimeUtc: new DateTime(2026, 6, 27, 14, 30, 0, DateTimeKind.Utc),
        Callsign: "K1ABC", Name: null, FrequencyMhz: 14.074, Band: "20m", Mode: "FT8",
        RstSent: "-12", RstRcvd: "-08", Grid: "FN31", Country: null, Dxcc: null, CqZone: null,
        ItuZone: null, State: null, Comment: null, CreatedUtc: DateTime.UtcNow);

    private static ServiceBundle BuildService(TempDb tmp, HttpMessageHandler? clubHandler = null)
    {
        var store = new CloudLogConfigStore(NullLogger<CloudLogConfigStore>.Instance, tmp.Path);
        var creds = new CredentialStore(NullLogger<CredentialStore>.Instance, tmp.Path);
        var noop = new StubHandler((_, _) => Reply(HttpStatusCode.OK, "{}"));
        var wavelog = new WavelogClient(new SingleClientFactory(noop), NullLogger<WavelogClient>.Instance);
        var clubLog = new ClubLogClient(
            new SingleClientFactory(clubHandler ?? noop), NullLogger<ClubLogClient>.Instance);
        var svc = new CloudLogService(
            NullLogger<CloudLogService>.Instance, store, creds, wavelog, clubLog);
        return new ServiceBundle(svc, store, creds);
    }

    private sealed class ServiceBundle : IDisposable
    {
        public CloudLogService Service { get; }
        public CloudLogConfigStore Store { get; }
        private readonly CredentialStore _creds;
        public ServiceBundle(CloudLogService svc, CloudLogConfigStore store, CredentialStore creds)
        {
            Service = svc; Store = store; _creds = creds;
        }
        public void Dispose() { Store.Dispose(); _creds.Dispose(); }
    }

    // ---- Config store ----------------------------------------------------

    [Fact]
    public void ConfigStore_DefaultsToOff()
    {
        using var tmp = new TempDb();
        using var store = new CloudLogConfigStore(NullLogger<CloudLogConfigStore>.Instance, tmp.Path);
        Assert.Null(store.Get()); // nothing persisted -> caller uses default (OFF)
    }

    [Fact]
    public void ConfigStore_RoundTrips()
    {
        using var tmp = new TempDb();
        using (var store = new CloudLogConfigStore(NullLogger<CloudLogConfigStore>.Instance, tmp.Path))
        {
            store.Set(new CloudLogConfig(
                new WavelogConfig(true, "https://log", "7"),
                new ClubLogConfig(true, "a@b.com", "K1ABC")));
        }
        using (var store = new CloudLogConfigStore(NullLogger<CloudLogConfigStore>.Instance, tmp.Path))
        {
            var c = store.Get();
            Assert.NotNull(c);
            Assert.True(c!.Wavelog.Enabled);
            Assert.Equal("https://log", c.Wavelog.BaseUrl);
            Assert.Equal("7", c.Wavelog.StationProfileId);
            Assert.True(c.ClubLog.Enabled);
            Assert.Equal("a@b.com", c.ClubLog.Email);
            Assert.Equal("K1ABC", c.ClubLog.Callsign);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static HttpResponseMessage Reply(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    private sealed record CapturedRequest(string Url, string? ContentType, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;
        public List<CapturedRequest> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn) => _fn = fn;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return _fn(request, cancellationToken);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class TempDb : IDisposable
    {
        public string Path { get; }
        private readonly string _dir;
        public TempDb()
        {
            _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zeus-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            Path = System.IO.Path.Combine(_dir, "zeus-prefs.db");
        }
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
