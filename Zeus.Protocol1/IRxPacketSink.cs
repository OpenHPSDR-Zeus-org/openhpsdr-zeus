// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol1;

/// <summary>
/// Synchronous sink for decoded RX packets, invoked DIRECTLY on the
/// <see cref="Protocol1Client"/> RX loop thread. Bypasses the
/// <c>Channel&lt;IqFrame&gt;</c> / <c>Channel&lt;PsFeedbackFrame&gt;</c>
/// hops that otherwise force a consumer task to park on
/// <c>WaitToReadAsync</c> and amplify thread-pool wake-ups.
/// </summary>
/// <remarks>
/// <para>
/// Attach via <see cref="IProtocol1Client.AttachRxSink"/> before
/// <see cref="IProtocol1Client.StartAsync"/> for stable lifetime semantics.
/// When a non-null sink is attached, the RX loop calls these methods INSTEAD
/// of writing to the public <c>ChannelReader</c>s; when no sink is attached,
/// the channel-write path remains live for tests and in-process probes.
/// </para>
/// <para>
/// Threading contract: every method on this interface is invoked on the
/// RX OS thread (see <see cref="Protocol1Client"/> ctor → <c>new Thread(RxLoop)</c>).
/// Implementations MUST:
/// <list type="bullet">
///   <item>Be non-blocking — no async, no I/O, no locks held longer than a
///         few microseconds. Any back-pressure must be enforced by the sink
///         itself (e.g. an SPSC ring with drop-oldest).</item>
///   <item>Not throw. Exceptions thrown from sink methods are caught and
///         logged by the RX loop, then the loop continues. This is a defensive
///         backstop, NOT an expected error channel.</item>
/// </list>
/// </para>
/// </remarks>
public interface IRxPacketSink
{
    /// <summary>
    /// Receive a freshly decoded RX IQ frame. Called on the RX OS thread,
    /// once per successfully parsed DDC0 payload (~1.2 kHz at 192 kSps).
    /// </summary>
    /// <param name="frame">
    /// The decoded frame. <see cref="IqFrame.InterleavedSamples"/> is backed
    /// by a buffer rented from <see cref="System.Buffers.ArrayPool{Double}.Shared"/>
    /// at RX time. Ownership of that buffer transfers to the sink on a
    /// successful (non-throwing) call: the sink — or whoever it forwards the
    /// frame to — MUST return the backing array to <c>ArrayPool&lt;double&gt;.Shared</c>
    /// exactly once after consuming the frame. If the sink throws, the RX
    /// loop returns the buffer itself.
    /// </param>
    void OnIqFrame(in IqFrame frame);

    /// <summary>
    /// Receive a 1024-sample paired PureSignal feedback block (HL2 + PS armed).
    /// Called on the RX OS thread once per full block (~187 blocks/s at 192 kSps).
    /// The buffers inside <see cref="PsFeedbackFrame"/> are plain <c>float[]</c>
    /// allocations (not pooled) — no ArrayPool return is required.
    /// </summary>
    void OnPsFeedbackFrame(in PsFeedbackFrame frame);
}
