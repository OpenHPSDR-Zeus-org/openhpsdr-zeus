// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers.Binary;

namespace Zeus.Server.Wav;

/// <summary>
/// Streaming writer for a 32-bit-float mono WAV. Writes a placeholder RIFF
/// header up front, appends sample blocks as they arrive from the audio path,
/// and patches the two RIFF size fields on <see cref="Dispose"/> so the file is
/// valid even if recording is stopped abruptly (the header is rewritten from
/// the byte count we tracked). Not thread-safe — the owner serialises
/// <see cref="Append"/> / <see cref="Dispose"/> behind its own lock.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;

    private readonly FileStream _fs;
    private readonly BinaryWriter _bw;
    private long _dataBytes;
    private bool _disposed;

    public string Path { get; }
    public int SampleRate { get; }
    public long SampleCount => _dataBytes / 4;

    public WavWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        _fs = File.Create(path);
        _bw = new BinaryWriter(_fs);
        WriteHeader(sampleRate, dataBytes: 0);
    }

    /// <summary>Append a block of mono float32 samples to the file.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (_disposed) return;
        Span<byte> buf = stackalloc byte[4];
        foreach (float s in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf, s);
            _bw.Write(buf);
        }
        _dataBytes += (long)samples.Length * 4;
    }

    private void WriteHeader(int sampleRate, long dataBytes)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 32;
        const ushort formatIeeeFloat = 3;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        ushort blockAlign = channels * (bitsPerSample / 8);

        _fs.Seek(0, SeekOrigin.Begin);
        _bw.Write("RIFF"u8);
        _bw.Write((uint)(HeaderBytes - 8 + dataBytes)); // RIFF chunk size
        _bw.Write("WAVE"u8);
        _bw.Write("fmt "u8);
        _bw.Write(16u);                       // fmt chunk size
        _bw.Write(formatIeeeFloat);           // audio format
        _bw.Write(channels);
        _bw.Write((uint)sampleRate);
        _bw.Write((uint)byteRate);
        _bw.Write(blockAlign);
        _bw.Write(bitsPerSample);
        _bw.Write("data"u8);
        _bw.Write((uint)dataBytes);           // data chunk size
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _bw.Flush();
            WriteHeader(SampleRate, _dataBytes); // patch RIFF + data sizes
            _bw.Flush();
        }
        finally
        {
            _bw.Dispose();
            _fs.Dispose();
        }
    }
}
