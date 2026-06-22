// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host;

/// <summary>
/// Scans an operator-chosen directory for VST3 plugins and registers
/// each one as an installed Zeus plugin so it appears in the Audio Suite
/// chain. Each discovered <c>.vst3</c> becomes a generated plugin
/// package under the plugin root:
///
/// <code>
///   &lt;plugin-root&gt;/&lt;id&gt;/
///     ├── plugin.json                 (synthesized manifest)
///     ├── Zeus.Plugins.VstHostStub.dll (no-op managed entrypoint)
///     └── vst3/&lt;name&gt;.vst3         (copy of the discovered VST)
/// </code>
///
/// The stub assembly satisfies the loader's "every package ships an
/// IZeusPlugin assembly" rule WITHOUT touching the core loader; the
/// audio comes entirely from <c>audio.vst3Path</c>, which
/// AudioPluginBridge wraps in a VstHostAudioPlugin. After writing the
/// package the service calls <see cref="PluginManager.ActivateAsync"/>,
/// which attaches it to the chain exactly like a normally-installed
/// plugin.
///
/// <para>v1 scope: the generated manifest has no <c>ui</c> module, so the
/// frontend renders a synthetic generic panel for it. Real VST names and
/// parameter/editor surfaces need native-bridge introspection (a future
/// ABI bump); for now the display name is derived from the filename.</para>
/// </summary>
public sealed class VstDirectoryScanService
{
    private const string StubResourceName = "Zeus.Plugins.VstHostStub.dll";
    private const string StubAssemblyFile = "Zeus.Plugins.VstHostStub.dll";
    private const string IdPrefix = "com.openhpsdr.zeus.vst.";
    private const string RxIdPrefix = "com.openhpsdr.zeus.rxvst.";
    private const int MaxScanDepth = 5;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly PluginManager _manager;
    private readonly string _pluginRoot;
    private readonly ILogger<VstDirectoryScanService> _log;
    private readonly VstEngineController? _engine;

    public VstDirectoryScanService(
        PluginManager manager,
        string pluginRoot,
        ILogger<VstDirectoryScanService> log,
        VstEngineController? engine = null)
    {
        _manager = manager;
        _pluginRoot = pluginRoot;
        _log = log;
        _engine = engine;
    }

    public sealed record ScannedVst(string Id, string Name, string Vst3Source);
    public sealed record ScanError(string Vst3Source, string Message);
    public sealed record ScanResult(
        string Directory,
        IReadOnlyList<ScannedVst> Registered,
        IReadOnlyList<ScannedVst> Skipped,
        IReadOnlyList<ScanError> Errors);
    private sealed record AudioRouteRegistration(string Slot, string IdPrefix, string? DisplaySuffix);

    /// <summary>
    /// Scan <paramref name="directory"/> for VST3 plugins and register
    /// each. The value may be either a directory or one exact <c>.vst3</c>
    /// file/bundle. Already-registered VSTs (same generated id) are skipped,
    /// not re-installed. Throws <see cref="DirectoryNotFoundException"/> if
    /// the path doesn't exist.
    /// </summary>
    public async Task<ScanResult> ScanAsync(string directory, CancellationToken ct) =>
        await ScanAsync(directory, route: null, ct).ConfigureAwait(false);

    public async Task<ScanResult> ScanAsync(string directory, string? route, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("directory is required", nameof(directory));
        var exactEntry = ResolveExactVst3Entry(directory);
        if (exactEntry is null && !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"directory or .vst3 path not found: {directory}");
        var routes = ResolveRoutes(route);

        var root = _pluginRoot;
        Directory.CreateDirectory(root);

        if (exactEntry is not null)
            return await ScanViaFileWalkAsync(directory, root, routes, ct, [exactEntry]).ConfigureAwait(false);

        // Engine-driven enumeration when the out-of-process engine is live: it
        // uses JUCE's scanner, which expands "shell" VST3s (e.g. Waves WaveShell)
        // into their hosted sub-plugins — each with a stable uid we pass back in
        // load_chain to load that exact plugin. It also reports only plugins that
        // actually load (and blacklists crashers), so incompatible files never
        // enter the rack. Falls back to a static file walk when the engine is off
        // (Native mode), which can only see whole-file single plugins.
        if (_engine is { IsActive: true })
            return await ScanViaEngineAsync(directory, root, routes, ct).ConfigureAwait(false);

        return await ScanViaFileWalkAsync(directory, root, routes, ct).ConfigureAwait(false);
    }

