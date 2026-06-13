// SPDX-License-Identifier: GPL-2.0-or-later
//
// RadioController — host implementation of the plugin-facing IRadioController.
// Lets a plugin holding the ControlRadio capability key TX and set VFO / mode,
// wrapping the same TxService / RadioService surfaces the UI and CW engine use.
// Keying is tagged MoxSource.Plugin so the ownership rules apply: the plugin
// releases only what it claimed and the operator's UI MOX stays master
// override. The plugin keys the radio; it never bypasses the band guard or
// connection interlock (TxService enforces both). Copyright (C) 2026
// contributors.

using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Plugins.Contracts;

namespace Zeus.Server;

internal sealed class RadioController : IRadioController
{
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly ILogger<RadioController> _log;

    public RadioController(TxService tx, RadioService radio, ILogger<RadioController> log)
    {
        _tx = tx;
        _radio = radio;
        _log = log;
    }

    public Task SetMoxAsync(bool keyed, CancellationToken ct = default)
    {
        if (!_tx.TrySetMox(keyed, MoxSource.Plugin, out var err))
            _log.LogInformation("plugin MOX {Keyed} refused: {Err}", keyed, err ?? "(no reason)");
        return Task.CompletedTask;
    }

    public Task SetFrequencyAsync(long hz, CancellationToken ct = default)
    {
        _radio.SetVfo(hz);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string mode, CancellationToken ct = default)
    {
        if (Enum.TryParse<RxMode>(mode, ignoreCase: true, out var m))
            _radio.SetMode(m);
        else
            _log.LogWarning("plugin SetMode: unknown mode '{Mode}'", mode);
        return Task.CompletedTask;
    }
}
