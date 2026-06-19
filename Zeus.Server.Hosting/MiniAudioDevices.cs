// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Zeus.Server;

internal sealed record MiniAudioDeviceInfo(string Id, string Name, bool IsDefault);

internal sealed record MiniAudioDeviceSnapshot(
    IReadOnlyList<MiniAudioDeviceInfo> Outputs,
    IReadOnlyList<MiniAudioDeviceInfo> Inputs)
{
    public static MiniAudioDeviceSnapshot Empty { get; } = new([], []);
}

internal static class MiniAudioDevices
{
    public static MiniAudioDeviceSnapshot Enumerate()
    {
        MiniAudioInterop.EnsureResolverRegistered();
        IntPtr snapshot = MiniAudioInterop.DevicesCreate();
        if (snapshot == IntPtr.Zero) return MiniAudioDeviceSnapshot.Empty;

        try
        {
            return new MiniAudioDeviceSnapshot(
                Outputs: ReadDevices(
                    snapshot,
                    MiniAudioInterop.DevicesPlaybackCount,
                    MiniAudioInterop.DevicesPlaybackId,
                    MiniAudioInterop.DevicesPlaybackName,
                    MiniAudioInterop.DevicesPlaybackDefault),
                Inputs: ReadDevices(
                    snapshot,
                    MiniAudioInterop.DevicesCaptureCount,
                    MiniAudioInterop.DevicesCaptureId,
                    MiniAudioInterop.DevicesCaptureName,
                    MiniAudioInterop.DevicesCaptureDefault));
        }
        finally
        {
            MiniAudioInterop.DevicesDestroy(snapshot);
        }
    }

    private static IReadOnlyList<MiniAudioDeviceInfo> ReadDevices(
        IntPtr snapshot,
        Func<IntPtr, uint> countFn,
        Func<IntPtr, uint, IntPtr> idFn,
        Func<IntPtr, uint, IntPtr> nameFn,
        Func<IntPtr, uint, int> defaultFn)
    {
        uint count = countFn(snapshot);
        if (count == 0) return [];

        var devices = new List<MiniAudioDeviceInfo>(checked((int)count));
        for (uint i = 0; i < count; i++)
        {
            string id = Marshal.PtrToStringUTF8(idFn(snapshot, i)) ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;
            string name = Marshal.PtrToStringUTF8(nameFn(snapshot, i)) ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = $"Audio device {i + 1}";
            devices.Add(new MiniAudioDeviceInfo(id, name, defaultFn(snapshot, i) != 0));
        }
        return devices;
    }
}
