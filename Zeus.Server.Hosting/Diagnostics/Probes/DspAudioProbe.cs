// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Zeus.Server;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// DSP + audio-output snapshot. Reports the active DSP engine kind (synthetic
/// vs WDSP) via <see cref="DspPipelineService.CurrentEngine"/>, the audio sink
/// route / mute state from <c>NativeAudioSink</c>, and the AGC + RX/TX leveler
/// configuration from <see cref="RadioService"/>'s cached StateDto. Read-only:
/// only getters and the cached state snapshot are touched.
/// </summary>
public sealed class DspAudioProbe : IDiagnosticProbe
{
    public string Id => "dsp-audio";

    public DiagnosticSection Collect(DiagnosticContext ctx)
    {
        var items = new List<DiagnosticKeyValue>();
        try
        {
            var anyService = false;

            var dsp = ctx.Services.GetService<DspPipelineService>();
            if (dsp is not null)
            {
                anyService = true;
                // No member on IDspEngine names its kind; the concrete runtime
                // type is the authoritative tell (SyntheticDspEngine vs
                // WdspDspEngine). GetType().Name avoids a hard ref to either.
                var engine = dsp.CurrentEngine;
                items.Add(new("dsp.engine", engine is null ? "none" : engine.GetType().Name));
                items.Add(new("dsp.audioOutputRateHz", DspPipelineService.AudioOutputRateHz.ToString()));
                items.Add(new("dsp.monitorBacklog", dsp.MonitorBacklog.ToString()));
            }
            else
            {
                items.Add(new("dsp.engine", "unavailable"));
            }

            // Audio sink: NativeAudioSink owns mute + the active output route.
            var sink = ctx.Services.GetService<NativeAudioSink>();
            if (sink is not null)
            {
                anyService = true;
                items.Add(new("audio.sink", "NativeAudioSink"));
                items.Add(new("audio.muted", sink.IsMuted ? "true" : "false"));
                items.Add(new("audio.previewEnabled", sink.IsEnabled ? "true" : "false"));
                items.Add(new("audio.configuredDeviceId",
                    string.IsNullOrWhiteSpace(sink.ConfiguredOutputDeviceId) ? "default" : sink.ConfiguredOutputDeviceId!));
                items.Add(new("audio.activeDeviceId",
                    string.IsNullOrWhiteSpace(sink.ActiveOutputDeviceId) ? "none" : sink.ActiveOutputDeviceId!));
                try
                {
                    var d = sink.GetDiagnostics();
                    items.Add(new("audio.sampleRateHz", d.SampleRateHz.ToString()));
                    items.Add(new("audio.ringDepthSamples", d.RingDepthSamples.ToString()));
                    items.Add(new("audio.ringCapacitySamples", d.RingCapacitySamples.ToString()));
                    items.Add(new("audio.underrunSamplesTotal", d.UnderrunSamplesTotal.ToString()));
                    items.Add(new("audio.overrunSamplesTotal", d.OverrunSamplesTotal.ToString()));
                    items.Add(new("audio.rebufferEvents", d.RebufferEvents.ToString()));
                    items.Add(new("audio.rebuffering", d.Rebuffering ? "true" : "false"));
                }
                catch (Exception ex)
                {
                    items.Add(new("audio.diagnostics", $"unavailable ({ex.GetType().Name})"));
                }
            }
            else
            {
                items.Add(new("audio.sink", "unavailable"));
            }

            // AGC + leveler config live on the cached StateDto (read-only).
            var radio = ctx.Services.GetService<RadioService>();
            if (radio is not null)
            {
                anyService = true;
                var state = radio.Snapshot();
                items.Add(new("agc.mode", state.Agc?.Mode.ToString() ?? "Med (default)"));
                items.Add(new("agc.topDb", state.AgcTopDb.ToString("0.#")));
                items.Add(new("agc.auto", state.AutoAgcEnabled ? "true" : "false"));

                // RX master AF gain (the closest read-only RX-gain surface; the
                // internal RX-audio leveler runtime state is private to the
                // pipeline and not exposed for read-out).
                items.Add(new("rx.afGainDb", state.RxAfGainDb.ToString("0.#")));

                // TX leveler config (read-only; null => engine defaults).
                if (state.TxLeveling is { } txl)
                {
                    items.Add(new("tx.levelerEnabled", txl.LevelerEnabled ? "true" : "false"));
                    items.Add(new("tx.levelerDecayMs", txl.LevelerDecayMs.ToString()));
                }
                items.Add(new("tx.levelerMaxGainDb", state.LevelerMaxGainDb.ToString("0.#")));
            }

            if (!anyService)
            {
                items.Clear();
                items.Add(new("status", "unavailable"));
            }
        }
        catch (Exception ex)
        {
            items.Add(new("status", "error"));
            items.Add(new("error", ex.GetType().Name));
        }

        return new DiagnosticSection(Id, "DSP & Audio", items);
    }
}
