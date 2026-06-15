// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.Buffers.Binary;
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
/// <see cref="DspPipelineService.RxAudioAvailable"/> (RX) and
/// <see cref="DspPipelineService.TxMonitorAudioAvailable"/> (processed TX) and
/// copies whatever the pipeline already produced — it never pulls samples out
/// of a ring another consumer depends on.</para>
///
/// <para><b>Playback</b> streams a WAV to the local monitor via
/// <see cref="IAuditionAudioSink"/> (mixed into the operator's RX output), or
/// to the air via <see cref="TxAudioIngest"/> so the recording is processed by
/// the normal TX chain like live speech.</para>
///
/// Threading: capture callbacks arrive on the WDSP worker; playback runs on a
/// dedicated pump thread. All state transitions take <see cref="_sync"/>.
/// </summary>
public sealed class WavRecorderService : IDisposable
{
    // Playback cadence: 20 ms blocks @ 48 kHz, matching the mic worklet,
    // TxAudioIngest, and the audition ring's expectations.
    private const int PlaybackBlockMs = 20;

    private readonly DspPipelineService _pipeline;
    private readonly IAuditionAudioSink _audition;
    private readonly TxAudioIngest _txIngest;
    private readonly TxService _tx;
    private readonly ILogger<WavRecorderService> _log;
    private readonly string _recordingsDir;

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
    private string? _playingFile;
    private bool _restoreAuditionOff;
    private bool _playingOnAir;

    public WavRecorderService(
        DspPipelineService pipeline,
        IAuditionAudioSink audition,
        TxAudioIngest txIngest,
        TxService tx,
        ILogger<WavRecorderService> log)
    {
        _pipeline = pipeline;
        _audition = audition;
        _txIngest = txIngest;
        _tx = tx;
        _log = log;

        _recordingsDir = ResolveDownloadsDir();
        Directory.CreateDirectory(_recordingsDir);

        _pipeline.RxAudioAvailable += OnRxAudio;
        _txIngest.MicPcmTapped += OnMicPcm;
    }

    public string RecordingsDir => _recordingsDir;

