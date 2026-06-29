// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.CloudLog;

/// <summary>
/// Owns the live config + secrets for the HTTP cloud-log uploaders and fans a
/// logged QSO out to every enabled provider. Like the WSJT-X broadcaster this is
/// SEND-ONLY and applies config changes live (no listener, no restart).
///
/// Egress is OFF by default and additionally no-ops per provider when its
/// credentials are missing. Each provider send is independent and failure
/// tolerant — one provider failing (or a slow/blocked host) never blocks the
/// operator's log confirmation or the other providers.
/// </summary>
public sealed class CloudLogService
{
    private readonly ILogger<CloudLogService> _log;
    private readonly CloudLogConfigStore _store;
    private readonly CredentialStore _creds;
    private readonly WavelogClient _wavelog;
    private readonly ClubLogClient _clubLog;
    private readonly object _sync = new();
    private CloudLogConfig _config;

    public CloudLogService(
        ILogger<CloudLogService> log,
        CloudLogConfigStore store,
        CredentialStore creds,
        WavelogClient wavelog,
        ClubLogClient clubLog)
    {
        _log = log;
        _store = store;
        _creds = creds;
        _wavelog = wavelog;
        _clubLog = clubLog;
        _config = _store.Get() ?? new CloudLogConfig();
    }

    public CloudLogConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public async Task<CloudLogStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var c = GetConfig();
        bool wavelogKey = await HasCredAsync(WavelogClient.ServiceName, ct).ConfigureAwait(false);
        bool clubLogPw = await HasCredAsync(ClubLogClient.PasswordServiceName, ct).ConfigureAwait(false);
        bool clubLogApi = await HasCredAsync(ClubLogClient.ApiKeyServiceName, ct).ConfigureAwait(false);
        return new CloudLogStatus(
            Wavelog: new WavelogStatus(c.Wavelog.Enabled, c.Wavelog.BaseUrl, c.Wavelog.StationProfileId, wavelogKey),
            ClubLog: new ClubLogStatus(c.ClubLog.Enabled, c.ClubLog.Email, c.ClubLog.Callsign, clubLogPw, clubLogApi));
    }

    public CloudLogConfig SetConfig(CloudLogConfig config)
    {
        var normalized = new CloudLogConfig(
            new WavelogConfig(
                Enabled: config.Wavelog.Enabled,
                BaseUrl: (config.Wavelog.BaseUrl ?? "").Trim(),
                StationProfileId: (config.Wavelog.StationProfileId ?? "").Trim()),
            new ClubLogConfig(
                Enabled: config.ClubLog.Enabled,
                Email: (config.ClubLog.Email ?? "").Trim(),
                Callsign: (config.ClubLog.Callsign ?? "").Trim().ToUpperInvariant()));

        lock (_sync) _config = normalized;
        try { _store.Set(normalized); }
        catch (Exception ex) { _log.LogWarning(ex, "cloudlog.config.persist failed"); }

        _log.LogInformation(
            "cloudlog.config.updated wavelog={WlEn} clublog={ClEn}",
            normalized.Wavelog.Enabled, normalized.ClubLog.Enabled);
        return normalized;
    }

    public Task SetWavelogApiKeyAsync(string? apiKey, CancellationToken ct = default) =>
        SetOrDeleteAsync(WavelogClient.ServiceName, "apikey", apiKey, ct);

    public async Task SetClubLogCredentialsAsync(string? password, string? apiKey, CancellationToken ct = default)
    {
        var email = GetConfig().ClubLog.Email;
        // Per-secret contract, matching the Wavelog path: a null field leaves
        // that secret unchanged, an empty string clears it, a non-empty string
        // replaces it. Club Log needs BOTH the application password and the API
        // key, so a routine single-secret rotation must NOT clobber the other —
        // guard each call independently rather than running both unconditionally.
        if (password is not null)
            await SetOrDeleteAsync(ClubLogClient.PasswordServiceName, email, password, ct).ConfigureAwait(false);
        if (apiKey is not null)
            await SetOrDeleteAsync(ClubLogClient.ApiKeyServiceName, email, apiKey, ct).ConfigureAwait(false);
    }

    /// <summary>Fan a logged QSO out to every enabled provider. Never throws.
    /// No-op when nothing is enabled.</summary>
    public async Task PublishAsync(LogEntry entry, CancellationToken ct = default)
    {
        var cfg = GetConfig();
        if (!cfg.Wavelog.Enabled && !cfg.ClubLog.Enabled) return;

        // One ADIF record reused across providers (ends in <EOR>, which Club Log
        // requires). Identical to the QRZ publish path.
        string adif;
        try { adif = QrzService.ConvertLogEntryToAdif(entry); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cloudlog.adif build failed call={Call}", entry.Callsign);
            return;
        }

        var results = await PublishToAllAsync(cfg, adif, ct).ConfigureAwait(false);

        // A Club Log 403 is a hard stop: disable it so we never keep hitting a
        // forbidden auth and risk an IP block.
        if (results.Any(r => r is { Provider: "clublog", HardStop: true }))
        {
            _log.LogWarning("cloudlog: disabling Club Log after a hard-stop (403) response");
            SetConfig(cfg with { ClubLog = cfg.ClubLog with { Enabled = false } });
        }

        foreach (var r in results)
            _log.LogInformation(
                "cloudlog.publish provider={Provider} ok={Ok} call={Call} msg={Msg}",
                r.Provider, r.Success, entry.Callsign, r.Message);
    }

    /// <summary>Publish a single entry on demand (batch endpoint). Same fan-out
    /// as <see cref="PublishAsync"/> but returns the per-provider results.</summary>
    public async Task<IReadOnlyList<CloudLogResult>> PublishOnceAsync(LogEntry entry, CancellationToken ct = default)
    {
        var cfg = GetConfig();
        string adif;
        try { adif = QrzService.ConvertLogEntryToAdif(entry); }
        catch (Exception ex) { return new[] { CloudLogResult.Fail("cloudlog", ex.Message) }; }
        return await PublishToAllAsync(cfg, adif, ct).ConfigureAwait(false);
    }

    private async Task<List<CloudLogResult>> PublishToAllAsync(
        CloudLogConfig cfg, string adif, CancellationToken ct)
    {
        var results = new List<CloudLogResult>();

        if (cfg.Wavelog.Enabled)
        {
            var key = await GetCredAsync(WavelogClient.ServiceName, ct).ConfigureAwait(false);
            results.Add(await _wavelog.PublishAsync(cfg.Wavelog, key ?? "", adif, ct).ConfigureAwait(false));
        }

        if (cfg.ClubLog.Enabled)
        {
            var pw = await GetCredAsync(ClubLogClient.PasswordServiceName, ct).ConfigureAwait(false);
            var api = await GetCredAsync(ClubLogClient.ApiKeyServiceName, ct).ConfigureAwait(false);
            results.Add(await _clubLog.PublishAsync(cfg.ClubLog, pw ?? "", api ?? "", adif, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<string?> GetCredAsync(string service, CancellationToken ct)
    {
        var c = await _creds.GetAsync(service, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(c?.Password) ? null : c.Password;
    }

    private async Task<bool> HasCredAsync(string service, CancellationToken ct) =>
        await GetCredAsync(service, ct).ConfigureAwait(false) is not null;

    private async Task SetOrDeleteAsync(string service, string username, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret))
            await _creds.DeleteAsync(service, ct).ConfigureAwait(false);
        else
            await _creds.SetAsync(service, string.IsNullOrWhiteSpace(username) ? "secret" : username, secret, ct)
                .ConfigureAwait(false);
    }
}

// Status DTOs — server-side only (never on the SignalR/StateDto wire). Secrets
// are reported as presence booleans, never echoed.
public sealed record CloudLogStatus(WavelogStatus Wavelog, ClubLogStatus ClubLog);

public sealed record WavelogStatus(bool Enabled, string BaseUrl, string StationProfileId, bool HasApiKey);

public sealed record ClubLogStatus(bool Enabled, string Email, string Callsign, bool HasPassword, bool HasApiKey);

// Write-only secret setter. Per-secret semantics applied uniformly across
// Wavelog and both Club Log secrets: a null/omitted field leaves that secret
// unchanged, an empty string clears it, a non-empty string replaces it. The
// endpoint (see ZeusEndpoints) only calls a provider when at least one of its
// fields is non-null; the service then guards each individual secret.
public sealed record CloudLogCredentialsRequest(
    string? WavelogApiKey = null,
    string? ClubLogPassword = null,
    string? ClubLogApiKey = null);
