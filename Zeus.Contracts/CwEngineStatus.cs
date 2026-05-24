// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

namespace Zeus.Contracts;

/// <summary>
/// Top-level state of the host-side CW engine. Reported on
/// <c>CwEngine.Status</c> events and (in a follow-up PR) broadcast over the
/// streaming hub so the UI macro pad can show queue / playback state.
/// </summary>
public enum CwEngineState : byte
{
    /// <summary>No message in flight, queue empty.</summary>
    Idle = 0,
    /// <summary>Actively playing out a message. MOX is held by Cwx.</summary>
    Sending = 1,
    /// <summary>Graceful stop requested — current symbol finishes, then unkey.</summary>
    Stopping = 2,
    /// <summary>Hard abort requested — drop immediately, queue cleared.</summary>
    Aborting = 3,
}

/// <summary>
/// Snapshot of the CW engine that <c>CwEngine.SendAsync</c> / completion /
/// abort transitions emit on the <c>Status</c> event. <see cref="Text"/> is
/// the message currently being sent (empty when <see cref="State"/> is
/// <see cref="CwEngineState.Idle"/>); <see cref="Wpm"/> is the requested
/// speed of that message. <see cref="QueueDepth"/> counts messages still
/// waiting after the current one.
/// </summary>
/// <remarks>
/// Reason field is optional and only populated on error / abort transitions
/// (e.g. "not in CW mode", "operator abort", "UI override dropped MOX"). It
/// surfaces in server logs and — once the streaming-hub frame lands in
/// PR 2 — in the UI status pill.
/// </remarks>
public sealed record CwEngineStatus(
    CwEngineState State,
    string Text,
    int Wpm,
    int QueueDepth,
    string? Reason = null);
