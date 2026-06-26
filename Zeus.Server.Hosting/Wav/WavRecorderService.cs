// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using Zeus.Contracts;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Wav;

/// <summary>What the recorder is capturing from.</summary>
public enum WavRecordSource
{
    /// <summary>Demodulated receive audio — what the operator hears.</summary>
    Rx = 0,
    /// <summary>Transmit-side mic audio, tapped clean as it enters the TX chain
    /// (pre-processing). Captured silently — no monitor playback. On over-the-
    /// air playback it runs through the normal TX processing once, so it sounds
    /// like a live transmission with no double-coloring.</summary>
    Tx = 1,
}

/// <summary>Where a recording is sent when played back.</summary>
public enum WavPlayDest
{
    /// <summary>Mix into the local monitor via the preview sink regardless of
    /// MOX. Never keys the transmitter. Heard by the operator only.</summary>
    Local = 0,
    /// <summary>Transmit the recording over the air. The recorder keys MOX
    /// itself (<see cref="MoxSource.Wav"/>) when the rig is unkeyed, and
    /// releases only what it keyed.</summary>
    Air = 1,
}

/// <summary>Recorder state for the status DTO.</summary>
public enum WavRecorderState
{
    Idle = 0,
    Recording = 1,
    Playing = 2,
}

/// <summary>
/// Records RX or processed-TX audio to a float32 WAV and plays recordings back.
///
/// <para><b>Capture</b> is non-destructive: it subscribes to
/// <see cref="DspPipelineService.RxAudioAvailable"/> (RX) and the
/// <see cref="TxAudioIngest.MicPcmTapped"/> mic tap (processed TX) and copies
/// whatever the pipeline already produced — it never pulls samples out of a
/// ring another consumer depends on.</para>
///
/// <para><b>Files</b> live under a managed root (<c>&lt;Downloads&gt;/Zeus
/// Recordings</c>) owned by <see cref="WavLibrary"/>: arbitrary nesting of
/// folders, traversal-guarded paths, and a prefix-free recursive listing.</para>
///
/// <para><b>Playback</b> takes an explicit destination.
/// <see cref="WavPlayDest.Local"/> mixes the WAV into the RX audio block via
/// <see cref="DspPipelineService.EnqueueMonitorAudio"/> (the universal monitor
/// path that reaches every sink — browser WebSocket and desktop native — alike)
/// without keying. <see cref="WavPlayDest.Air"/>
/// transmits the recording through <see cref="TxAudioIngest"/> so it is
/// processed by the normal TX chain like live speech; the recorder keys MOX
/// itself only when the rig is unkeyed and releases only what it keyed (see
/// <see cref="MoxSource.Wav"/>).</para>
///
/// Threading: capture callbacks arrive on the WDSP worker; playback runs on a
/// dedicated pump thread. All state transitions take <see cref="_sync"/>. The
/// <see cref="WavMeter"/> is updated lock-free from the audio hot paths.
/// </summary>
public sealed class WavRecorderService : IDisposable
{
    // Playback cadence: 20 ms blocks @ 48 kHz, matching the mic worklet,
    // TxAudioIngest, and the preview ring's expectations.
    private const int PlaybackBlockMs = 20;

    private readonly DspPipelineService _pipeline;
    private readonly TxAudioIngest _txIngest;
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly ILogger<WavRecorderService> _log;
    private readonly WavLibrary _library;
    private readonly WavRecorderSettingsStore _settings;
    private readonly WavMeter _meter = new();

    private readonly object _sync = new();
    private WavRecorderState _state = WavRecorderState.Idle;

    // Recording
    private WavWriter? _writer;
    private WavRecordSource _recordSource;
    // Scratch for decoding the f32le mic tap to float; only touched under _sync
    // while a TX recording is active.
    private readonly float[] _micDecode = new float[960];

    // Playback
    private Thread? _playThread;
    private CancellationTokenSource? _playCts;
    private string? _playingFile;       // absolute path of the clip being played
    private bool _playingOnAir;
    private bool _weKeyedMox;            // true ⇒ we raised MOX and must drop it
    private double _playingDurationSec;

