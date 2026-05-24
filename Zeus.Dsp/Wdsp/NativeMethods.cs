// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Wdsp;

internal static partial class NativeMethods
{
    internal const string LibraryName = "wdsp";

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void OpenChannel(
        int channel,
        int in_size,
        int dsp_size,
        int input_samplerate,
        int dsp_rate,
        int output_samplerate,
        int type,
        int state,
        double tdelayup,
        double tslewup,
        double tdelaydown,
        double tslewdown,
        int bfo);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseChannel(int channel);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetInputSamplerate(int channel, int samplerate);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetOutputSamplerate(int channel, int samplerate);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAMode(int channel, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXABandpassFreqs(int channel, double f_low, double f_high);

    // SetRXABandpassFreqs alone only updates bp1 (bypassed for SSB). The SSB
    // passband is enforced by nbp0 (notch-bandpass), which is set by
    // RXANBPSetFreqs. SNBA tracks the output bandwidth. Thetis rxa.cs:110-124
    // calls all three together whenever the filter edges change — so do we.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RXANBPSetFreqs(int channel, double f_low, double f_high);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASNBAOutputBandwidth(int channel, double f_low, double f_high);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXABandpassRun(int channel, int run);

    // CTUN frequency shift — WDSP's `shift` stage (wdsp/shift.c). Mirrors the
    // Thetis trio at radio.cs:1419: when the operator clicks off-centre while
    // CTUN is on we shift the IF by (dial - radioLoHz) so the tuned signal
    // lands at baseband for the (unmodified) bandpass filter. Calling
    // SetRXABandpassFreqs with a shifted range works for the bp1 stage but
    // breaks SSB (the nbp0 stage that actually enforces the SSB passband
    // expects sideband-signed values; a negative-going CTUN offset on USB
    // crashes WDSP). The shift stage is the correct seam. Issue #427.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAShiftFreq(int channel, double freq);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAShiftRun(int channel, int run);

    // Paired with SetRXAShiftFreq — Thetis radio.cs:1420 calls both together.
    // nbp0 is the SSB-enforcing notch-bandpass stage and needs the same
    // shift applied to keep its passband centred on the tuned signal.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RXANBPSetShiftFrequency(int channel, double freq);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXABandpassWindow(int channel, int wintype);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAPanelRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAPanelGain1(int channel, double gain);

    // select=3 routes both I and Q into the RXA chain — without it WDSP gets
    // a real-valued signal and cannot separate sidebands (LSB/USB sound identical).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAPanelSelect(int channel, int select);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAPanelBinaural(int channel, int bin);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAMDSBMode(int channel, int sbmode);

    // AGC bindings — mode 3 (MED) is the Thetis default; without this the WDSP
    // output stays near the raw post-demod level (HL2 signals ~2e-5 peak),
    // which reaches the browser as near-silence or whisper-quiet hiss.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCMode(int channel, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCSlope(int channel, int slope);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCTop(int channel, double max_agc);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCAttack(int channel, int attack);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCHang(int channel, int hang);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCDecay(int channel, int decay);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCHangThreshold(int channel, int hangthreshold);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAAGCFixed(int channel, double fixed_agc);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XCreateAnalyzer(
        int disp,
        out int success,
        int m_size,
        int m_LO,
        int m_stitch,
        string? app_data_path);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyAnalyzer(int disp);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetAnalyzer(
        int disp,
        int n_pixout,
        int n_fft,
        int typ,
        ref int flp,
        int sz,
        int bf_sz,
        int win_type,
        double pi_alpha,
        int ovrlp,
        int clp,
        double fscLin,
        double fscHin,
        int n_pix,
        int n_stch,
        int calset,
        double fmin,
        double fmax,
        int max_w);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Spectrum0(int run, int disp, int ss, int LO, ref double pbuff);

