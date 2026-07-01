// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

/// <summary>
/// Single "operator wants RX audio silenced" flag shared across every RX audio
/// sink (issue #1252). Desktop-mode Mute button flips this once via
/// <c>/api/audio/native/mute</c> → <see cref="NativeAudioSink.SetMuted"/>; the
/// PC-side playback sink AND the radio-side onboard-speaker sinks
/// (<see cref="RadioSpeakerAudioSink"/> P1, <see cref="SaturnSpeakerAudioSink"/>
/// P2) all read from this state so a single click silences every output path.
///
/// <para>Not persisted: mute is a session-scoped operator control; the app
/// starts unmuted on every launch. In-memory only, thread-safe via a volatile
/// flag (single-bit signal). Registered unconditionally so sinks that live in
/// both host modes can bind to it — server mode has no writer today, which is
/// fine because server-mode operators mute via their browser AudioContext.
/// </para>
/// </summary>
public sealed class RxAudioMuteState
{
    private volatile bool _muted;

    /// <summary>Current mute flag. Cheap volatile read — safe from any thread.</summary>
    public bool IsMuted => _muted;

    /// <summary>Raised after <see cref="SetMuted"/> flips the flag. Subscribers
    /// can use this to drain buffered audio so unmute doesn't replay a stale
    /// tail. Runs on the caller's thread (the REST request thread in
    /// production) — handlers must be non-blocking.</summary>
    public event Action? Changed;

    /// <summary>Set the mute flag. Idempotent: no event fires when the value
    /// is unchanged.</summary>
    public void SetMuted(bool muted)
    {
        if (_muted == muted) return;
        _muted = muted;
        Changed?.Invoke();
    }
}
