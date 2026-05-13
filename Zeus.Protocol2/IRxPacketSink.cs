// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol2;

/// <summary>
/// Synchronous sink for decoded RX packets, invoked DIRECTLY on the
/// <see cref="Protocol2Client"/> RX loop thread. Mirrors
/// <c>Zeus.Protocol1.IRxPacketSink</c> in shape — the
/// <c>DspPipelineService</c> implements one of each (the
/// <see cref="IqFrame"/> / <see cref="PsFeedbackFrame"/> types are per-protocol
/// because the protocol projects do not reference each other).
/// </summary>
/// <remarks>
/// <para>
/// Attach via <see cref="Protocol2Client.AttachRxSink"/> before
/// <see cref="Protocol2Client.StartAsync"/>. When a non-null sink is attached,
/// the RX loop calls these methods INSTEAD of writing to the public
/// <c>ChannelReader</c>s; when no sink is attached, the channel-write path
/// remains live as a fallback for tests and in-process probes.
/// </para>
/// <para>
/// Threading contract: every method is invoked on the RX thread (the
/// long-running task spun up by <c>StartAsync</c>). Implementations must be
/// non-blocking and must not throw — exceptions are caught and logged, then
/// the RX loop continues.
/// </para>
/// </remarks>
public interface IRxPacketSink
{
    /// <summary>
    /// Receive a freshly decoded P2 RX IQ frame. Called on the RX thread,
    /// once per successfully parsed DDC payload.
    /// </summary>
    /// <param name="frame">
    /// The decoded frame. Unlike <c>Zeus.Protocol1.IqFrame</c>, P2 currently
    /// backs the interleaved samples with a freshly allocated <c>double[]</c>
    /// per packet (no ArrayPool), so no buffer return is required.
    /// </param>
    void OnIqFrame(in IqFrame frame);

    /// <summary>
    /// Receive a 1024-sample paired PureSignal feedback block. Called on the
    /// RX thread once a full block has been assembled. Buffers inside
    /// <see cref="PsFeedbackFrame"/> are plain <c>float[]</c> allocations —
    /// no ArrayPool return required.
    /// </summary>
    void OnPsFeedbackFrame(in PsFeedbackFrame frame);
}
