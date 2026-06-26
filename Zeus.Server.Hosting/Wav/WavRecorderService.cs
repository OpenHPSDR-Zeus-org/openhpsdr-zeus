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
/// <see cref="WavPlayDest.Local"/> streams a WAV to the operator's monitor via
/// <see cref="IPreviewAudioSink"/> without keying. <see cref="WavPlayDest.Air"/>
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
    private readonly IPreviewAudioSink _preview;
    private readonly TxAudioIngest _txIngest;
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly ILogger<WavRecorderService> _log;
    private readonly WavLibrary _library;
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
    private bool _restorePreviewOff;
    private bool _playingOnAir;
    private bool _weKeyedMox;            // true ⇒ we raised MOX and must drop it
    private double _playingDurationSec;

    public WavRecorderService(
        DspPipelineService pipeline,
        IPreviewAudioSink preview,
        TxAudioIngest txIngest,
        TxService tx,
        RadioService radio,
        ILogger<WavRecorderService> log,
        string? recordingsRootOverride = null)
    {
        _pipeline = pipeline;
        _preview = preview;
        _txIngest = txIngest;
        _tx = tx;
        _radio = radio;
        _log = log;

        _library = new WavLibrary(recordingsRootOverride ?? WavLibrary.DefaultRoot(), log);

        _pipeline.RxAudioAvailable += OnRxAudio;
        _txIngest.MicPcmTapped += OnMicPcm;
    }

    public string RecordingsDir => _library.Root;

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

            _playingFile = path;
            _playingOnAir = onAir;
            _weKeyedMox = weKeyed;
            _playingDurationSec = samples.Length / (double)Math.Max(1, rate);
            _state = WavRecorderState.Playing;
            _playCts = new CancellationTokenSource();
            _meter.Reset();

            // Local playback mixes into the speaker via the preview path; force
            // it on for the clip and restore after. Over-air playback does NOT
            // touch the preview sink (the operator hears their TX monitor /
            // sidetone as usual, not a local copy).
            _restorePreviewOff = false;
            if (!onAir)
            {
                _restorePreviewOff = !_preview.IsEnabled;
                if (_restorePreviewOff) _preview.SetEnabled(true);
            }

            var ct = _playCts.Token;
            bool keyedForSettle = weKeyed;
            _playThread = new Thread(() => PlaybackPump(samples, rate, onAir, keyedForSettle, ct))
            {
                IsBackground = true,
                Name = "wav-playback",
            };
            _playThread.Start();
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
        thread?.Join(500);
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
                    _preview.PublishPreview(block, TxMicBlockResampler.OutputSampleRate);
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
            if (_restorePreviewOff) { _preview.SetEnabled(false); _restorePreviewOff = false; }
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

    public bool DeleteRecording(string relPath) => _library.DeleteRecording(relPath);
    public string RenameRecording(string relFrom, string newDisplayName)
        => _library.RenameRecording(relFrom, newDisplayName);
    public string MoveRecording(string relFrom, string destFolder)
        => _library.MoveRecording(relFrom, destFolder);
    public string CreateFolder(string relPath) => _library.CreateFolder(relPath);
    public string DeleteFolder(string relPath) => _library.DeleteFolder(relPath);

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
