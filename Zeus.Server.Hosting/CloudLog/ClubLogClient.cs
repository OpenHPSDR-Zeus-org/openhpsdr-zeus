// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;

namespace Zeus.Server.CloudLog;

/// <summary>
/// Per-QSO realtime uploader for Club Log. SEND-ONLY: POSTs exactly one ADIF
/// record to the realtime endpoint and never reads the remote log back.
///
/// Wire contract (clublog.freshdesk.com art. 54906):
///   POST https://clublog.org/realtime.php
///   Content-Type: application/x-www-form-urlencoded
///
/// Host note: the legacy secure.clublog.org / www.clublog.org hosts were
/// retired (DNS + certificates removed in December 2022). Per Club Log's
/// "Using the right URLs" article (freshdesk art. 3000118796) the correct host
/// for all web/iFrame/API requests — realtime.php included — is clublog.org.
///   params: email (account email, NOT callsign), password (Club Log Application
///           Password), callsign (target log — must belong to email), adif (ONE
///           record ending in &lt;EOR&gt;), api (API key from the helpdesk).
///
/// Response mapping:
///   200 "QSO OK" / "QSO Modified" / "QSO Duplicate"  -> success
///   400 -> rejected (bad ADIF)            -> retryable failure
///   403 -> auth failure                   -> HARD STOP (IP-block risk; stop sending)
///   500 -> server error                   -> retryable failure
///
/// This endpoint is throttled and realtime-only. NEVER loop putlogs.php batch
/// uploads through here.
/// </summary>
public sealed class ClubLogClient
{
    public const string PasswordServiceName = "clublog-password";
    public const string ApiKeyServiceName = "clublog-apikey";
    public const string RealtimeUrl = "https://clublog.org/realtime.php";

    private readonly HttpClient _http;
    private readonly ILogger<ClubLogClient> _log;

    public ClubLogClient(IHttpClientFactory httpClientFactory, ILogger<ClubLogClient> log)
    {
        _http = httpClientFactory.CreateClient("ClubLog");
        _log = log;
    }

    public async Task<CloudLogResult> PublishAsync(
        ClubLogConfig cfg, string password, string apiKey, string adifRecord,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Email))
            return CloudLogResult.Skipped("clublog", "account email not configured");
        if (string.IsNullOrWhiteSpace(cfg.Callsign))
            return CloudLogResult.Skipped("clublog", "callsign not configured");
        if (string.IsNullOrWhiteSpace(password))
            return CloudLogResult.Skipped("clublog", "application password not configured");
        if (string.IsNullOrWhiteSpace(apiKey))
            return CloudLogResult.Skipped("clublog", "API key not configured");

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", cfg.Email.Trim()),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("callsign", cfg.Callsign.Trim().ToUpperInvariant()),
            new KeyValuePair<string, string>("adif", adifRecord),
            new KeyValuePair<string, string>("api", apiKey),
        });

        try
        {
            using var resp = await _http.PostAsync(RealtimeUrl, form, ct).ConfigureAwait(false);
            var body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            return MapResponse(resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "clublog publish failed");
            return CloudLogResult.Fail("clublog", ex.Message);
        }
    }

    internal CloudLogResult MapResponse(HttpStatusCode status, string body)
    {
        switch ((int)status)
        {
            case 200:
                // "QSO OK" / "QSO Modified" / "QSO Duplicate" — all success.
                return CloudLogResult.Ok("clublog", id: null,
                    message: string.IsNullOrWhiteSpace(body) ? "QSO accepted" : body);
            case 403:
                _log.LogWarning(
                    "clublog 403 forbidden (auth fail) — disabling to avoid IP block: {Body}", body);
                return CloudLogResult.Fail("clublog",
                    $"authentication failed (403): {body} — Club Log disabled to avoid an IP block; check email/password/API key.",
                    hardStop: true);
            case 400:
                return CloudLogResult.Fail("clublog", $"rejected (400): {body}");
            default:
                // 500 and anything else — retryable, do not hard-stop.
                return CloudLogResult.Fail("clublog", $"HTTP {(int)status}: {body}");
        }
    }
}
