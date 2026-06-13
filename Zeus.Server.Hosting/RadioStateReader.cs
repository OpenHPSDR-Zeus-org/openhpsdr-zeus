// SPDX-License-Identifier: GPL-2.0-or-later
//
// RadioStateReader — host implementation of the plugin-facing IRadioStateReader
// (read-only). Surfaces the operator's current VFO frequency, mode, band, and
// MOX state to plugins holding the ReadRadioState capability, wrapping the same
// RadioService snapshot + change events the UI uses. Band is derived from the
// VFO with BandUtils.FreqToBand, exactly as the in-core Voyeur session metadata
// did. Registered as a singleton and surfaced via IPluginContext.Radio (gated
// on the capability in PluginManager). Copyright (C) 2026 contributors.

using Zeus.Contracts;
using Zeus.Plugins.Contracts;

namespace Zeus.Server;

internal sealed class RadioStateReader : IRadioStateReader, IDisposable
{
    private readonly RadioService _radio;
    private long _lastFreq;
    private string _lastMode;
    private volatile bool _mox;

    public RadioStateReader(RadioService radio)
    {
        _radio = radio;
        var s = _radio.Snapshot();
        _lastFreq = s.VfoHz;
        _lastMode = s.Mode.ToString();
        _radio.StateChanged += OnStateChanged;
        _radio.MoxChanged += OnMoxChanged;
    }

    public long FrequencyHz => _radio.Snapshot().VfoHz;
    public string Mode => _radio.Snapshot().Mode.ToString();
    public string Band => BandUtils.FreqToBand(_radio.Snapshot().VfoHz) ?? "";
    public bool Mox => _mox;

    public event Action<long>? FrequencyChanged;
    public event Action<string>? ModeChanged;
    public event Action<bool>? MoxChanged;

    private void OnStateChanged(StateDto s)
    {
        if (s.VfoHz != _lastFreq)
        {
            _lastFreq = s.VfoHz;
            FrequencyChanged?.Invoke(s.VfoHz);
        }
        var mode = s.Mode.ToString();
        if (!string.Equals(mode, _lastMode, StringComparison.Ordinal))
        {
            _lastMode = mode;
            ModeChanged?.Invoke(mode);
        }
    }

    private void OnMoxChanged(bool on)
    {
        _mox = on;
        MoxChanged?.Invoke(on);
    }

    public void Dispose()
    {
        _radio.StateChanged -= OnStateChanged;
        _radio.MoxChanged -= OnMoxChanged;
    }
}