    // Recordings save to the OS Downloads folder by default so they land
    // where the operator expects to find files. UserProfile/Downloads is the
    // default on macOS, Windows, and Linux; fall back to the profile root, then
    // the working dir, if Downloads can't be resolved.
    private static string ResolveDownloadsDir()
    {
        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return Environment.CurrentDirectory;
        string downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    // Files we own carry this prefix so listing/deleting never touches other
    // WAVs the operator happens to keep in Downloads.
    private const string FilePrefix = "zeus-";

    public WavRecorderStatus GetStatus()
    {
        lock (_sync)
        {
            return new WavRecorderStatus(
                State: _state.ToString().ToLowerInvariant(),
                Source: _recordSource.ToString().ToLowerInvariant(),
                File: _state == WavRecorderState.Recording ? _writer?.Path
                      : _state == WavRecorderState.Playing ? _playingFile
                      : null,
                Seconds: _state == WavRecorderState.Recording && _writer is { } w
                    ? Math.Round(w.SampleCount / (double)Math.Max(1, w.SampleRate), 1)
                    : 0,
                Mox: _tx.IsMoxOn,
                OnAir: _state == WavRecorderState.Playing && _playingOnAir);
        }
    }

    // ---- Recording ---------------------------------------------------------

    /// <summary>Begin capturing the chosen source to a new timestamped WAV.
    /// Returns the file path. Throws if not idle.</summary>
    public string StartRecording(WavRecordSource source)
    {
        lock (_sync)
        {
            if (_state != WavRecorderState.Idle)
                throw new InvalidOperationException($"recorder busy ({_state})");

            int rate = DspPipelineService.AudioOutputRateHz;
            string name = $"{FilePrefix}{source.ToString().ToLowerInvariant()}-"
                        + $"{DateTime.Now:yyyyMMdd-HHmmss}.wav";
            string path = Path.Combine(_recordingsDir, name);
            _writer = new WavWriter(path, rate);
            _recordSource = source;
            _state = WavRecorderState.Recording;
            // RX taps demodulated receive audio; TX taps the mic feeding the TX
            // chain — both non-destructive, both silent (no monitor playback).
            _log.LogInformation("wav.record start source={Source} file={File} rate={Rate}",
                source, path, rate);
            return path;
        }
    }

    /// <summary>Stop the in-progress recording and finalise the file.
    /// Returns the path and sample count, or null if not recording.</summary>
    public (string Path, long Samples)? StopRecording()
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
            _log.LogInformation("wav.record stop file={File} samples={Samples}", path, samples);
            return (path, samples);
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
            w.Append(_micDecode.AsSpan(0, n));
        }
    }

    // ---- Local playback ----------------------------------------------------

    /// <summary>Play a recording. MOX state at play time decides the
    /// destination: <b>MOX on → over the air</b> (injected into the TX chain
    /// and processed like live speech); <b>MOX off → local monitor</b> (mixed
    /// into RX audio, no transmit). The operator owns MOX — this never keys or
    /// unkeys. Throws if not idle or the file is missing.</summary>
    public void Play(string fileName)
    {
        string path = ResolveRecording(fileName);
        var (samples, rate) = WavFile.ReadAllSamples(path);

        lock (_sync)
        {
            if (_state != WavRecorderState.Idle)
                throw new InvalidOperationException($"recorder busy ({_state})");

            bool onAir = _tx.IsMoxOn;
            _playingFile = path;
            _playingOnAir = onAir;
            _state = WavRecorderState.Playing;
            _playCts = new CancellationTokenSource();

            // Local playback mixes into the speaker via the audition path; force
            // it on for the clip and restore after. Over-air playback does NOT
            // touch the audition sink (the operator hears their own TX monitor /
            // sidetone as usual, not a local copy).
            _restoreAuditionOff = false;
            if (!onAir)
            {
                _restoreAuditionOff = !_audition.IsEnabled;
                if (_restoreAuditionOff) _audition.SetEnabled(true);
            }

            var ct = _playCts.Token;
            _playThread = new Thread(() => PlaybackPump(samples, rate, onAir, ct))
            {
                IsBackground = true,
                Name = "wav-playback",
            };
            _playThread.Start();
            _log.LogInformation("wav.play start file={File} samples={Samples} rate={Rate} onAir={OnAir}",
                path, samples.Length, rate, onAir);
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

    private void PlaybackPump(float[] samples, int rate, bool onAir, CancellationToken ct)
    {
        // Playback is paced by the 48 kHz mic-block clock. Non-48 kHz files
        // are converted once here so both desktop audition and over-air TX see
        // the canonical 960-sample block shape.
        byte[]? airBlock = null;
        try
        {
            if (!TxMicBlockResampler.IsSupportedInputSampleRate(rate))
            {
                _log.LogWarning("wav.play unsupported sampleRate={Rate}", rate);
                return;
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
                    _audition.PublishAudition(block, TxMicBlockResampler.OutputSampleRate);
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
        lock (_sync)
        {
            if (_state != WavRecorderState.Playing) return;
            if (_restoreAuditionOff) { _audition.SetEnabled(false); _restoreAuditionOff = false; }
            _playCts?.Dispose();
            _playCts = null;
            _playThread = null;
            _log.LogInformation("wav.play stop file={File} onAir={OnAir}", _playingFile, _playingOnAir);
            _playingFile = null;
            _playingOnAir = false;
            _state = WavRecorderState.Idle;
        }
    }

    // ---- Listing -----------------------------------------------------------

    public IReadOnlyList<WavRecordingInfo> ListRecordings()
    {
        var list = new List<WavRecordingInfo>();
        foreach (var path in Directory.EnumerateFiles(_recordingsDir, FilePrefix + "*.wav"))
        {
            var fi = new FileInfo(path);
            list.Add(new WavRecordingInfo(
                Name: fi.Name,
                Bytes: fi.Length,
                ModifiedUnixMs: new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        list.Sort((a, b) => b.ModifiedUnixMs.CompareTo(a.ModifiedUnixMs));
        return list;
    }

    public bool DeleteRecording(string fileName)
    {
        string path = ResolveRecording(fileName);
        File.Delete(path);
        _log.LogInformation("wav.delete file={File}", path);
        return true;
    }

    private string ResolveRecording(string fileName)
    {
        // Guard against path traversal — only bare names inside the dir.
        string safe = Path.GetFileName(fileName);
        string path = Path.Combine(_recordingsDir, safe);
        if (!File.Exists(path)) throw new FileNotFoundException("recording not found", safe);
        return path;
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
    string State, string Source, string? File, double Seconds, bool Mox, bool OnAir);

/// <summary>One entry in the recordings list.</summary>
public sealed record WavRecordingInfo(string Name, long Bytes, long ModifiedUnixMs);