    public WavRecorderService(
        DspPipelineService pipeline,
        TxAudioIngest txIngest,
        TxService tx,
        RadioService radio,
        ILogger<WavRecorderService> log,
        WavRecorderSettingsStore settings,
        string? recordingsRootOverride = null)
    {
        _pipeline = pipeline;
        _txIngest = txIngest;
        _tx = tx;
        _radio = radio;
        _log = log;
        _settings = settings;

        // Resolve the effective root: an explicit test override wins; otherwise
        // the persisted custom root if it's set and usable; otherwise the
        // platform default. Migrate loose legacy files ONLY when we land on the
        // default root — never scan an operator-chosen parent directory.
        string defaultRoot = WavLibrary.DefaultRoot();
        string effectiveRoot;
        bool migrate;
        if (!string.IsNullOrWhiteSpace(recordingsRootOverride))
        {
            effectiveRoot = recordingsRootOverride;
            migrate = PathsEqual(recordingsRootOverride, defaultRoot);
        }
        else
        {
            string? persisted = _settings.GetRoot();
            if (!string.IsNullOrWhiteSpace(persisted) && IsUsableRoot(persisted))
            {
                effectiveRoot = persisted;
                migrate = false;
            }
            else
            {
                effectiveRoot = defaultRoot;
                migrate = true;
            }
        }

        _library = new WavLibrary(effectiveRoot, log, migrate);

        _pipeline.RxAudioAvailable += OnRxAudio;
        _txIngest.MicPcmTapped += OnMicPcm;
    }

    public string RecordingsDir => _library.Root;

    /// <summary>The current recordings root and whether it is the platform
    /// default.</summary>
    public (string Root, bool IsDefault) GetRecordingsRoot()
    {
        lock (_sync)
        {
            return (_library.Root, PathsEqual(_library.Root, WavLibrary.DefaultRoot()));
        }
    }

    /// <summary>Change the recordings root, persisting the choice.
    /// <para>Null/empty resets to the platform default (and clears the persisted
    /// value). A non-empty value must be an ABSOLUTE, creatable, writable path.
    /// Only allowed while idle.</para>
    /// Throws <see cref="InvalidOperationException"/> if the recorder is busy and
    /// <see cref="ArgumentException"/> on a path that is relative or cannot be
    /// created/written.</summary>
    public (string Root, bool IsDefault) SetRecordingsRoot(string? absPath)
    {
        lock (_sync)
        {
            if (_state != WavRecorderState.Idle)
                throw new InvalidOperationException($"recorder busy ({_state})");

            string defaultRoot = WavLibrary.DefaultRoot();
            string resolved;

            if (string.IsNullOrWhiteSpace(absPath))
            {
                resolved = defaultRoot;
                _settings.SetRoot(null);
            }
            else
            {
                if (!Path.IsPathRooted(absPath))
                    throw new ArgumentException("recordings path must be absolute", nameof(absPath));
                resolved = Path.GetFullPath(absPath);
                if (!TryProbeWritable(resolved, out string? err))
                    throw new ArgumentException(err ?? "recordings path is not writable", nameof(absPath));
                _settings.SetRoot(resolved);
            }

            _library.SetRoot(resolved);
            return (_library.Root, PathsEqual(_library.Root, defaultRoot));
        }
    }

