// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>Persisted CAT (Kenwood TS-2000 over TCP) server config. Mirrors
/// <see cref="TciRuntimeConfig"/>. Default port 19090 (operator-configurable —
/// there is no universal TCP-CAT port; match it to your client).</summary>
public sealed record CatRuntimeConfig(
    bool Enabled = false,
    string BindAddress = "127.0.0.1",
    int Port = 19090);

/// <summary>CAT server status response. Mirrors <see cref="TciStatus"/>.</summary>
public sealed record CatStatus(
    bool CurrentlyEnabled,
    int CurrentPort,
    string CurrentBindAddress,
    bool PendingEnabled,
    int PendingPort,
    string PendingBindAddress,
    int ClientCount,
    bool PortAvailable,
    bool RequiresRestart,
    string? Error);

/// <summary>CAT port test request. Mirrors <see cref="TciTestRequest"/>.</summary>
public sealed record CatTestRequest(string BindAddress, int Port);

/// <summary>CAT port test result. Mirrors <see cref="TciTestResult"/>.</summary>
public sealed record CatTestResult(bool Ok, string? Error);

// ---- Serial CAT ports (Thetis CAT1–4 parity) ------------------------------
//
// Thetis exposes four general-purpose serial CAT ports, each a functional
// clone of the primary (TCP) CAT port speaking the identical Kenwood TS-2000
// command set. Zeus mirrors that: each configured port feeds the SAME
// CatCommandHandler, so a logger/digimode app can drive Zeus over a virtual
// serial pair (com0com / socat) exactly as it would a real rig. There are
// always <see cref="CatSerialDefaults.PortCount"/> slots; an unconfigured slot
// is simply disabled. Parity / StopBits are carried as the System.IO.Ports
// enum names ("None"/"Odd"/…, "One"/"OnePointFive"/"Two") so the wire DTO has
// no platform type dependency.

/// <summary>Shared constants for the serial CAT feature.</summary>
public static class CatSerialDefaults
{
    /// <summary>Number of serial CAT port slots (Thetis CAT1–4).</summary>
    public const int PortCount = 4;
    /// <summary>Default baud — matches Thetis's effective CAT default (115200).</summary>
    public const int BaudRate = 115200;
    /// <summary>Baud rates offered in the UI (Thetis's combo list).</summary>
    public static readonly int[] BaudRates =
        { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
}

/// <summary>Persisted config for one serial CAT port. Defaults mirror Thetis:
/// disabled, 115200 8-N-1. <paramref name="PortName"/> is a free-form device
/// path (COM5, /dev/cu.usbserial-1, /dev/ttys013) — virtual pty/com0com pairs
/// are not enumerable, so the UI never gates on a dropdown.</summary>
public sealed record CatSerialPortConfig(
    bool Enabled = false,
    string PortName = "",
    int BaudRate = CatSerialDefaults.BaudRate,
    string Parity = "None",
    int DataBits = 8,
    string StopBits = "One");

/// <summary>Full serial-CAT config: exactly <see cref="CatSerialDefaults.PortCount"/>
/// ports, index 0 = CAT 1.</summary>
public sealed record CatSerialConfig(IReadOnlyList<CatSerialPortConfig> Ports);

/// <summary>Live status for one serial CAT port: its stored config plus whether
/// it is currently open and the last open error (busy / not found / permission).</summary>
public sealed record CatSerialPortStatus(
    int Index,
    bool Enabled,
    string PortName,
    int BaudRate,
    string Parity,
    int DataBits,
    string StopBits,
    bool Open,
    int ClientActivity,
    string? Error);

/// <summary>Serial-CAT status response: per-port status plus the host's
/// enumerable serial devices (suggestions only — see <see cref="CatSerialPortConfig"/>).</summary>
public sealed record CatSerialStatus(
    IReadOnlyList<CatSerialPortStatus> Ports,
    IReadOnlyList<string> AvailablePorts);

/// <summary>Probe-open request for one serial CAT port.</summary>
public sealed record CatSerialTestRequest(
    string PortName,
    int BaudRate = CatSerialDefaults.BaudRate,
    string Parity = "None",
    int DataBits = 8,
    string StopBits = "One");

/// <summary>Probe-open result. Mirrors <see cref="CatTestResult"/>.</summary>
public sealed record CatSerialTestResult(bool Ok, string? Error);
