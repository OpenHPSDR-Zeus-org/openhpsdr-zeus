// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// The operator's Audio Suite processing route: the native in-process plugin
/// chain (Brian's Audio Suite, the untouched default) or the out-of-process VST
/// engine. The two are mutually exclusive.
/// </summary>
public enum AudioProcessingMode
{
    /// <summary>Native in-process <c>AudioChain</c>. Default — byte-identical to no VST mode.</summary>
    Native = 0,
    /// <summary>Out-of-process VST engine via the shared-memory bridge (opt-in).</summary>
    Vst = 1,
}

/// <summary>
/// Persists the operator's Audio Suite processing mode across restarts. A
/// dedicated single-row collection ("audio_processing_mode") sharing
/// zeus-prefs.db, mirroring <see cref="AudioChainSettingsStore"/> — kept
/// separate from the master-bypass row on purpose so the two settings never
/// clobber one another's defaults when only one has ever been written.
///
/// <para>First-run semantics: <see cref="GetMode"/> returns <c>null</c> when no
/// row exists. The caller (<c>AudioProcessingModeService</c>) treats null as
/// <see cref="AudioProcessingMode.Native"/> so a brand-new operator stays on the
/// native chain until they explicitly opt into VST.</para>
/// </summary>
public sealed class AudioProcessingModeStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioProcessingModeEntry> _state;
    private readonly ILogger<AudioProcessingModeStore> _log;
    private readonly object _sync = new();

    public AudioProcessingModeStore(ILogger<AudioProcessingModeStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _state = _db.GetCollection<AudioProcessingModeEntry>("audio_processing_mode");

        _log.LogInformation("AudioProcessingModeStore initialized at {Path}", dbPath);
    }

    /// <summary>Persisted mode, or null on first run (no row yet → caller defaults to Native).</summary>
    public AudioProcessingMode? GetMode()
    {
        lock (_sync)
        {
            var entry = _state.FindAll().FirstOrDefault();
            return entry?.Mode;
        }
    }

    public void SetMode(AudioProcessingMode mode)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new AudioProcessingModeEntry { Mode = mode, UpdatedUtc = nowUtc });
            }
            else
            {
                existing.Mode = mode;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class AudioProcessingModeEntry
{
    public int Id { get; set; }
    public AudioProcessingMode Mode { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