    // Pull complex IQ samples from TXA's siphon ring. The siphon sits at
    // xsiphon's position in xtxa, which is BEFORE iqc (PureSignal correction)
    // and BEFORE cfir/rsmpout. Default run=1, mode=0, sipsize=16384 (set
    // inside libwdsp's create_txa). Output buffer is 2*size floats interleaved
    // I,Q,I,Q. This lets the panadapter render the operator's pre-distortion
    // voice spectrum so the trace looks "clean" while PS is converging — the
    // same approach Thetis uses (cmaster.cs:544-545 + siphon.c:268).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TXAGetaSipF1(int channel, ref float pout, int size);

    // Averaging trio. Mode 3 = log-recursive (EMA in dB space) — the Thetis
    // default. Backmult is the per-frame retention factor (0 = no smoothing,
    // 1 = frozen). NumAverage matters for modes 2/4; we're on mode 3 so it
    // just caps the analyzer's internal ring buffer.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetDisplayAverageMode(int disp, int pixout, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetDisplayNumAverage(int disp, int pixout, int num);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetDisplayAvBackmult(int disp, int pixout, double mult);

    // GetPixels has no pixel_ref out-parameter — doc 03 predicted a 5th
    // argument but the shipped ABI is 4-parameter. Writes float[num_pixels] in place.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GetPixels(int disp, int pixout, ref float pix, out int flag);

