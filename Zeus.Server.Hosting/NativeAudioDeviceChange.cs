// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

// Decides which side of a PUT /api/audio/devices actually changes, and rejects
// only the side the operator is trying to change. A stored device ID the
// operator isn't touching this call MUST NOT block the call when it has gone
// stale (e.g. a USB mic that's no longer plugged in): that was #1128, where a
// stale stored input rejected every output-only change so the operator could
// not switch RX audio devices at all.
internal static class NativeAudioDeviceChange
{
    internal readonly record struct Decision(bool ChangeInput, bool ChangeOutput, string? RejectReason);

    public static Decision Decide(
        string? requestedInput,
        string? requestedOutput,
        string? currentInput,
        string? currentOutput,
        MiniAudioDeviceSnapshot snapshot)
    {
        bool changeInput = !string.Equals(requestedInput, currentInput, StringComparison.Ordinal);
        bool changeOutput = !string.Equals(requestedOutput, currentOutput, StringComparison.Ordinal);

        if (changeInput && requestedInput is not null &&
            !snapshot.Inputs.Any(d => d.Id == requestedInput))
        {
            return new Decision(false, false,
                "inputDeviceId is not in the current native input device list");
        }
        if (changeOutput && requestedOutput is not null &&
            !snapshot.Outputs.Any(d => d.Id == requestedOutput))
        {
            return new Decision(false, false,
                "outputDeviceId is not in the current native output device list");
        }

        return new Decision(changeInput, changeOutput, null);
    }
}
