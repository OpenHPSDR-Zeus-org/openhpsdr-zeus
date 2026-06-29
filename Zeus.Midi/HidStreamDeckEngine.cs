// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using HidSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Midi;

/// <summary>
/// Real Elgato Stream Deck engine over HidSharp (pure-managed cross-platform
/// HID). Enumerates Elgato HID devices (vendor 0x0FD9), opens an input stream
/// per device, and emits a <see cref="StreamDeckInput"/> on every key edge.
///
/// CROSS-PLATFORM / DEGRADE-GRACEFULLY: HID enumeration and open are fully
/// guarded. On Linux/Pi without a udev rule the open fails with a permission
/// error; the engine logs at Debug and simply reports no usable devices instead
/// of throwing, so the server always starts. The per-model key-state report
/// offsets below are the documented Stream Deck layouts but are UNVERIFIED on a
/// bench (no Stream Deck on the G2) — see the PR's bench-gate note.
/// </summary>
public sealed class HidStreamDeckEngine : IStreamDeckEngine
{
    private const int ElgatoVendorId = 0x0FD9;

    // ProductID → (button count, key-state byte offset in the input report).
    // Gen-1 decks put key states at offset 1; gen-2 (MK.2 / XL / Plus / Pedal)
    // at offset 4. Unknown Elgato products fall through to the gen-2 default.
    private static readonly IReadOnlyDictionary<int, (int Buttons, int Offset)> Models =
        new Dictionary<int, (int, int)>
        {
            [0x0060] = (15, 1), // Stream Deck (original)
            [0x0063] = (6, 1),  // Stream Deck Mini
            [0x006C] = (32, 4), // Stream Deck XL
            [0x006D] = (15, 4), // Stream Deck MK.2 / v2
            [0x0080] = (15, 4), // Stream Deck MK.2 (newer rev)
            [0x0084] = (8, 4),  // Stream Deck Plus (8 keys + dials)
            [0x0086] = (6, 4),  // Stream Deck Mini (v2)
            [0x0090] = (15, 4), // Stream Deck MK.2 Scissor
        };

    private readonly ILogger<HidStreamDeckEngine> _log;
    private readonly object _sync = new();
    private readonly List<Reader> _readers = new();
    private bool _available = true;
    private bool _watcherHooked;
    private bool _disposed;

    public HidStreamDeckEngine(ILogger<HidStreamDeckEngine>? log = null)
        => _log = log ?? NullLogger<HidStreamDeckEngine>.Instance;

    public bool IsAvailable => _available;

    public event Action<StreamDeckInput>? InputReceived;
    public event Action? DevicesChanged;