    // SnapSpectrumTimeout — pulls a raw complex FFT snapshot (Re/Im interleaved
    // doubles) from the analyzer identified by (disp, ss, LO). Blocks up to
    // timeoutMs milliseconds for a fresh frame. flag is set to 1 on success,
    // 0 on timeout. analyzer.c:1633.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SnapSpectrumTimeout(
        int disp,
        int ss,
        int LO,
        ref double snap_buff,
        uint timeoutMs,
        ref int flag);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void fexchange0(
        int channel,
        ref double inInterleavedIq,
        ref double outInterleavedAudio,
        out int error);

    // Noise reduction (post-RXA surface — mirrors the exact subset Thetis exposes).

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANRRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANRVals(int channel, int taps, int delay, double gain, double leakage);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANRPosition(int channel, int position);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANFRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANFVals(int channel, int taps, int delay, double gain, double leakage);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAANFPosition(int channel, int position);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRPosition(int channel, int position);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRgainMethod(int channel, int method);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRnpeMethod(int channel, int method);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRaeRun(int channel, int run);

    // Auto-enhancement tuning. emnr.c:1415,1422 — both take double. Not exposed
    // in Thetis UI; defaults inside create_emnr() apply unless overridden.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRaeZetaThresh(int channel, double zetathresh);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRaePsi(int channel, double psi);

    // EMNR post-processing (post2). Comfort-noise injection that masks
    // residual EMNR artifacts — the psychoacoustic mechanism behind Thetis's
    // smoother-sounding NR2 hiss. emnr.c:1026,1033,1040,1047,1056. Run/Taper
    // are int; Factor/Nlevel/Rate are double.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRpost2Run(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRpost2Factor(int channel, double factor);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRpost2Nlevel(int channel, double nlevel);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRpost2Taper(int channel, int taper);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRpost2Rate(int channel, double tc);

    // EMNR Trained gain-method tuning. emnr.c:1429,1436. T1 is the dB threshold
    // against the trained zetaHat lookup table; T2 is a secondary suppression
    // threshold. Only consulted by WDSP when SetRXAEMNRgainMethod=3 (Trained),
    // but writing them is harmless either way.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRtrainZetaThresh(int channel, double thresh);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXAEMNRtrainT2(int channel, double t2);

    // SBNR (NR4) — libspecbleach spectral-bleaching NR. Symbols defined in
    // native/wdsp/sbnr.c with the float / int signatures shown here.
    // IMPORTANT: requires libwdsp rebuild — Phase 1 of issue #79; the
    // bundled binaries shipped today do NOT export these symbols, so any
    // call here will fail with EntryPointNotFoundException at runtime.
    // The engine guards SBNR-on against the missing library; tests that
    // try to actually flip Sbnr Run=1 are [Skip]'d until Phase 1 lands.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRPosition(int channel, int position);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRreductionAmount(int channel, float amount);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRsmoothingFactor(int channel, float factor);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRwhiteningFactor(int channel, float factor);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRnoiseRescale(int channel, float factor);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRpostFilterThreshold(int channel, float threshold);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASBNRnoiseScalingType(int channel, int noise_scaling_type);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetRXASNBARun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RXANBPSetNotchesRun(int channel, int run);

    // NB1 (EXTANB) — pre-RXA time-domain noise blanker. Setters dereference
    // panb[id] so create_anbEXT MUST be called before any SetEXTANB*/xanbEXT.
    // Buffsize is complex samples (matches WDSP InSize), not doubles.
    [LibraryImport(LibraryName, EntryPoint = "create_anbEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CreateAnbEXT(
        int id,
        int run,
        int buffsize,
        double samplerate,
        double tau,
        double hangtime,
        double advtime,
        double backtau,
        double threshold);

    [LibraryImport(LibraryName, EntryPoint = "destroy_anbEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyAnbEXT(int id);

    [LibraryImport(LibraryName, EntryPoint = "flush_anbEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FlushAnbEXT(int id);

    [LibraryImport(LibraryName, EntryPoint = "xanbEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XanbEXT(int id, ref double @in, ref double @out);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBRun(int id, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBBuffsize(int id, int size);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBSamplerate(int id, int rate);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBTau(int id, double tau);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBHangtime(int id, double time);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBAdvtime(int id, double time);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBBacktau(int id, double tau);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTANBThreshold(int id, double thresh);

    // NB2 (EXTNOB) — pre-RXA impulse-sequence blanker. Same ordering rule as
    // EXTANB: create_nobEXT before any SetEXTNOB*/xnobEXT. create_nobEXT's
    // `slewtime` parameter is applied to both adv_slewtime and hang_slewtime
    // inside wdsp (see nobII.c:636-637).
    [LibraryImport(LibraryName, EntryPoint = "create_nobEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CreateNobEXT(
        int id,
        int run,
        int mode,
        int buffsize,
        double samplerate,
        double slewtime,
        double hangtime,
        double advtime,
        double backtau,
        double threshold);

    [LibraryImport(LibraryName, EntryPoint = "destroy_nobEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyNobEXT(int id);

    [LibraryImport(LibraryName, EntryPoint = "flush_nobEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FlushNobEXT(int id);

    [LibraryImport(LibraryName, EntryPoint = "xnobEXT")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void XnobEXT(int id, ref double @in, ref double @out);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBRun(int id, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBMode(int id, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBBuffsize(int id, int size);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBSamplerate(int id, int rate);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBTau(int id, double tau);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBHangtime(int id, double time);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBAdvtime(int id, double time);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBBacktau(int id, double tau);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetEXTNOBThreshold(int id, double thresh);

    // =================================================================
    // TX bindings — mirror of the RXA subset. TXA is WDSP channel type=1
    // (RXA is type=0) per OpenChannel's `type` param. SetChannelState is
    // channel-generic: state 1=on / 0=off, dmp 1=damp buffers on transition.
    // =================================================================

    // channel.h:82 — returns previous state (int). We usually ignore the return.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetChannelState(int channel, int state, int dmp);

    // fexchange2 (iobuffs.h:91) — TX-side frame exchange. Iin carries mono mic
    // samples, Qin stays silent, Iout/Qout receive modulated I/Q for the P1 EP2
    // outbound packer. INREAL/OUTREAL are `float` (wdsp.h:7-8) — NOT `double`
    // like fexchange0's interleaved buffers.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void fexchange2(
        int channel,
        ref float Iin,
        ref float Qin,
        ref float Iout,
        ref float Qout,
        out int error);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAMode(int channel, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXABandpassFreqs(int channel, double f_low, double f_high);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXABandpassRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXABandpassWindow(int channel, int wintype);

    // Correcting FIR — compensates the sinc droop introduced by TXA's
    // 48k → 192k upsample on P2. P2 firmware requires this on; P1 leaves it
    // off. Thetis audio.cs:1803-1808, pihpsdr transmitter.c:1288.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFIRRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPanelRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPanelGain1(int channel, double gain);

    // ALC (Automatic Level Control) must be on for the SSB modulator to emit
    // non-zero IQ. Upstream TX code carries a "TX ALC on (never switch it
    // off!)" warning. When the ALC is disabled the mic reaches TXA but the
    // modulator outputs 0.0 — confirmed on an HL2 live rig 2026-04-18.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAALCSt(int channel, int state);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAALCAttack(int channel, int attackMs);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAALCDecay(int channel, int decayMs);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAALCMaxGain(int channel, double maxGainDb);

    // Microphone noise gate — we don't want it shaping SSB audio.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAAMSQRun(int channel, int run);

    // TX chain stage enables. WDSP ships these "off" at channel-create time
    // (see native/wdsp/TXA.c) but we assert them explicitly in OpenTxChannel
    // so our TX baseline is deterministic, not a byproduct of library
    // defaults. Enabling/tuning Leveler/Compressor is operator-controlled
    // follow-up work; this worktree only locks in the clean-slate state.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXALevelerSt(int channel, int state);

    // Leveler maximum-gain ceiling. WDSP's SetTXALevelerTop takes the value
    // in dB (wcpAGC.c:648 — converts internally via pow(10, maxgain/20)
    // to the linear `max_gain` field on the wcpagc struct). create_wcpagc
    // (TXA.c:169) ships with max_gain = 1.778 linear ≈ +5 dB; this setter
    // lets the operator widen/narrow the peak-leveling headroom at runtime.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXALevelerTop(int channel, double maxgain);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACompressorRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFCOMPRun(int channel, int run);

    // CFC (Continuous Frequency Compressor — multi-band frequency-domain
    // compressor sitting in xtxa between xeqp and xbandpass). Issue #123.
    // Entry points are case-sensitive — note `prof_i_le` is lowercase per
    // cfcomp.c. Verified via `nm -D libwdsp.so | grep -i CFC`.
    //
    // SetTXACFCOMPprofile takes parallel arrays F[], G[], E[] (frequency Hz,
    // compression dB, post-EQ gain dB) plus optional Q-derivative arrays for
    // the *parametric* mode. Pass IntPtr.Zero for Qg/Qe to select the classic
    // (pihpsdr / PowerSDR) non-parametric mode — cfcomp.c:122-123 falls back
    // to linear interpolation when the Q pointers are NULL. The caller pins
    // F/G/E with `fixed` blocks and passes them via `ref *pF`.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFCOMPprofile(
        int channel,
        int nfreqs,
        ref double F,
        ref double G,
        ref double E,
        IntPtr Qg,
        IntPtr Qe);

    // Frequency-independent pre-compressor gain (dB).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFCOMPPrecomp(int channel, double precomp);

    // Frequency-independent pre-EQ gain (dB).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFCOMPPrePeq(int channel, double prepeq);

    // Post-comp EQ branch enable (separate toggle from the master CFCOMPRun).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXACFCOMPPeqRun(int channel, int run);

    // Per-bin compression-meter readback. `comp_values` must be a pinned
    // double[] sized to the FFT bins; `ready` is set to 1 when a fresh frame
    // is available. Read-only diagnostic — not on the hot path.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GetTXACFCOMPDisplayCompression(
        int channel,
        ref double comp_values,
        out int ready);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPHROTRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAosctrlRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAEQRun(int channel, int run);

    // TUN carrier generator (wdsp.h:586-589) injected post-DSP. Thetis
    // console.cs:18648 uses SetTXAPostGenMode(1) + SetTXAPostGenToneFreq(0.0)
    // + SetTXAPostGenToneMag(mag) to key a steady tone for antenna matching.
    // Same sequence here (see docs/prd/12-tx-feature.md §FR-7).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenMode(int channel, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenToneMag(int channel, double mag);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenToneFreq(int channel, double freq);

    // Two-tone test generator (wdsp.h:590-591). PostGen mode=1 sums two
    // tones at the modulator input. Standard PS calibration excitation —
    // see Thetis PSForm.cs:935-940. Independent of TUN (mode=0).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenTTMag(int channel, double mag1, double mag2);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPostGenTTFreq(int channel, double freq1, double freq2);

    // Pre-DSP generator (injected before the TX chain). pihpsdr transmitter.c
    // :1293-1296 explicitly disables PreGen on TXA open — WDSP's create_channel
    // doesn't guarantee mag=0/run=0, and a residual PreGen tone would appear
    // alongside the PostGen tune tone as a second carrier.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPreGenRun(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPreGenMode(int channel, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPreGenToneMag(int channel, double mag);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPreGenToneFreq(int channel, double freq);

    // TXA Panel input-select. pihpsdr sets this to 2 = "Mic I sample" on open
    // (transmitter.c:1298). Default varies by WDSP build — being explicit.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetTXAPanelSelect(int channel, int select);

    // meter.h:67 — returns a dBm value from RXA's internal meter ring. `mt` is
    // the rxaMeterType ordinal: 0 = RXA_S_PK, 1 = RXA_S_AV (what the S-meter
    // uses), 2 = RXA_ADC_PK, … See Thetis console/dsp.cs:876-884 for the full
    // enum. Thread-safe — WDSP holds a per-meter CRITICAL_SECTION internally.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double GetRXAMeter(int channel, int mt);

    // TXA side. Indices per Thetis TXA.h:49-68 txaMeterType — MIC_AV=1,
    // EQ_AV=3, LVLR_AV=5, LVLR_GAIN=6, CFC_AV=8, CFC_GAIN=9, COMP_AV=11,
    // ALC_AV=13, ALC_GAIN=14, OUT_AV=16. Used for per-stage TX diagnostics
    // to localize where the signal is dropping.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double GetTXAMeter(int channel, int mt);

    // FFTW wisdom: builds or loads cached plans from `directory/wdspWisdom00`.
    // Returns 0 if plans were loaded from file (fast), 1 if newly built and
    // saved (slow — FFTW_PATIENT runs sizes 64..262144). Must be called once
    // before the first OpenChannel so FFTW reuses the cached plans instead of
    // re-planning cold on every launch.
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int WDSPwisdom(string directory);

    // Returns a pointer into a static 128-byte buffer written by the last
    // WDSPwisdom call (e.g. "Wisdom already existed" / "Wisdom created").
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr wisdom_get_status();

    // -- PureSignal (predistortion). Lives inside the TXA channel via
    //    create_calcc / create_iqc (TXA.c:405,424). Symbol set verified
    //    against native/wdsp/wdsp.h. The mox/solidmox args to psccF are
    //    documented as ignored — drive MOX state through SetPSMox instead
    //    (calcc.c:846).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSControl(int channel, int reset, int mancal, int automode, int turnon);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSRunCal(int channel, int run);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSMox(int channel, int mox);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSFeedbackRate(int channel, int rate);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSHWPeak(int channel, double peak);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSPtol(int channel, double ptol);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSPinMode(int channel, int pin);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSMapMode(int channel, int map);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSStabilize(int channel, int stbl);

    // SetPSIntsAndSpi is a heavy restart, not a setter (calcc.c:1132-1151).
    // Only safe to call when not actively calibrating.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSIntsAndSpi(int channel, int ints, int spi);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSMoxDelay(int channel, double delay);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetPSLoopDelay(int channel, double delay);

    // Returns the actual delay applied (clamped). pihpsdr stores the return.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double SetPSTXDelay(int channel, double delay);

    // info[16]: state-machine snapshot. Caller passes a pinned int[16].
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GetPSInfo(int channel, IntPtr info);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GetPSMaxTX(int channel, out double maxtx);

    // pscc float variant (calcc.c:840). Feed paired TX-modulator IQ + RX
    // feedback IQ. Block size is 1024 complex samples per pihpsdr's
    // receiver.c:636. mox/solidmox are dead args — pass 0/0.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void psccF(int channel, int size,
                                       float[] Itxbuff, float[] Qtxbuff,
                                       float[] Irxbuff, float[] Qrxbuff,
                                       int mox, int solidmox);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void PSSaveCorr(int channel, string filename);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void PSRestoreCorr(int channel, string filename);
}
