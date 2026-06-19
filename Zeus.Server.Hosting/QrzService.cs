// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Xml.Linq;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class QrzService
{
    private const string QrzXmlApiUrl = "https://xmldata.qrz.com/xml/current/";
    private const string QrzLogbookApiUrl = "https://logbook.qrz.com/api";
    private const string Agent = "Zeus";
    private const string ServiceName = "qrz";
    private const string ApiKeyServiceName = "qrz-apikey";
    private static readonly XNamespace Ns = "http://xmldata.qrz.com";

    private readonly HttpClient _http;
    private readonly ILogger<QrzService> _log;
    private readonly CredentialStore _credStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _username;
    private string? _password;
    private string? _sessionKey;
    private DateTime _sessionExpiry;
    private QrzStation? _home;
    private bool _hasXmlSubscription;
    private string? _apiKey;

    public QrzService(IHttpClientFactory httpClientFactory, ILogger<QrzService> log, CredentialStore credStore)
    {
        _http = httpClientFactory.CreateClient("Qrz");
        _log = log;
        _credStore = credStore;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Attempt silent re-login from stored credentials
        var stored = await _credStore.GetAsync(ServiceName, ct);
        if (stored != null)
        {
            _log.LogInformation("Found stored QRZ credentials for user={User}; attempting silent login", stored.Username);
            try
            {
                await LoginAsync(stored.Username, stored.Password, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Silent QRZ login failed; clearing stored credentials");
                await _credStore.DeleteAsync(ServiceName, ct);
            }
        }

        // Load API key if stored
        var apiKeyStored = await _credStore.GetAsync(ApiKeyServiceName, ct);
        if (apiKeyStored != null)
        {
            _apiKey = apiKeyStored.Password; // Store API key in password field
            _log.LogInformation("Loaded QRZ API key from storage");
        }
    }

    public QrzStatus GetStatus() => new(
        Connected: _sessionKey != null && _home != null,
        HasXmlSubscription: _hasXmlSubscription,
        Home: _home,
        Error: null,
        HasStoredCredentials: !string.IsNullOrWhiteSpace(_username),
        HasApiKey: !string.IsNullOrWhiteSpace(_apiKey));

    public async Task<QrzStatus> LoginAsync(string username, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _username = username;
            _password = password;
            _sessionKey = null;
            _home = null;
            _hasXmlSubscription = false;

            var key = await AcquireSessionKeyAsync(ct);
            if (key == null)
            {
                return new QrzStatus(false, false, null, "QRZ login failed");
            }

            // Look up the user's own callsign to populate home station info. Success here
            // is also proof the account has an active XML subscription (the session key
            // alone doesn't guarantee lookup rights).
            try
            {
                var home = await LookupInternalAsync(username, ct);
                _home = home;
                _hasXmlSubscription = home != null;

                // Persist credentials on successful login
                await _credStore.SetAsync(ServiceName, username, password, ct);

                return new QrzStatus(
                    Connected: true,
                    HasXmlSubscription: _hasXmlSubscription,
                    Home: _home,
                    Error: _hasXmlSubscription ? null : "XML subscription required; login OK but lookups will fail",
                    HasStoredCredentials: true);
            }
            catch (QrzSubscriptionRequiredException ex)
            {
                _hasXmlSubscription = false;

                // Still persist credentials even without XML subscription
                await _credStore.SetAsync(ServiceName, username, password, ct);

                return new QrzStatus(true, false, null, ex.Message, HasStoredCredentials: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QrzStation?> LookupAsync(string callsign, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("QRZ not logged in");
            }
            return await LookupInternalAsync(callsign, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the operator's chat identity for the ZeusChat relay handshake:
    /// the home callsign plus a currently-valid QRZ session key (the relay
    /// validates the session on the WS upgrade). Returns null when the operator
    /// is not logged in or no session key can be acquired. Reuses the same gate
    /// and session-acquisition path as lookups so a refresh here doesn't race a
    /// concurrent lookup.
    /// </summary>
    public async Task<(string Callsign, string SessionKey)?> GetChatIdentityAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var callsign = _home?.Callsign;
            if (string.IsNullOrWhiteSpace(callsign)) return null;

            var key = await AcquireSessionKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) return null;

            return (callsign!, key!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _username = null;
        _password = null;
        _sessionKey = null;
        _sessionExpiry = default;
        _home = null;
        _hasXmlSubscription = false;

        // Delete stored credentials
        await _credStore.DeleteAsync(ServiceName, ct);
    }

    // Assumes _gate is held.
    private async Task<QrzStation?> LookupInternalAsync(string callsign, CancellationToken ct)
    {
        var key = await AcquireSessionKeyAsync(ct);
        if (key == null) return null;

        var url = $"{QrzXmlApiUrl}?s={key}&callsign={Uri.EscapeDataString(callsign)}";
        var xml = await _http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(xml);

        var session = doc.Descendants(Ns + "Session").FirstOrDefault()
                      ?? doc.Descendants("Session").FirstOrDefault();
        var sessionError = Get(session, "Error");
        if (!string.IsNullOrEmpty(sessionError))
        {
            if (sessionError.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                throw new QrzSubscriptionRequiredException(sessionError);
            if (sessionError.Contains("Invalid session", StringComparison.OrdinalIgnoreCase))
            {
                // Session timed out — force re-auth and retry once.
                _sessionKey = null;
                var retryKey = await AcquireSessionKeyAsync(ct);
                if (retryKey == null) return null;
                var retryUrl = $"{QrzXmlApiUrl}?s={retryKey}&callsign={Uri.EscapeDataString(callsign)}";
                xml = await _http.GetStringAsync(retryUrl, ct);
                doc = XDocument.Parse(xml);
            }
            else
            {
                _log.LogWarning("QRZ lookup error for {Callsign}: {Err}", callsign, sessionError);
                return null;
            }
        }

        var el = doc.Descendants(Ns + "Callsign").FirstOrDefault()
                 ?? doc.Descendants("Callsign").FirstOrDefault();
        if (el == null) return null;

        var lat = ParseDouble(Get(el, "lat"));
        var lon = ParseDouble(Get(el, "lon"));
        return new QrzStation(
            Callsign: (Get(el, "call") ?? callsign).ToUpperInvariant(),
            Name: Get(el, "name"),
            FirstName: Get(el, "fname"),
            Country: Get(el, "country"),
            State: Get(el, "state"),
            City: Get(el, "addr2"),
            Grid: Get(el, "grid"),
            Lat: NormalizeCoord(lat, maxAbs: 90),
            Lon: NormalizeCoord(lon, maxAbs: 180),
            Dxcc: ParseInt(Get(el, "dxcc")),
            CqZone: ParseInt(Get(el, "cqzone")),
            ItuZone: ParseInt(Get(el, "ituzone")),
            ImageUrl: Get(el, "image"),
            // Extended fields — QRZ returns these in the same <Callsign> element.
            LicenseClass: NullIfEmpty(Get(el, "class")),
            LicenseCodes: NullIfEmpty(Get(el, "codes")),
            LicenseEffectiveDate: NullIfEmpty(Get(el, "efdate")),
            LicenseExpiresDate: NullIfEmpty(Get(el, "expdate")),
            Email: NullIfEmpty(Get(el, "email")),
            AcceptsLotw: ParseQslFlag(Get(el, "lotw")),
            AcceptsEqsl: ParseQslFlag(Get(el, "eqsl")),
            AcceptsMailQsl: ParseQslFlag(Get(el, "mqsl")),
            QslManager: NullIfEmpty(Get(el, "qslmgr")),
            GmtOffset: ParseDouble(Get(el, "GMTOffset")),
            TimeZone: NullIfEmpty(Get(el, "TimeZone")),
            ObservesDst: ParseDstFlag(Get(el, "DST")),
            Born: ParseInt(Get(el, "born")));
    }

    // Assumes _gate is held.
    private async Task<string?> AcquireSessionKeyAsync(CancellationToken ct)
    {
        if (_sessionKey != null && _sessionExpiry > DateTime.UtcNow) return _sessionKey;
        if (_username == null || _password == null) return null;

        var url = $"{QrzXmlApiUrl}?username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}&agent={Agent}";
        var xml = await _http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(xml);
        var session = doc.Descendants(Ns + "Session").FirstOrDefault()
                      ?? doc.Descendants("Session").FirstOrDefault();
        if (session == null)
        {
            _log.LogWarning("QRZ login response had no Session element");
            return null;
        }

        var err = Get(session, "Error");
        if (!string.IsNullOrEmpty(err))
        {
            if (err.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                throw new QrzSubscriptionRequiredException(err);
            _log.LogWarning("QRZ login error: {Err}", err);
            return null;
        }

        _sessionKey = Get(session, "Key");
        _sessionExpiry = DateTime.UtcNow.AddHours(1);
        return _sessionKey;
    }

    private static string? Get(XElement? parent, string name)
    {
        if (parent == null) return null;
        return parent.Element(Ns + name)?.Value ?? parent.Element(name)?.Value;
    }

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // QRZ QSL flags are "1" (accepts) / "0" (does not). Absent → unknown (null).
    private static bool? ParseQslFlag(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t == "1") return true;
        if (t == "0") return false;
        return null;
    }

    // QRZ <DST> is "Y"/"N".
    private static bool? ParseDstFlag(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase);
    }

    // QRZ occasionally returns microdegree-scaled coordinates (value × 1e6). If the raw
    // value is outside the valid range but would be valid when divided by 1e6, normalize.
    private static double? NormalizeCoord(double? value, double maxAbs)
    {
        if (value is null) return null;
        var v = value.Value;
        if (Math.Abs(v) <= maxAbs) return v;
        var scaled = v / 1_000_000.0;
        return Math.Abs(scaled) <= maxAbs ? scaled : null;
    }

    public async Task SetApiKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        _apiKey = apiKey;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Store API key (using "apikey" as username for consistency)
            await _credStore.SetAsync(ApiKeyServiceName, "apikey", apiKey, ct);
            _log.LogInformation("QRZ API key stored");
        }
        else
        {
            // Delete API key
            await _credStore.DeleteAsync(ApiKeyServiceName, ct);
            _log.LogInformation("QRZ API key deleted");
        }
    }

    public async Task<QrzPublishResult> PublishLogEntryAsync(LogEntry logEntry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new QrzPublishResult(
                LogEntryId: logEntry.Id,
                Success: false,
                QrzLogId: null,
                Message: "QRZ API key not configured");
        }

        try
        {
            var adif = ConvertLogEntryToAdif(logEntry);
            var (success, logId, message) = await UploadAdifToQrzAsync(_apiKey, adif, ct);

            return new QrzPublishResult(
                LogEntryId: logEntry.Id,
                Success: success,
                QrzLogId: logId,
                Message: message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error publishing log entry {LogEntryId} to QRZ", logEntry.Id);
            return new QrzPublishResult(
                LogEntryId: logEntry.Id,
                Success: false,
                QrzLogId: null,
                Message: $"Error: {ex.Message}");
        }
    }

    private async Task<(bool Success, string? LogId, string? Message)> UploadAdifToQrzAsync(
        string apiKey, string adif, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("KEY", apiKey),
            new KeyValuePair<string, string>("ACTION", "INSERT"),
            new KeyValuePair<string, string>("ADIF", adif)
        });

        var response = await _http.PostAsync(QrzLogbookApiUrl, content, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        _log.LogInformation("QRZ logbook response: {Response}", responseText);

        // Parse response - format is: RESULT=OK&LOGID=12345 or RESULT=FAIL&REASON=message
        var parts = responseText.Split('&')
            .Select(p => p.Split('='))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => System.Net.WebUtility.UrlDecode(p[1]));

        parts.TryGetValue("RESULT", out var result);
        parts.TryGetValue("LOGID", out var logId);
        parts.TryGetValue("REASON", out var reason);

        // OK = new record inserted, REPLACE = duplicate updated
        if (result == "OK" || result == "REPLACE")
        {
            var message = result == "REPLACE" ? "QSO already exists (updated)" : "QSO uploaded successfully";
            return (true, logId, message);
        }

        // Handle duplicate errors as success
        if (reason != null && (
            reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("dupe", StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogDebug("QRZ duplicate detected, treating as success: {Reason}", reason);
            return (true, logId, "QSO already exists in QRZ");
        }

        return (false, null, reason ?? "Unknown error");
    }

    /// <summary>Internal so AdifUtcTimezoneTests can verify the QRZ upload
    /// path emits UTC clock values regardless of the incoming entry's
    /// <see cref="DateTime.Kind"/>. The method is otherwise a private
    /// detail of the publish path.</summary>
    internal static string ConvertLogEntryToAdif(LogEntry entry)
    {
        var sb = new System.Text.StringBuilder();

        // Required fields
        AppendAdifField(sb, "CALL", entry.Callsign);
        // .ToUniversalTime() guards against the LiteDB round-trip stripping
        // the UTC kind off QsoDateTimeUtc — see LogService.AppendAdifRecord
        // for the full story. Without it QRZ.com receives local-clock times
        // and stamps the QSOs at the operator's wall-clock hour, breaking
        // award credit and DXCC matching for anyone outside UTC.
        AppendAdifField(sb, "QSO_DATE", entry.QsoDateTimeUtc.ToUniversalTime().ToString("yyyyMMdd"));
        AppendAdifField(sb, "TIME_ON", entry.QsoDateTimeUtc.ToUniversalTime().ToString("HHmmss"));
        AppendAdifField(sb, "FREQ", entry.FrequencyMhz.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        AppendAdifField(sb, "BAND", entry.Band);
        AppendAdifField(sb, "MODE", entry.Mode);
        AppendAdifField(sb, "RST_SENT", entry.RstSent);
        AppendAdifField(sb, "RST_RCVD", entry.RstRcvd);

        // Optional fields
        if (!string.IsNullOrEmpty(entry.Name))
            AppendAdifField(sb, "NAME", entry.Name);
        if (!string.IsNullOrEmpty(entry.Grid))
            AppendAdifField(sb, "GRIDSQUARE", entry.Grid);
        if (!string.IsNullOrEmpty(entry.Country))
            AppendAdifField(sb, "COUNTRY", entry.Country);
        if (entry.Dxcc.HasValue)
            AppendAdifField(sb, "DXCC", entry.Dxcc.Value.ToString());
        if (entry.CqZone.HasValue)
            AppendAdifField(sb, "CQZ", entry.CqZone.Value.ToString());
        if (entry.ItuZone.HasValue)
            AppendAdifField(sb, "ITUZ", entry.ItuZone.Value.ToString());
        if (!string.IsNullOrEmpty(entry.State))
            AppendAdifField(sb, "STATE", entry.State);
        if (!string.IsNullOrEmpty(entry.Comment))
            AppendAdifField(sb, "COMMENT", entry.Comment);

        sb.Append("<EOR>");
        return sb.ToString();
    }

    private static void AppendAdifField(System.Text.StringBuilder sb, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append($"<{fieldName}:{value.Length}>{value} ");
    }
}

public sealed class QrzSubscriptionRequiredException : Exception
{
    public QrzSubscriptionRequiredException(string message) : base(message) { }
}