    public IReadOnlyList<StreamDeckDeviceDto> EnumerateDevices()
    {
        var list = new List<StreamDeckDeviceDto>();
        try
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(ElgatoVendorId, null, null, null))
            {
                var (buttons, _) = ResolveModel(dev.ProductID, dev);
                list.Add(new StreamDeckDeviceDto(
                    Name: SafeName(dev),
                    Serial: SafeSerial(dev),
                    ButtonCount: buttons,
                    Connected: true));
            }
        }
        catch (Exception ex)
        {
            _available = false;
            _log.LogDebug(ex, "streamdeck.enumerate failed; engine unavailable");
        }
        return list;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CloseAllLocked();
            HookWatcherLocked();
            try
            {
                foreach (var dev in DeviceList.Local.GetHidDevices(ElgatoVendorId, null, null, null))
                {
                    try
                    {
                        if (!dev.TryOpen(out var stream))
                        {
                            _log.LogDebug("streamdeck.open denied product={Pid:X4} (HID permission?)", dev.ProductID);
                            continue;
                        }
                        var (buttons, offset) = ResolveModel(dev.ProductID, dev);
                        var reader = new Reader(this, dev, stream, SafeName(dev), SafeSerial(dev), buttons, offset);
                        _readers.Add(reader);
                        reader.Start();
                        _log.LogInformation("streamdeck.device.open serial={Serial} buttons={Buttons}",
                            reader.Serial, buttons);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "streamdeck.device.open failed product={Pid:X4}", dev.ProductID);
                    }
                }
            }
            catch (Exception ex)
            {
                _available = false;
                _log.LogDebug(ex, "streamdeck.start failed; engine unavailable");
            }
        }
    }

    public void Stop()
    {
        lock (_sync) CloseAllLocked();
    }

    private void HookWatcherLocked()
    {
        if (_watcherHooked) return;
        try
        {
            DeviceList.Local.Changed += OnDeviceListChanged;
            _watcherHooked = true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "streamdeck.watcher hook failed (hot-plug disabled)");
        }
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        try { DevicesChanged?.Invoke(); }
        catch (Exception ex) { _log.LogDebug(ex, "streamdeck.devices-changed handler threw"); }
    }

    private void CloseAllLocked()
    {
        foreach (var r in _readers) r.Dispose();
        _readers.Clear();
    }

    private void Emit(StreamDeckInput input)
    {
        try { InputReceived?.Invoke(input); }
        catch (Exception ex) { _log.LogDebug(ex, "streamdeck.input handler threw"); }
    }

    private static (int Buttons, int Offset) ResolveModel(int productId, HidDevice dev)
    {
        if (Models.TryGetValue(productId, out var m)) return m;
        // Unknown Elgato HID: assume gen-2 layout, derive a conservative key
        // count from the report length (states are one byte each after offset).
        int len;
        try { len = dev.GetMaxInputReportLength(); }
        catch { len = 0; }
        int buttons = len > 4 ? Math.Min(len - 4, 32) : 15;
        return (buttons, 4);
    }

    private static string SafeName(HidDevice dev)
    {
        try { return dev.GetProductName(); }
        catch { return "Stream Deck"; }
    }

    private static string SafeSerial(HidDevice dev)
    {
        try { return dev.GetSerialNumber(); }
        catch { return $"elgato:{dev.ProductID:X4}"; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseAllLocked();
            if (_watcherHooked)
            {
                try { DeviceList.Local.Changed -= OnDeviceListChanged; }
                catch (Exception ex) { _log.LogDebug(ex, "streamdeck.watcher unhook failed"); }
                _watcherHooked = false;
            }
        }
    }

    /// <summary>Per-device blocking reader. Reads input reports on a background
    /// thread, diffs key states against the previous report, and emits an edge
    /// for every changed key. Self-terminates on any read error (unplug).</summary>
    private sealed class Reader : IDisposable
    {
        private readonly HidStreamDeckEngine _owner;
        private readonly HidDevice _dev;
        private readonly HidStream _stream;
        private readonly int _buttons;
        private readonly int _offset;
        private readonly bool[] _state;
        private readonly CancellationTokenSource _cts = new();
        private Thread? _thread;

        public string Serial { get; }
        private readonly string _name;

        public Reader(HidStreamDeckEngine owner, HidDevice dev, HidStream stream,
            string name, string serial, int buttons, int offset)
        {
            _owner = owner;
            _dev = dev;
            _stream = stream;
            _name = name;
            Serial = serial;
            _buttons = buttons;
            _offset = offset;
            _state = new bool[buttons];
        }

        public void Start()
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"streamdeck:{Serial}",
            };
            _thread.Start();
        }

        private void Loop()
        {
            int reportLen;
            try { reportLen = Math.Max(_dev.GetMaxInputReportLength(), _offset + _buttons + 1); }
            catch { reportLen = _offset + _buttons + 1; }
            var buf = new byte[reportLen];
            try { _stream.ReadTimeout = Timeout.Infinite; } catch { /* best effort */ }

            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = _stream.Read(buf); }
                catch (Exception) { break; } // unplug / closed
                if (n <= _offset) continue;

                for (int i = 0; i < _buttons; i++)
                {
                    int idx = _offset + i;
                    if (idx >= n) break;
                    bool pressed = buf[idx] != 0;
                    if (pressed != _state[i])
                    {
                        _state[i] = pressed;
                        _owner.Emit(new StreamDeckInput(_name, Serial, i, pressed));
                    }
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _stream.Dispose(); } catch { /* ignore */ }
            try { _thread?.Join(250); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
