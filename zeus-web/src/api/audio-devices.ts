// SPDX-License-Identifier: GPL-2.0-or-later

export interface NativeAudioDevice {
  id: string;
  name: string;
  isDefault: boolean;
}

export interface NativeAudioDevicesResponse {
  supported: boolean;
  inputDeviceId: string | null;
  outputDeviceId: string | null;
  activeInputDeviceId: string | null;
  activeOutputDeviceId: string | null;
  inputs: NativeAudioDevice[];
  outputs: NativeAudioDevice[];
  error?: string | null;
}

export async function fetchNativeAudioDevices(): Promise<NativeAudioDevicesResponse> {
  const res = await fetch('/api/audio/devices');
  if (!res.ok) throw new Error(`GET /api/audio/devices ${res.status}`);
  return (await res.json()) as NativeAudioDevicesResponse;
}

export async function setNativeAudioDevices(
  inputDeviceId: string | null,
  outputDeviceId: string | null,
): Promise<NativeAudioDevicesResponse> {
  const res = await fetch('/api/audio/devices', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ inputDeviceId, outputDeviceId }),
  });
  if (!res.ok) {
    let detail = `${res.status}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body.error) detail = body.error;
    } catch {
      // Fall through to the status code.
    }
    throw new Error(detail);
  }
  return (await res.json()) as NativeAudioDevicesResponse;
}