    // ── Engine-driven enumeration (expands Waves-style shells) ───────────────────
    private async Task<ScanResult> ScanViaEngineAsync(
        string directory,
        string root,
        IReadOnlyList<AudioRouteRegistration> routes,
        CancellationToken ct)
    {
        var registered = new List<ScannedVst>();
        var skipped = new List<ScannedVst>();
        var errors = new List<ScanError>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var activeIds = new HashSet<string>(
            _manager.Active.Select(p => p.Loaded.Manifest.Id), StringComparer.Ordinal);
        // Already-registered (file,uid,slot) tuples so a re-scan skips the same
        // route while still allowing independent TX and RX instances of one VST.
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _manager.Active)
        {
            var a = p.Loaded.Manifest.Audio;
            if (a?.Vst3Path is { Length: > 0 } f)
                activeKeys.Add(EngineKey(f, a.Vst3Uid, a.Slot));
        }

        var plugins = await _engine!.ScanPluginsAsync(
            new[] { directory }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

        // De-duplicate plugins that the engine reports more than once — the same
        // Waves plugin is exposed by EVERY installed WaveShell version (16.6 /
        // 16.7 / 16.8 / ARA), so without this the list balloons 3-4x. Keep the
        // first occurrence of each (name, manufacturer); Mono vs Stereo have
        // distinct names so both are preserved.
        var seenIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pl in plugins)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(pl.File)) continue;
            // A TX insert effect must have an audio input to process. Instruments
            // and generators (Waves InternalSynth pianos, Clavinet, etc.) have
            // zero inputs / are flagged as instruments — they can't sit in the
            // insert chain, so skip them.
            if (pl.IsInstrument || pl.NumInputs == 0) continue;

            if (!seenIdentity.Add($"{pl.Name} {pl.Manufacturer}")) continue;

            var name = string.IsNullOrWhiteSpace(pl.Name)
                ? Path.GetFileNameWithoutExtension(pl.File) : pl.Name;

            foreach (var routeInfo in routes)
            {
                var routeName = routeInfo.DisplaySuffix is null ? name : $"{name} {routeInfo.DisplaySuffix}";
                var slot = ResolveSlot(routeInfo, name, pl.File);
                var id = StableUniqueId(name, pl.Uid, usedIds, ResolveIdPrefix(routeInfo, slot));
                usedIds.Add(id);

                if (activeIds.Contains(id) || activeKeys.Contains(EngineKey(pl.File, pl.Uid, slot)))
                {
                    skipped.Add(new ScannedVst(id, routeName, pl.File));
                    continue;
                }

                try
                {
                    var pluginDir = Path.Combine(root, id);
                    Directory.CreateDirectory(pluginDir);
                    WriteStubAssembly(Path.Combine(pluginDir, StubAssemblyFile));
                    await File.WriteAllTextAsync(
                        Path.Combine(pluginDir, "plugin.json"),
                        BuildManifestJson(id, routeName, pl.File, pl.Uid, slot),
                        ct).ConfigureAwait(false);
                    await _manager.ActivateAsync(pluginDir, ct).ConfigureAwait(false);
                    registered.Add(new ScannedVst(id, routeName, pl.File));
                    _log.LogInformation("Registered VST '{Name}' as {Id} (uid={Uid}, route={Route})", routeName, id, pl.Uid, slot);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to register VST {Name} from {File}", routeName, pl.File);
                    errors.Add(new ScanError(pl.File, ex.Message));
                }
            }
        }

        return new ScanResult(directory, registered, skipped, errors);
    }

    private static string EngineKey(string file, string? uid, string slot) =>
        Path.GetFullPath(file).TrimEnd('\\', '/') + " " + (uid ?? string.Empty) + " " + slot;

    // ── Static file-walk enumeration (Native mode; whole-file single plugins) ────
    private async Task<ScanResult> ScanViaFileWalkAsync(
        string directory,
        string root,
        IReadOnlyList<AudioRouteRegistration> routes,
        CancellationToken ct,
        IReadOnlyList<string>? exactEntries = null)
    {
        var entries = exactEntries?.ToList() ?? FindVst3Entries(directory);

        var registered = new List<ScannedVst>();
        var skipped = new List<ScannedVst>();
        var errors = new List<ScanError>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var activeIds = new HashSet<string>(
            _manager.Active.Select(p => p.Loaded.Manifest.Id), StringComparer.Ordinal);

        // The in-process bridge is the authoritative loadability check + metadata
        // source on EVERY platform — it is exactly what hosts the plugin in Native
        // mode. describe() loads the module and reads the factory, so it accepts a
        // Linux ELF .vst3 / correct-arch bundle (which the Windows-PE heuristic
        // below rejects) and yields the real plugin name. Falls back to the static
        // PE heuristic only when the native bridge can't initialise (e.g. the
        // library isn't staged in a CI/test layout).
        var bridge = TryCreateScanBridge();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileNameWithoutExtension(entry.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            try
            {
                var candidates = EnumerateVstCandidates(entry, bridge, errors, fileName);
                if (candidates.Count == 0)
                {
                    _log.LogInformation("Skipped VST '{Name}' (not hostable on this platform)", fileName);
                    continue;
                }

                // Reference the VST in place — do NOT copy it. Many plugins ship
                // as a thin .vst3 stub beside sibling payload libraries / resources
                // in their install dir; copying only the .vst3 strips those siblings
                // and the module then fails to load. The manifest carries the
                // ORIGINAL absolute path; both the engine's load_chain and the
                // in-process bridge honour a rooted vst3Path. Trade-off: moving or
                // deleting the original breaks the plugin — acceptable vs. silently
                // breaking every stub-based plugin.
                foreach (var cand in candidates)
                foreach (var routeInfo in routes)
                {
                    var routeName = routeInfo.DisplaySuffix is null ? cand.Name : $"{cand.Name} {routeInfo.DisplaySuffix}";
                    var slot = ResolveSlot(routeInfo, cand.Name, cand.Vst3Abs);
                    var id = UniqueId(cand.Name, usedIds, ResolveIdPrefix(routeInfo, slot));
                    usedIds.Add(id);

                    if (activeIds.Contains(id))
                    {
                        skipped.Add(new ScannedVst(id, routeName, entry));
                        continue;
                    }

                    var pluginDir = Path.Combine(root, id);
                    Directory.CreateDirectory(pluginDir);

                    WriteStubAssembly(Path.Combine(pluginDir, StubAssemblyFile));
                    await File.WriteAllTextAsync(
                        Path.Combine(pluginDir, "plugin.json"),
                        BuildManifestJson(id, routeName, cand.Vst3Abs, cand.Uid, slot),
                        ct).ConfigureAwait(false);

                    await _manager.ActivateAsync(pluginDir, ct).ConfigureAwait(false);
                    registered.Add(new ScannedVst(id, routeName, entry));
                    _log.LogInformation("Registered scanned VST '{Name}' as {Id} (route={Route})", routeName, id, slot);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to register scanned VST {Entry}", entry);
                errors.Add(new ScanError(entry, ex.Message));
            }
        }

        return new ScanResult(directory, registered, skipped, errors);
    }

    private sealed record VstCandidate(string Vst3Abs, string Name, string? Uid);

    private static IVstBridgeNative? TryCreateScanBridge()
    {
        try
        {
            var b = new VstBridgeNative();
            return b.Init(VstBridgeAbi.Current) == VstBridgeStatus.Ok ? b : null;
        }
        catch
        {
            return null; // native lib absent (CI / unsupported layout) — use the heuristic
        }
    }

    /// <summary>
    /// Hostable plugin candidates for one <c>.vst3</c> entry. Two layered checks:
    /// the cheap static Windows-PE heuristic accepts a 64-bit Windows VST3 without
    /// loading anything (and gives the skip reason for incompatible Windows files);
    /// when it rejects a binary — which it always does for a Linux ELF / macOS
    /// dylib VST3 — the in-process bridge (the actual host) is asked to describe
    /// the file, and a non-empty result both confirms it is hostable on THIS
    /// platform/arch and yields the real plugin name. The bridge also enriches the
    /// display name on the Windows path when it can introspect the file. The
    /// in-process loader instantiates the first audio-effect class, so a file
    /// yields one candidate (uid null = "load the first class"; shell sub-plugin
    /// selection awaits a load-by-uid ABI).
    /// </summary>
    private IReadOnlyList<VstCandidate> EnumerateVstCandidates(
        string entry, IVstBridgeNative? bridge, List<ScanError> errors, string fileName)
    {
        var vst3Abs = Path.GetFullPath(entry);

        if (IsLoadableVst3(entry, out var reason))
        {
            var name = fileName;
            if (bridge is not null)
            {
                var d = VstBridgeNative.Scan(bridge, vst3Abs);
                if (d.Count > 0 && !string.IsNullOrWhiteSpace(d[0].Name)) name = d[0].Name;
            }
            return [new VstCandidate(vst3Abs, name, null)];
        }

        // PE heuristic rejected it — but it may be a Linux/macOS VST3 the bridge
        // can host. The bridge IS what loads it in Native mode, so its verdict is
        // authoritative for non-Windows binaries.
        if (bridge is not null)
        {
            var descs = VstBridgeNative.Scan(bridge, vst3Abs);
            if (descs.Count > 0)
            {
                var name = string.IsNullOrWhiteSpace(descs[0].Name) ? fileName : descs[0].Name;
                return [new VstCandidate(vst3Abs, name, null)];
            }
        }

        errors.Add(new ScanError(entry, $"skipped (incompatible): {reason}"));
        return [];
    }

    /// <summary>
    /// Walk <paramref name="root"/> for <c>*.vst3</c> entries (file OR
    /// bundle directory), bounded in depth and NOT descending into a
    /// found bundle.
    /// </summary>
    private static List<string> FindVst3Entries(string root)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > MaxScanDepth) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { return; } // unreadable dir — skip
            foreach (var e in entries)
            {
                if (e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip macOS AppleDouble sidecars (e.g. "._ReLife.vst3").
                    // These resource-fork shadow files travel next to the real
                    // bundle on cross-platform copies; they parse as a .vst3 by
                    // extension but are junk metadata that no host can load. Left
                    // in, they register as dead chain plugins that silently
                    // passthrough (a VST chain that "does nothing").
                    var leaf = Path.GetFileName(e.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (leaf.StartsWith("._", StringComparison.Ordinal)) continue;
                    found.Add(e); // file or bundle — record, don't descend in
                    continue;
                }
                if (Directory.Exists(e)) Walk(e, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    private static string? ResolveExactVst3Entry(string path)
    {
        if (!path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(path) || Directory.Exists(path)) return Path.GetFullPath(path);
        return null;
    }

    /// <summary>
    /// Cheap static check that an entry is a 64-bit Windows VST3 the engine can
    /// host — WITHOUT executing any plugin code. Rejects the cases that left
    /// dead "Bad Image" entries in the rack: non-PE junk (e.g. macOS AppleDouble
    /// sidecars), 32-bit binaries (the engine is x64), and DLLs that don't export
    /// the VST3 factory (e.g. VST2-only). A heuristic, not a full load: a plugin
    /// that passes here can still fail at runtime (the engine's crash isolation
    /// covers that), but the common incompatibilities never enter the chain.
    /// </summary>
    private static bool IsLoadableVst3(string entry, out string reason)
    {
        reason = string.Empty;
        var binary = ResolveVst3Binary(entry);
        if (binary is null) { reason = "no 64-bit Windows VST3 binary in bundle"; return false; }

        var machine = ReadPeMachine(binary);
        if (machine is null) { reason = "not a Windows executable image"; return false; }
        if (machine != 0x8664)
        {
            reason = machine == 0x14C ? "32-bit (engine is 64-bit)" : $"unsupported CPU architecture 0x{machine:X}";
            return false;
        }
        if (!FileContainsAscii(binary, "GetPluginFactory"))
        {
            reason = "not a VST3 (no GetPluginFactory export)";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Resolve the loadable Windows binary for a .vst3 entry: the file itself for
    /// a single-file plugin, or <c>Contents\x86_64-win\*.vst3</c> (preferred) /
    /// <c>x86-win</c> for a bundle directory. Null if no candidate binary exists.
    /// </summary>
    private static string? ResolveVst3Binary(string entry)
    {
        if (File.Exists(entry)) return entry;
        if (!Directory.Exists(entry)) return null;

        // VST3 bundle layout: <name>.vst3/Contents/<arch>/<name>.vst3
        foreach (var arch in new[] { "x86_64-win", "x86-win" })
        {
            var archDir = Path.Combine(entry, "Contents", arch);
            if (!Directory.Exists(archDir)) continue;
            var bin = Directory.EnumerateFiles(archDir, "*.vst3").FirstOrDefault()
                      ?? Directory.EnumerateFiles(archDir, "*.dll").FirstOrDefault();
            if (bin is not null) return bin;
        }
        return null;
    }

    /// <summary>PE COFF machine field (0x8664 = x64, 0x14C = x86), or null if not a PE.</summary>
    private static ushort? ReadPeMachine(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) return null;
            if (br.ReadUInt16() != 0x5A4D) return null;        // 'MZ'
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOff = br.ReadInt32();
            if (peOff <= 0 || peOff + 6 > fs.Length) return null;
            fs.Seek(peOff, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return null;    // 'PE\0\0'
            return br.ReadUInt16();
        }
        catch { return null; }
    }

    /// <summary>
    /// Stream the file looking for an ASCII needle (an exported symbol name).
    /// Reads in chunks with overlap so a match spanning a boundary isn't missed;
    /// early-exits on first hit. Cheap relative to actually loading the module.
    /// </summary>
    private static bool FileContainsAscii(string path, string needle)
    {
        try
        {
            var pat = System.Text.Encoding.ASCII.GetBytes(needle);
            using var fs = File.OpenRead(path);
            const int chunk = 1 << 20; // 1 MiB
            var buf = new byte[chunk + pat.Length];
            int carry = 0;
            int read;
            while ((read = fs.Read(buf, carry, chunk)) > 0)
            {
                int total = carry + read;
                if (IndexOf(buf, total, pat) >= 0) return true;
                carry = Math.Min(pat.Length - 1, total);
                Array.Copy(buf, total - carry, buf, 0, carry); // keep tail for boundary spans
            }
            return false;
        }
        catch { return false; }
    }

    private static int IndexOf(byte[] haystack, int length, byte[] needle)
    {
        for (int i = 0; i <= length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static void WriteStubAssembly(string destPath)
    {
        using var res = typeof(VstDirectoryScanService).Assembly
            .GetManifestResourceStream(StubResourceName)
            ?? throw new InvalidOperationException(
                $"embedded stub resource '{StubResourceName}' not found");
        using var file = File.Create(destPath);
        res.CopyTo(file);
    }

    private static string RecommendedAudioSlot(string name, string vst3Path)
    {
        static string Normalize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        string key = Normalize(name) + " " + Normalize(Path.GetFileNameWithoutExtension(vst3Path));
        return key.Contains("rnnoise", StringComparison.Ordinal) ||
            key.Contains("noisesuppressionforvoice", StringComparison.Ordinal) ||
            key.Contains("wermannoisesuppression", StringComparison.Ordinal)
                ? "rx.post-demod"
                : "tx.post-leveler";
    }

    public static bool IsTxPluginId(string pluginId) =>
        pluginId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static bool IsRxPluginId(string pluginId) =>
        pluginId.StartsWith(RxIdPrefix, StringComparison.Ordinal);

    private static IReadOnlyList<AudioRouteRegistration> ResolveRoutes(string? route)
    {
        var normalized = (route ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "" => [new AudioRouteRegistration("auto", IdPrefix, null)],
            "tx" => [new AudioRouteRegistration("tx.post-leveler", IdPrefix, null)],
            "rx" => [new AudioRouteRegistration("rx.post-demod", RxIdPrefix, "RX")],
            "both" => [
                new AudioRouteRegistration("tx.post-leveler", IdPrefix, "TX"),
                new AudioRouteRegistration("rx.post-demod", RxIdPrefix, "RX"),
            ],
            _ => throw new ArgumentException("route must be 'auto', 'tx', 'rx', or 'both'"),
        };
    }

    private static string ResolveSlot(AudioRouteRegistration route, string name, string vst3Path) =>
        route.Slot == "auto"
            ? RecommendedAudioSlot(name, vst3Path)
            : route.Slot;

    private static string ResolveIdPrefix(AudioRouteRegistration route, string slot) =>
        route.Slot == "auto" && string.Equals(slot, "rx.post-demod", StringComparison.Ordinal)
            ? RxIdPrefix
            : route.IdPrefix;

    private static string BuildManifestJson(
        string id,
        string name,
        string vst3Path,
        string? vst3Uid = null,
        string slot = "tx.post-leveler")
    {
        // Anonymous object keyed to the manifest's JsonPropertyName values
        // (camelCase). Most scanned VSTs route into the TX insert chain so they
        // land in the Audio Suite rack. Known RNNoise/noise-suppression VSTs are
        // receive speech denoisers, so route them to rx.post-demod where Candidate can
        // feed them demodulated 48 kHz audio. vst3Path is the ORIGINAL absolute
        // path to the .vst3 (referenced in place, not copied); vst3Uid selects
        // one sub-plugin from a shell file (null = single plugin).
        var manifest = new
        {
            schemaVersion = 1,
            id,
            name,
            version = "1.0.0",
            author = "Scanned VST",
            description = $"VST3 plugin registered from a scanned directory ({name}).",
            license = "Unknown",
            sdk = new { abi = 1, minVersion = "1.0.0" },
            entrypoint = new { assembly = StubAssemblyFile },
            audio = new
            {
                vst3Path,
                vst3Uid,
                slot,
                channels = 1,
                sampleRate = 48000,
            },
        };
        return JsonSerializer.Serialize(manifest, JsonOpts);
    }

    /// <summary>
    /// A stable, unique plugin id from a display name, disambiguated by a short
    /// hash of the engine uid when names collide (shell sub-plugins share a file
    /// and often have similar names). Same (name, uid) ⇒ same id across rescans,
    /// so chain/parked state keyed by id survives a re-scan.
    /// </summary>
    private static string StableUniqueId(string name, string uid, HashSet<string> used, string idPrefix)
    {
        var slug = Slugify(name);
        if (slug.Length == 0) slug = "plugin";
        var baseId = idPrefix + slug;
        if (!used.Contains(baseId)) return baseId;

        var suffix = ShortHash(uid.Length > 0 ? uid : name);
        var candidate = $"{baseId}{suffix}";
        for (int n = 2; used.Contains(candidate); n++)
            candidate = $"{baseId}{suffix}{n}";
        return candidate;
    }

    /// <summary>8 lowercase hex chars of SHA-256(s) — stable id disambiguator.</summary>
    private static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string UniqueId(string name, HashSet<string> used, string idPrefix)
    {
        var slug = Slugify(name);
        if (slug.Length == 0) slug = "plugin";
        var id = idPrefix + slug;
        if (!used.Contains(id)) return id;
        for (int i = 2; ; i++)
        {
            var candidate = $"{idPrefix}{slug}{i}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Reduce a display name to the id charset: lowercase [a-z0-9] only
    /// (the manifest id pattern allows dots but not hyphens; we drop
    /// everything that isn't a letter or digit). Must end on alnum.
    /// </summary>
    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
        }
        return sb.ToString();
    }
}
