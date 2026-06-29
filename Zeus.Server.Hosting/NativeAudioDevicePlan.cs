// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Decides which native audio device side(s) to (re)open in response to a
/// device-change request, and which (if any) request a rejection.
///
/// <para>Background (#1128): the SPA's Audio Devices rail sends BOTH the input
/// and the output device id on every change because the PUT contract has no
/// "leave unchanged" sentinel — changing only the output device resends the
/// currently-configured input id alongside the new output id. The old endpoint
/// validated both ids against the live device list and rejected the WHOLE
/// request if either was missing. So when an operator's previously-saved mic
/// was unplugged (or the prefs DB was copied from another machine), every
/// attempt to change the OUTPUT device was rejected with an input-device error
/// and the dropdown snapped back — "Zeus will not allow me to use any of the
/// windows sound devices for RX audio".</para>
///
/// <para>The fix: only validate and apply a side that is actually CHANGING
/// relative to the current configuration. A carried-over (unchanged) id —
/// even one that is now stale — must never block a change to the other side.
/// As a free bonus, re-selecting the same device is a no-op rather than a
/// needless device reopen (which would glitch audio).</para>
/// </summary>
internal static class NativeAudioDevicePlan
{
    internal readonly record struct Result(
        bool ApplyInput,
        bool ApplyOutput,
        string? InputError,
        string? OutputError);

    /// <param name="hasMic">Whether a capture (mic) service is registered.</param>
    /// <param name="currentInput">Currently-configured input device id (normalized), or null for OS default.</param>
    /// <param name="requestedInput">Requested input device id (normalized), or null for OS default.</param>
    /// <param name="availableInputIds">Ids present in the live capture device list.</param>
    /// <param name="hasSink">Whether a playback (RX sink) service is registered.</param>
    /// <param name="currentOutput">Currently-configured output device id (normalized), or null for OS default.</param>
    /// <param name="requestedOutput">Requested output device id (normalized), or null for OS default.</param>
    /// <param name="availableOutputIds">Ids present in the live playback device list.</param>
    internal static Result Plan(
        bool hasMic,
        string? currentInput,
        string? requestedInput,
        IReadOnlyCollection<string> availableInputIds,
        bool hasSink,
        string? currentOutput,
        string? requestedOutput,
        IReadOnlyCollection<string> availableOutputIds)
    {
        var (applyInput, inputError) = PlanSide(
            hasMic, currentInput, requestedInput, availableInputIds,
            "inputDeviceId is not in the current native input device list");
        var (applyOutput, outputError) = PlanSide(
            hasSink, currentOutput, requestedOutput, availableOutputIds,
            "outputDeviceId is not in the current native output device list");

        return new Result(applyInput, applyOutput, inputError, outputError);
    }

    private static (bool Apply, string? Error) PlanSide(
        bool hasService,
        string? current,
        string? requested,
        IReadOnlyCollection<string> available,
        string errorMessage)
    {
        // No service for this side (e.g. server host, or RX-only / TX-only
        // builds): nothing to apply, and a supplied id is silently ignored
        // rather than treated as an error.
        if (!hasService) return (false, null);

        // Unchanged relative to the current configuration → skip entirely.
        // This is the load-bearing case: a stale-but-unchanged carried-over id
        // must never reject the request or reopen a healthy device. (#1128)
        if (string.Equals(requested, current, StringComparison.Ordinal))
            return (false, null);

        // Changing to OS default (null) always validates.
        if (requested is null) return (true, null);

        // Changing to a specific device: it must be in the live list.
        foreach (var id in available)
        {
            if (string.Equals(id, requested, StringComparison.Ordinal))
                return (true, null);
        }
        return (false, errorMessage);
    }
}