    // A persisted root is usable if it already exists or can be created.
    private static bool IsUsableRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // Create the directory and prove we can write a file into it, then clean up
    // the probe. Any failure means the path is unusable as a root.
    private static bool TryProbeWritable(string root, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, ".zeus-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"recordings path is not writable: {ex.Message}";
            return false;
        }
    }

    // Case- and separator-tolerant absolute-path comparison.
    private static bool PathsEqual(string a, string b)
    {
        string fa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string fb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fa, fb, cmp);
    }

    public WavRecorderStatus GetStatus()
    {
        lock (_sync)
        {
            return new WavRecorderStatus(
                State: _state.ToString().ToLowerInvariant(),
                Source: _recordSource.ToString().ToLowerInvariant(),
                File: _state == WavRecorderState.Recording ? _library.RelOf(_writer?.Path)
                      : _state == WavRecorderState.Playing ? _library.RelOf(_playingFile)
                      : null,
                Seconds: _state == WavRecorderState.Recording && _writer is { } w
                    ? Math.Round(w.SampleCount / (double)Math.Max(1, w.SampleRate), 1)
                    : 0,
                DurationSec: _state == WavRecorderState.Playing
                    ? Math.Round(_playingDurationSec, 1)
                    : 0,
                Mox: _tx.IsMoxOn,
                OnAir: _state == WavRecorderState.Playing && _playingOnAir,
                Peak: _meter.Peak,
                Rms: _meter.Rms,
                PeakDb: _meter.PeakDb,
                Clip: _meter.Clip);
        }
    }

    // ---- Recording ---------------------------------------------------------

    /// <summary>Begin capturing the chosen source to a new timestamped WAV under
    /// the managed root (optionally inside <paramref name="folder"/>). Returns
    /// the new file's root-relative path. Throws if not idle.</summary>
    public string StartRecording(WavRecordSource source, string? folder = null)
    {
        lock (_sync)
        {
            if (_state != WavRecorderState.Idle)
                throw new InvalidOperationException($"recorder busy ({_state})");

            string dir = _library.ResolveRecordDir(folder);
            int rate = DspPipelineService.AudioOutputRateHz;
            string name = $"{WavLibrary.FilePrefix}{source.ToString().ToLowerInvariant()}-"
                        + $"{DateTime.Now:yyyyMMdd-HHmmss}.wav";
            string path = Path.Combine(dir, name);
            _writer = new WavWriter(path, rate);
            _recordSource = source;
            _state = WavRecorderState.Recording;
            _meter.Reset();
            _log.LogInformation("wav.record start source={Source} file={File} rate={Rate}",
                source, path, rate);
            return _library.RelOf(path)!;
        }
    }

    /// <summary>Stop the in-progress recording and finalise the file.
    /// Returns the root-relative path and sample count, or null if not
    /// recording.</summary>
    public (string RelPath, long Samples)? StopRecording()
    {
        lock (_sync)
        {
            if (_state != WavRecorderState.Recording || _writer is null) return null;
            var w = _writer;
            _writer = null;
            _state = WavRecorderState.Idle;
            string path = w.Path;
            long samples = w.SampleCount;
            w.Dispose();
            _meter.Reset();
            _log.LogInformation("wav.record stop file={File} samples={Samples}", path, samples);
            return (_library.RelOf(path)!, samples);
        }
    }

    private void OnRxAudio(int rxId, int sampleRate, ReadOnlyMemory<float> samples)
    {
        lock (_sync)
        {
            if (_state == WavRecorderState.Recording
                && _recordSource == WavRecordSource.Rx
                && _writer is { } w)
            {
                w.Append(samples.Span);
                _meter.Update(samples.Span);
            }
        }
    }

    // f32le mic blocks (960 samples) from the TX ingest tap. Decoded and
    // appended only while a TX recording is active.
    private void OnMicPcm(ReadOnlyMemory<byte> f32lePayload)
    {
        lock (_sync)
        {
            if (_state != WavRecorderState.Recording
                || _recordSource != WavRecordSource.Tx
                || _writer is not { } w) return;

            var src = f32lePayload.Span;
            int n = Math.Min(_micDecode.Length, src.Length / 4);
            for (int i = 0; i < n; i++)
                _micDecode[i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
            var span = _micDecode.AsSpan(0, n);
            w.Append(span);
            _meter.Update(span);
        }
    }

    // ---- Playback ----------------------------------------------------------

    /// <summary>Play a recording to an explicit destination.
    /// <list type="bullet">
    ///   <item><see cref="WavPlayDest.Local"/> — mix into the monitor via the
    ///   preview sink regardless of MOX; never keys.</item>
    ///   <item><see cref="WavPlayDest.Air"/> — transmit. If MOX is already on we
    ///   ride the existing key (and will NOT drop it at the end); if MOX is off
    ///   we key <see cref="MoxSource.Wav"/> ourselves and drop only what we
    ///   keyed.</item>
    /// </list>
    /// Throws <see cref="FileNotFoundException"/> if the file is missing,
    /// <see cref="InvalidOperationException"/> if the recorder is busy or MOX
    /// keying is refused, <see cref="ArgumentException"/> on a bad path.</summary>
    public void Play(string relPath, WavPlayDest dest)
    {
        string path = _library.ResolveRel(relPath);
        if (!File.Exists(path)) throw new FileNotFoundException("recording not found", relPath);
        var (samples, rate) = WavFile.ReadAllSamples(path);

        bool onAir = dest == WavPlayDest.Air;
        bool weKeyed = false;

        lock (_sync)
        {
            if (_state != WavRecorderState.Idle)
                throw new InvalidOperationException($"recorder busy ({_state})");

            if (onAir && !_tx.IsMoxOn)
            {
                if (!_tx.TrySetMox(true, MoxSource.Wav, out var err))
                    throw new InvalidOperationException(err ?? "could not key MOX");
                weKeyed = true;
            }
            // else over-air on an already-keyed rig: ride the operator's key and
            // do not unkey at the end.

            // From here on a key may be raised. If ANYTHING below throws (e.g.
            // thread/OOM exhaustion on a Pi when starting the pump), the pump's
            // finally→FinishPlayback never runs, so we must unwind to Idle and —
            // critically — drop any MOX we raised. Otherwise the rig is stranded
            // keyed with no release path. RF-safety: only ever drop a key we
            // raised ourselves; never touch an operator-held key.
            try
            {
                _playingFile = path;
                _playingOnAir = onAir;
                _weKeyedMox = weKeyed;
                _playingDurationSec = samples.Length / (double)Math.Max(1, rate);
                _state = WavRecorderState.Playing;
                _playCts = new CancellationTokenSource();
                _meter.Reset();

                // Local playback is mixed into the RX audio block on the pump thread
                // (see EmitBlock → EnqueueMonitorAudio), so it reaches the operator's
                // speakers in both web and desktop modes. Over-air playback feeds the
                // TX chain instead. Nothing to arm here.
                var ct = _playCts.Token;
                bool keyedForSettle = weKeyed;
                _playThread = new Thread(() => PlaybackPump(samples, rate, onAir, keyedForSettle, ct))
                {
                    IsBackground = true,
                    Name = "wav-playback",
                };
                _playThread.Start();
            }
            catch
            {
                // Unwind: reset state, tear down the CTS/thread refs, and release
                // any key we raised so we never leave the transmitter stranded.
                _state = WavRecorderState.Idle;
                _playingFile = null;
                _playingOnAir = false;
                _weKeyedMox = false;
                _playingDurationSec = 0;
                _playCts?.Dispose();
                _playCts = null;
                _playThread = null;
                if (weKeyed)
                    _tx.TrySetMox(false, MoxSource.Wav, out _);
                throw;
            }

            _log.LogInformation(
                "wav.play start file={File} samples={Samples} rate={Rate} onAir={OnAir} weKeyed={WeKeyed}",
                path, samples.Length, rate, onAir, weKeyed);
        }
    }

    /// <summary>Stop any in-progress playback.</summary>
    public void StopPlayback()
    {
        Thread? thread;
        lock (_sync)
        {
            if (_state != WavRecorderState.Playing) return;
            _playCts?.Cancel();
            thread = _playThread;
        }
        // Only join a thread that actually started and is still running. A thread
        // that was created but never Start()ed (Play threw mid-setup) throws
        // ThreadStateException on Join — which would skip FinishPlayback and leave
        // a keyed rig. IsAlive is false for both an unstarted and an
        // already-finished thread, so guarding on it keeps FinishPlayback always
        // reachable. FinishPlayback is idempotent (no-ops if already Idle).
        if (thread is { IsAlive: true })
            thread.Join(500);
        FinishPlayback();
    }

    private void PlaybackPump(float[] samples, int rate, bool onAir, bool weKeyed, CancellationToken ct)
    {
        // Playback is paced by the 48 kHz mic-block clock. Non-48 kHz files
        // are converted once here so both desktop preview and over-air TX see
        // the canonical 960-sample block shape.
        byte[]? airBlock = null;
        try
        {
            if (!TxMicBlockResampler.IsSupportedInputSampleRate(rate))
            {
                _log.LogWarning("wav.play unsupported sampleRate={Rate}", rate);
                return;
            }

            // When we keyed MOX ourselves, give the radio a brief settle before
            // the first audio block so the relay/PA is up. Interruptible. Runs
            // on the pump thread, never the request thread.
            if (onAir && weKeyed)
            {
                int settleMs = Math.Clamp(_radio.TxMoxPreKeyDelayMs, 0, 250);
                if (settleMs <= 0) settleMs = 50;
                ct.WaitHandle.WaitOne(settleMs);
            }

            airBlock = onAir ? new byte[TxMicBlockResampler.OutputBlockBytes] : null;
            int pos = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextDueMs = 0;
            bool stop = false;
            int sourceBlockSamples = Math.Max(1, (int)Math.Round(rate * (PlaybackBlockMs / 1000.0)));

            void Pace()
            {
                nextDueMs += PlaybackBlockMs;
                long waitMs = nextDueMs - sw.ElapsedMilliseconds;
                if (waitMs > 0) ct.WaitHandle.WaitOne((int)waitMs);
            }

            void EmitBlock(ReadOnlySpan<float> block)
            {
                if (ct.IsCancellationRequested)
                {
                    stop = true;
                    return;
                }

                _meter.Update(block);

                if (onAir)
                {
                    if (!_tx.IsMoxOn)
                    {
                        stop = true;
                        return;
                    }

                    var span = airBlock!.AsSpan();
                    for (int i = 0; i < TxMicBlockResampler.OutputBlockSamples; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * 4, 4), block[i]);
                    _txIngest.OnMicPcmBytesFromWav(airBlock);
                }
                else
                {
                    // Local monitor: mix into the RX audio block so it reaches
                    // every sink — browser (WebSocket) AND desktop (native) —
                    // alike. The old preview-sink side-channel was desktop-only
                    // and silent in web mode; EnqueueMonitorAudio is the
                    // universal path PluginPlaybackSink already uses. A full ring
                    // (returns false) just drops the block, same as that path.
                    _pipeline.EnqueueMonitorAudio(block);
                }

                Pace();
            }

            var converter = new TxMicBlockResampler(EmitBlock);
            while (pos < samples.Length && !ct.IsCancellationRequested)
            {
                // Unkeying mid-clip stops an over-air playback (samples would be
                // dropped by the MOX gate anyway, and the live mic should resume).
                if (onAir && !_tx.IsMoxOn) break;
                if (stop) break;

                int n = Math.Min(sourceBlockSamples, samples.Length - pos);
                converter.Accept(samples.AsSpan(pos, n), rate);
                pos += n;
            }

            if (!ct.IsCancellationRequested && !stop && (!onAir || _tx.IsMoxOn))
                converter.FlushZeroPadded();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wav.play pump faulted");
        }
        finally
        {
            FinishPlayback();
        }
    }

    private void FinishPlayback()
    {
        bool dropMox;
        lock (_sync)
        {
            if (_state != WavRecorderState.Playing) return;
            dropMox = _weKeyedMox;
            _weKeyedMox = false;
            _playCts?.Dispose();
            _playCts = null;
            _playThread = null;
            _log.LogInformation("wav.play stop file={File} onAir={OnAir} droppingMox={Drop}",
                _playingFile, _playingOnAir, dropMox);
            _playingFile = null;
            _playingOnAir = false;
            _playingDurationSec = 0;
            _state = WavRecorderState.Idle;
            _meter.Reset();
        }

        // Drop MOX only if we keyed it. Done outside _sync (TrySetMox reads
        // RadioService under its own lock). A drop on an already-off rig is a
        // harmless no-op/refusal — if the operator manually unkeyed mid-clip,
        // Wav no longer owns MOX and the refusal is fine.
        if (dropMox)
            _tx.TrySetMox(false, MoxSource.Wav, out _);
    }

    // ---- Listing / CRUD (delegated to the library) -------------------------

    public IReadOnlyList<WavRecordingInfo> ListRecordings() => _library.ListRecordings();
    public IReadOnlyList<string> ListFolders() => _library.ListFolders();

    /// <summary>Compute a peak-envelope overview of a recording for the waveform
    /// display: <paramref name="buckets"/> values (clamped 16..2000), each the
    /// max |sample| over its time slice (0..1). Throws
    /// <see cref="FileNotFoundException"/> if the clip is missing,
    /// <see cref="ArgumentException"/> on a bad path.</summary>
    public IReadOnlyList<float> ComputeWaveform(string relPath, int buckets)
    {
        string path = _library.ResolveRel(relPath);
        if (!File.Exists(path)) throw new FileNotFoundException("recording not found", relPath);
        var (samples, _) = WavFile.ReadAllSamples(path);
        return WavFile.Envelope(samples, Math.Clamp(buckets, 16, 2000));
    }

    public bool DeleteRecording(string relPath)
    {
        EnsureNotActiveTarget(_library.ResolveRel(relPath));
        return _library.DeleteRecording(relPath);
    }
    public string RenameRecording(string relFrom, string newDisplayName)
        => _library.RenameRecording(relFrom, newDisplayName);
    public string MoveRecording(string relFrom, string destFolder)
        => _library.MoveRecording(relFrom, destFolder);
    public string CreateFolder(string relPath) => _library.CreateFolder(relPath);
    public string DeleteFolder(string relPath)
    {
        EnsureNotActiveTarget(_library.ResolveRel(relPath));
        return _library.DeleteFolder(relPath);
    }

    // Refuse to delete the file currently being recorded or played, or a folder
    // that contains it. On Unix the unlink succeeds and silently truncates the
    // live capture (data loss); on Windows it throws a sharing violation (500).
    // Stop both up front with a clear error the endpoint maps to 409 Conflict.
    private void EnsureNotActiveTarget(string absPathOrDir)
    {
        lock (_sync)
        {
            if (_state == WavRecorderState.Idle) return;
            string? active = _state == WavRecorderState.Recording ? _writer?.Path
                           : _state == WavRecorderState.Playing ? _playingFile
                           : null;
            if (string.IsNullOrEmpty(active)) return;
            if (IsSameOrUnder(absPathOrDir, active))
                throw new InvalidOperationException(
                    $"cannot delete a recording that is currently {_state.ToString().ToLowerInvariant()}");
        }
    }

    // True when <paramref name="candidate"/> is the same file as
    // <paramref name="target"/> or lives under it (when target is a directory).
    private static bool IsSameOrUnder(string target, string candidate)
    {
        string t = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(t, c, cmp)
            || c.StartsWith(t + Path.DirectorySeparatorChar, cmp);
    }

    public void Dispose()
    {
        _pipeline.RxAudioAvailable -= OnRxAudio;
        _txIngest.MicPcmTapped -= OnMicPcm;
        StopPlayback();
        lock (_sync) { _writer?.Dispose(); _writer = null; }
    }
}

/// <summary>Status DTO for <c>GET /api/wav/status</c>.</summary>
public sealed record WavRecorderStatus(
    string State, string Source, string? File, double Seconds, double DurationSec,
    bool Mox, bool OnAir, double Peak, double Rms, double PeakDb, bool Clip);

/// <summary>One entry in the recordings list.</summary>
public sealed record WavRecordingInfo(
    string Name, string FileName, string RelPath, string Folder,
    long Bytes, double DurationSec, string Source, long ModifiedUnixMs);
