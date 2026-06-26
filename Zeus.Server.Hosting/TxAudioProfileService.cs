// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Plugins.Host;

namespace Zeus.Server;

/// <summary>
/// Orchestrates the unified "TX Audio Profile" system — the single
/// operator-named macro that snapshots and recalls the ENTIRE TX-audio shaping
/// state. It REPLACES both the named audio-suite TX profiles
/// (<see cref="AudioProfileService"/>, retained only as VST-capture machinery)
/// and the fixed 3-up TX station profiles.
///
/// <para><b>Single source of truth:</b> a profile is a write-through macro over
/// the existing per-setting stores (DspSettingsStore / RadioStateStore /
/// TxFidelityPolicyStore / the suite seams / PluginSettingsStore). Capture
/// reads the live state; apply writes it back through the same live Set* paths.
/// The profile store never competes with those stores for live authority —
/// PureSignal and non-profile users are untouched.</para>
///
/// <para><b>Startup:</b> registered as an <see cref="IHostedService"/> AFTER
/// <see cref="AudioProcessingModeService"/> so by the time
/// <see cref="StartAsync"/> runs the engine route has been replayed. On startup
/// it (1) seeds the three starter profiles idempotently if the collection is
/// empty and (2) replays the persisted last-loaded profile's chain/plugin state
/// in the background (never blocking startup). The scalar overlay onto the
/// radio's _state happens inside RadioService construction so the radio comes
/// up already on the last-loaded profile.</para>
/// </summary>
public sealed class TxAudioProfileService : IHostedService
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    private readonly TxAudioProfileStore _store;
    private readonly RadioService _radio;
    private readonly ChainOrderService _chainOrder;
    private readonly AudioChainMasterBypassService _masterBypass;
    private readonly AudioProcessingModeService _mode;
    private readonly TxFidelityPolicyStore _fidelity;
    private readonly PluginManager _manager;
    private readonly PluginSettingsStore _pluginSettings;
    private readonly ILogger<TxAudioProfileService> _log;

    public TxAudioProfileService(
        TxAudioProfileStore store,
        RadioService radio,
        ChainOrderService chainOrder,
        AudioChainMasterBypassService masterBypass,
        AudioProcessingModeService mode,
        TxFidelityPolicyStore fidelity,
        PluginManager manager,
        PluginSettingsStore pluginSettings,
        ILogger<TxAudioProfileService> log)
    {
        _store = store;
        _radio = radio;
        _chainOrder = chainOrder;
        _masterBypass = masterBypass;
        _mode = mode;
        _fidelity = fidelity;
        _manager = manager;
        _pluginSettings = pluginSettings;
        _log = log;
    }

    public IReadOnlyList<TxAudioProfileDto> List() => _store.GetAll();

    public TxAudioProfileDto? Get(string id) => _store.Get(id);

    public string? LastLoadedId => _store.GetLastLoadedId();

    public void SetLastLoadedId(string? id) => _store.SetLastLoadedId(id);

    public bool Delete(string id)
    {
        var removed = _store.Delete(id);
        if (removed)
            _log.LogInformation("TX audio profile '{Id}' deleted", TxAudioProfileStore.NormalizeId(id));
        return removed;
    }

    /// <summary>The on-disk folder every profile is mirrored into as JSON.</summary>
    public string ProfileFolder => _store.ProfileFolder;

    // Indented camelCase export — matches the folder-mirror format so a
    // downloaded file is byte-for-byte the same shape as the on-disk mirror.
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Import a profile from a JSON document (an uploaded/shared file). The
    /// profile is ADDED to the collection — it is never auto-applied, and it
    /// never clobbers an existing profile: if the derived slug is taken, the
    /// display name is bumped (\"Voice\" -> \"Voice 2\") until the id is free.
    /// Partial / hand-edited files are tolerated (missing collections default to
    /// empty so the apply path can't dereference a null). Throws
    /// <see cref="ArgumentException"/> on unparseable input.
    /// </summary>
    public TxAudioProfileDto ImportProfile(string json, string? fallbackName = null)
    {
        var parsed = TxAudioProfileStore.ParseJson(json)
            ?? throw new ArgumentException("File is not a valid TX audio profile (could not parse JSON).");

        var clean = SanitizeForCurrentInstall(parsed, fallbackName);

        var baseName = !string.IsNullOrWhiteSpace(clean.Name) ? clean.Name!.Trim()
            : !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName!.Trim()
            : "imported profile";

        var name = baseName;
        var id = Slugify(name);
        for (int n = 2; _store.Get(id) is not null; n++)
        {
            name = $"{baseName} {n}";
            id = Slugify(name);
        }

        var saved = _store.Upsert(SanitizeForCurrentInstall(clean with { Id = id, Name = name }, name));
        _log.LogInformation("TX audio profile imported as '{Name}' (id={Id})", saved.Name, saved.Id);
        return saved;
    }

    /// <summary>
    /// Serialize a saved profile to downloadable JSON bytes (indented). Returns
    /// null when no such profile. The byte shape matches the folder mirror.
    /// </summary>
    public (byte[] Bytes, string FileName)? ExportProfile(string id)
    {
        var profile = _store.Get(id);
        if (profile is null) return null;
        var json = JsonSerializer.Serialize(profile, ExportJsonOptions);
        return (Encoding.UTF8.GetBytes(json), profile.Id + ".json");
    }

    /// <summary>
    /// Snapshot the live TX-audio state under <paramref name="name"/>. The Id is
    /// derived from the name (slugified); re-saving the same name/id overwrites.
    /// Captures mic/leveler scalars, the whole TxLeveling + CFC configs, TX
    /// bandpass magnitudes, processing route, suite chain shape, every plugin's
    /// settings (VST blobs + native collection dumps), and the fidelity target.
    /// </summary>
    public async Task<TxAudioProfileDto> SaveCurrentAsync(string name, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Profile name is required", nameof(name));
        var id = Slugify(trimmed);

        var snap = _radio.Snapshot();
        var order = _chainOrder.CurrentOrder.ToList();
        var parked = _chainOrder.ParkedIds.ToList();

        // VST states: pull a fresh capture from the engine (empty when the engine
        // isn't active / no VST plugins). Native states: dump each plugin's whole
        // LiteDB collection. Keyed by id so a plugin's VST blob and native dump
        // never collide.
        var vstStates = new Dictionary<string, string>(
            await _mode.CaptureChainStatesAsync(CaptureTimeout, ct).ConfigureAwait(false),
            StringComparer.Ordinal);

        var nativeStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var pid in order.Concat(parked).Distinct(StringComparer.Ordinal))
        {
            if (IsVstPlugin(pid)) continue; // VST voicing lives in the engine blob
            var dump = _pluginSettings.DumpCollection(pid);
            if (dump.Count > 0)
                nativeStates[pid] = new Dictionary<string, string>(dump, StringComparer.Ordinal);
        }

        int lowCut = Math.Min(Math.Abs(snap.TxFilterLowHz), Math.Abs(snap.TxFilterHighHz));
        int highCut = Math.Max(Math.Abs(snap.TxFilterLowHz), Math.Abs(snap.TxFilterHighHz));

        var profile = new TxAudioProfileDto(
            Id: id,
            Name: trimmed,
            MicGainDb: snap.MicGainDb,
            LevelerMaxGainDb: snap.LevelerMaxGainDb,
            TxLeveling: snap.TxLeveling ?? new TxLevelingConfig(),
            CfcConfig: snap.Cfc ?? CfcConfig.Default,
            LowCutHz: lowCut,
            HighCutHz: highCut,
            ProcessingMode: _mode.Mode == AudioProcessingMode.Vst ? "vst" : "native",
            MasterBypass: _masterBypass.IsBypassed,
            ChainOrder: order,
            ChainParked: parked,
            VstPluginStates: vstStates,
            NativePluginStates: nativeStates,
            TargetSpectralDensity: _fidelity.Get().TargetSpectralDensity,
            CreatedUtc: DateTime.UtcNow,
            UpdatedUtc: DateTime.UtcNow);

        var saved = _store.Upsert(profile);
        _log.LogInformation(
            "TX audio profile '{Name}' (id={Id}) saved (mode={Mode}, {Active} active, {Parked} parked, {Vst} VST states, {Native} native dumps, density={Density})",
            saved.Name, saved.Id, saved.ProcessingMode, saved.ChainOrder.Count, saved.ChainParked.Count,
            saved.VstPluginStates.Count, saved.NativePluginStates.Count, saved.TargetSpectralDensity);
        return saved;
    }

    /// <summary>
    /// Apply a saved profile by id to the LIVE state. Writes through the same
    /// live Set* paths the operator's controls use, so persistence and WDSP push
    /// happen exactly as a manual edit would. PureSignal is never touched (PS
    /// fields are not in the DTO). Records the applied id as last-loaded.
    /// Returns null if no such profile.
    /// </summary>
    public async Task<TxAudioProfileDto?> ApplyAsync(string id, CancellationToken ct = default)
    {
        var stored = _store.Get(id);
        if (stored is null) return null;
        var profile = SanitizeForCurrentInstall(stored, stored.Name);

        ApplyScalars(profile);

        // Plugin states FIRST (arm the saved voicing), then route, then membership.
        // Native dumps are written to each plugin's collection BEFORE the chain
        // reslot so InitializeAsync re-reads them when the chain rebuilds.
        foreach (var kv in profile.NativePluginStates)
            _pluginSettings.RestoreCollection(kv.Key, kv.Value);

        _mode.SetPluginStates(profile.VstPluginStates);
        var targetMode = string.Equals(profile.ProcessingMode, "vst", StringComparison.OrdinalIgnoreCase)
            ? AudioProcessingMode.Vst : AudioProcessingMode.Native;
        await _mode.SetModeAsync(targetMode, ct).ConfigureAwait(false);
        // ApplyMembershipAndOrder fires OrderChanged -> AudioChain rebuild ->
        // native plugins re-read PluginSettingsStore (restored above), and the
        // VST engine reloads its chain with the armed states.
        _chainOrder.ApplyMembershipAndOrder(profile.ChainOrder, profile.ChainParked);
        _masterBypass.SetMasterBypassed(profile.MasterBypass);

        _store.SetLastLoadedId(profile.Id);

        _log.LogInformation(
            "TX audio profile '{Name}' (id={Id}) applied (mode={Mode}, {Vst} VST states, {Native} native dumps restored)",
            profile.Name, profile.Id, profile.ProcessingMode, profile.VstPluginStates.Count, profile.NativePluginStates.Count);
        return profile;
    }

    /// <summary>
    /// Write a profile's scalar/config fields through the live Set* paths. Shared
    /// by ApplyAsync. Never touches PureSignal, MOX, drive, or attenuation.
    /// </summary>
    private void ApplyScalars(TxAudioProfileDto profile)
    {
        _radio.SetTxMicGain(profile.MicGainDb);
        _radio.SetTxLevelerMaxGain(profile.LevelerMaxGainDb);
        _radio.SetTxLeveling(profile.TxLeveling);
        if (profile.CfcConfig is not null)
            _radio.SetCfc(new CfcSetRequest(profile.CfcConfig));
        // Operator-typed positive magnitudes; SetTxFilter re-signs per mode-family
        // and updates the per-mode-family TX filter memory.
        _radio.SetTxFilter(profile.LowCutHz, profile.HighCutHz);
        // Fidelity target: store the raw density + point the fidelity policy at
        // this profile id so existing diagnostics resolve.
        _fidelity.Set(new TxFidelityPolicyDto(profile.Id, profile.TargetSpectralDensity));
    }

    private bool IsVstPlugin(string pluginId)
    {
        if (LooksLikeVstPluginId(pluginId)) return true;
        var p = _manager.Find(pluginId);
        return !string.IsNullOrEmpty(p?.Loaded.Manifest.Audio?.Vst3Path);
    }

    private TxAudioProfileDto SanitizeForCurrentInstall(TxAudioProfileDto profile, string? fallbackName)
    {
        var clean = TxAudioProfileStore.Sanitize(profile, fallbackName: fallbackName);
        var nativeMode = !string.Equals(clean.ProcessingMode, "vst", StringComparison.OrdinalIgnoreCase);

        var order = new List<string>(clean.ChainOrder.Count);
        var seenOrder = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in clean.ChainOrder)
        {
            if (nativeMode && IsVstPlugin(id)) continue;
            if (seenOrder.Add(id)) order.Add(id);
        }

        var parked = new List<string>(clean.ChainParked.Count);
        var seenParked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in clean.ChainParked)
        {
            if (seenOrder.Contains(id)) continue;
            if (seenParked.Add(id)) parked.Add(id);
        }

        var nativeStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (id, state) in clean.NativePluginStates)
        {
            if (nativeMode && IsVstPlugin(id)) continue;
            nativeStates[id] = state;
        }

        return clean with
        {
            ChainOrder = order,
            ChainParked = parked,
            VstPluginStates = nativeMode
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : clean.VstPluginStates,
            NativePluginStates = nativeStates,
        };
    }

    private static bool LooksLikeVstPluginId(string pluginId) =>
        pluginId.Contains(".vst.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".rxvst.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".au.", StringComparison.OrdinalIgnoreCase)
        || pluginId.Contains(".rxau.", StringComparison.OrdinalIgnoreCase);

    private static string Slugify(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        bool lastDash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "profile" : slug;
    }

    // ---------------- Startup: seed + replay last-loaded ----------------

    public Task StartAsync(CancellationToken ct)
    {
        try { SeedStartersIfEmpty(); }
        catch (Exception ex) { _log.LogWarning(ex, "TX audio profile seed threw"); }

        var lastId = _store.GetLastLoadedId();
        if (!string.IsNullOrWhiteSpace(lastId) && _store.Get(lastId) is not null)
        {
            // Replay chain/plugin-state in the background — never block startup,
            // and let AudioProcessingModeService.StartAsync bring the engine up
            // first. The scalar overlay already happened in RadioService ctor.
            _ = ReplayLastLoadedAsync(lastId!, ct);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ReplayLastLoadedAsync(string id, CancellationToken ct)
    {
        try
        {
            // Give AudioProcessingModeService.StartAsync a beat to begin engine
            // activation (it returns immediately and activates in the background);
            // ApplyAsync's SetModeAsync is idempotent regardless of ordering.
            await Task.Yield();
            var applied = await ApplyAsync(id, ct).ConfigureAwait(false);
            if (applied is not null)
                _log.LogInformation("Startup: replayed last-loaded TX audio profile '{Id}'", id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup replay of TX audio profile '{Id}' threw", id);
        }
    }

    /// <summary>
    /// Seed the three starter profiles (studio-ssb / essb-wide / dx-punch) as
    /// named, editable TX Audio Profiles if the collection is empty. Idempotent:
    /// only runs on a truly empty collection so an operator who deletes a starter
    /// doesn't see it resurrected.
    /// </summary>
    private void SeedStartersIfEmpty()
    {
        if (_store.Any()) return;
        foreach (var seed in TxAudioProfileSeeds.Starters)
            _store.Upsert(seed);
        _log.LogInformation("Seeded {Count} starter TX audio profiles", TxAudioProfileSeeds.Starters.Count);
    }
}
