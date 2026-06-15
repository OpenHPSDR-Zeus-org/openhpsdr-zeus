/*  spnr.c

Signal-preserving noise reduction for experimental Zeus NR5.

The stage is intentionally self-contained: it uses a conservative
overlap-add STFT, online per-bin noise learning, signal-presence memory, and
a bounded output normalizer. It does not persist learned state across runs.
*/

#include "comm.h"

static double spnr_clip(double v, double lo, double hi) {
  return v < lo ? lo : (v > hi ? hi : v);
}

static double spnr_wrap_pi(double v) {
  while (v > PI) { v -= 2.0 * PI; }
  while (v < -PI) { v += 2.0 * PI; }
  return v;
}

static int spnr_next_pow2(int n) {
  int p = 1;
  while (p < n) { p <<= 1; }
  return p;
}

static double spnr_time_alpha(double block, double rate, double tau) {
  if (rate <= 0.0 || tau <= 0.0) { return 0.0; }
  return exp(-block / (rate * tau));
}

static void spnr_calc_window(SPNR a) {
  for (int i = 0; i < a->fsize; i++) {
    a->window[i] = 0.5 - 0.5 * cos(2.0 * PI * (double)i / (double)(a->fsize - 1));
  }
}

static void spnr_calc(SPNR a) {
  int i;
  a->incr = a->fsize / a->ovrlp;

  if (a->fsize > a->bsize) {
    a->iasize = a->fsize;
  } else {
    a->iasize = a->bsize + a->fsize - a->incr;
  }

  a->iainidx = 0;
  a->iaoutidx = 0;

  if (a->fsize > a->bsize) {
    if (a->bsize > a->incr) { a->oasize = a->bsize; }
    else { a->oasize = a->incr; }

    a->oainidx = (a->fsize - a->bsize - a->incr) % a->oasize;
  } else {
    a->oasize = a->bsize;
    a->oainidx = a->fsize - a->incr;
  }

  a->init_oainidx = a->oainidx;
  a->oaoutidx = 0;
  a->msize = a->fsize / 2 + 1;
  a->window = (double*)malloc0(a->fsize * sizeof(double));
  a->inaccum = (double*)malloc0(a->iasize * sizeof(double));
  a->forfftin = (double*)malloc0(a->fsize * sizeof(double));
  a->forfftout = (double*)malloc0(a->msize * sizeof(complex));
  a->revfftin = (double*)malloc0(a->msize * sizeof(complex));
  a->revfftout = (double*)malloc0(a->fsize * sizeof(double));
  a->save = (double**)malloc0(a->ovrlp * sizeof(double*));

  for (i = 0; i < a->ovrlp; i++) {
    a->save[i] = (double*)malloc0(a->fsize * sizeof(double));
  }

  a->outaccum = (double*)malloc0(a->oasize * sizeof(double));
  a->power = (double*)malloc0(a->msize * sizeof(double));
  a->noise = (double*)malloc0(a->msize * sizeof(double));
  a->smooth = (double*)malloc0(a->msize * sizeof(double));
  a->presence = (double*)malloc0(a->msize * sizeof(double));
  a->salience = (double*)malloc0(a->msize * sizeof(double));
  a->prev_phase = (double*)malloc0(a->msize * sizeof(double));
  a->prev_phase_delta = (double*)malloc0(a->msize * sizeof(double));
  a->coherence = (double*)malloc0(a->msize * sizeof(double));
  a->ridge = (double*)malloc0(a->msize * sizeof(double));
  a->floor_bias = (double*)malloc0(a->msize * sizeof(double));
  a->gain = (double*)malloc0(a->msize * sizeof(double));
  a->prev_gain = (double*)malloc0(a->msize * sizeof(double));

  for (i = 0; i < a->msize; i++) {
    a->noise[i] = 1.0e-9;
    a->smooth[i] = 1.0e-9;
    a->floor_bias[i] = 1.0;
    a->gain[i] = 1.0;
    a->prev_gain[i] = 1.0;
  }

  a->learned_frames = 0;
  a->nsamps = 0;
  a->saveidx = 0;
  a->agc_gain = 1.0;
  a->agc_gate = 0.0;
  a->agc_env = 0.0;
  a->diag_input_rms = 0.0;
  a->diag_output_rms = 0.0;
  a->diag_presence_peak = 0.0;
  a->diag_salience_peak = 0.0;
  a->diag_coherence_peak = 0.0;
  a->diag_ridge_peak = 0.0;
  a->diag_mean_gain = 1.0;
  a->diag_min_gain = 1.0;
  a->diag_noise_floor_db = -90.0;
  a->diag_floor_reduction_db = 0.0;
  a->diag_dynamic_range_db = 0.0;
  a->diag_signal_confidence = 0.0;
  a->diag_agc_gate = 0.0;
  a->Rfor = fftw_plan_dft_r2c_1d(a->fsize, a->forfftin, (fftw_complex*)a->forfftout, FFTW_ESTIMATE);
  a->Rrev = fftw_plan_dft_c2r_1d(a->fsize, (fftw_complex*)a->revfftin, a->revfftout, FFTW_ESTIMATE);
  spnr_calc_window(a);
}

static void spnr_decalc(SPNR a) {
  int i;
  _aligned_free(a->prev_gain);
  _aligned_free(a->gain);
  _aligned_free(a->floor_bias);
  _aligned_free(a->ridge);
  _aligned_free(a->coherence);
  _aligned_free(a->prev_phase_delta);
  _aligned_free(a->prev_phase);
  _aligned_free(a->salience);
  _aligned_free(a->presence);
  _aligned_free(a->smooth);
  _aligned_free(a->noise);
  _aligned_free(a->power);
  fftw_destroy_plan(a->Rrev);
  fftw_destroy_plan(a->Rfor);
  _aligned_free(a->outaccum);

  for (i = 0; i < a->ovrlp; i++) {
    _aligned_free(a->save[i]);
  }

  _aligned_free(a->save);
  _aligned_free(a->revfftout);
  _aligned_free(a->revfftin);
  _aligned_free(a->forfftout);
  _aligned_free(a->forfftin);
  _aligned_free(a->inaccum);
  _aligned_free(a->window);
}

SPNR create_spnr(int run, int position, int size, double* in, double* out,
                 int fsize, int ovrlp, int rate) {
  SPNR a = (SPNR)malloc0(sizeof(spnr));
  a->run = run;
  a->position = position;
  a->bsize = size;
  a->in = in;
  a->out = out;
  a->fsize = max(spnr_next_pow2(size), fsize);
  a->ovrlp = ovrlp < 2 ? 2 : ovrlp;
  a->rate = (double)rate;
  a->aggressiveness = 0.62;
  a->agc_run = 1;
  a->target_rms = 0.075;
  a->max_gain = 12.0;
  a->agc_attack = 0.080;
  a->agc_release = 3.000;
  spnr_calc(a);
  return a;
}

void destroy_spnr(SPNR a) {
  spnr_decalc(a);
  _aligned_free(a);
}

void flush_spnr(SPNR a) {
  memset(a->inaccum, 0, a->iasize * sizeof(double));
  memset(a->forfftin, 0, a->fsize * sizeof(double));
  memset(a->forfftout, 0, a->msize * sizeof(complex));
  memset(a->revfftin, 0, a->msize * sizeof(complex));
  memset(a->revfftout, 0, a->fsize * sizeof(double));

  for (int i = 0; i < a->ovrlp; i++) {
    memset(a->save[i], 0, a->fsize * sizeof(double));
  }

  memset(a->outaccum, 0, a->oasize * sizeof(double));
  memset(a->power, 0, a->msize * sizeof(double));
  for (int i = 0; i < a->msize; i++) {
    a->noise[i] = 1.0e-9;
    a->smooth[i] = 1.0e-9;
    a->presence[i] = 0.0;
    a->salience[i] = 0.0;
    a->prev_phase[i] = 0.0;
    a->prev_phase_delta[i] = 0.0;
    a->coherence[i] = 0.0;
    a->ridge[i] = 0.0;
    a->floor_bias[i] = 1.0;
    a->gain[i] = 1.0;
    a->prev_gain[i] = 1.0;
  }

  a->learned_frames = 0;
  a->nsamps = 0;
  a->iainidx = 0;
  a->iaoutidx = 0;
  a->oainidx = a->init_oainidx;
  a->oaoutidx = 0;
  a->saveidx = 0;
  a->agc_gain = 1.0;
  a->agc_gate = 0.0;
  a->agc_env = 0.0;
  a->diag_input_rms = 0.0;
  a->diag_output_rms = 0.0;
  a->diag_presence_peak = 0.0;
  a->diag_salience_peak = 0.0;
  a->diag_coherence_peak = 0.0;
  a->diag_ridge_peak = 0.0;
  a->diag_mean_gain = 1.0;
  a->diag_min_gain = 1.0;
  a->diag_noise_floor_db = -90.0;
  a->diag_floor_reduction_db = 0.0;
  a->diag_dynamic_range_db = 0.0;
  a->diag_signal_confidence = 0.0;
  a->diag_agc_gate = 0.0;
}

static void spnr_calc_gain(SPNR a) {
  const double eps = 1.0e-18;
  const double attack = 0.22;
  const double release = 0.035;
  const double alpha_smooth = spnr_time_alpha((double)a->incr, a->rate, 0.050);

  for (int k = 0; k < a->msize; k++) {
    double re = a->forfftout[2 * k + 0];
    double im = a->forfftout[2 * k + 1];
    a->power[k] = re * re + im * im + eps;
  }

  double prior_island_peak = 0.0;
  int prior_signal_bins = 0;
  int prior_bins = 0;
  if (a->learned_frames >= 12) {
    for (int k = 1; k < a->msize - 1; k++) {
      double left_presence = a->presence[k - 1];
      double right_presence = a->presence[k + 1];
      double neighbor_presence = max(left_presence, right_presence);
      double coherent_guard = max(a->coherence[k], a->ridge[k]);
      double paired_spectral = sqrt(spnr_clip(
        a->presence[k] * max(a->salience[k], 0.35 * neighbor_presence),
        0.0, 1.0));
      double bin_confidence = max(coherent_guard, paired_spectral);
      double left_guard = max(a->coherence[k - 1], a->ridge[k - 1]);
      double right_guard = max(a->coherence[k + 1], a->ridge[k + 1]);
      double neighbor_guard = max(left_guard, right_guard);
      double island_support = max(neighbor_guard, 0.55 * neighbor_presence);
      double island_confidence = sqrt(spnr_clip(bin_confidence * island_support, 0.0, 1.0));
      if (island_confidence > prior_island_peak) { prior_island_peak = island_confidence; }
      if (island_confidence > 0.56) { prior_signal_bins++; }
      prior_bins++;
    }
  }
  double prior_signal_occupancy = prior_bins > 0
    ? (double)prior_signal_bins / (double)prior_bins
    : 1.0;
  double sparse_band = spnr_clip((0.18 - prior_signal_occupancy) / 0.16, 0.0, 1.0)
    * spnr_clip((prior_island_peak - 0.22) / 0.46, 0.0, 1.0);

  for (int k = 0; k < a->msize; k++) {
    double p = a->power[k];
    double re = a->forfftout[2 * k + 0];
    double im = a->forfftout[2 * k + 1];
    double phase = atan2(im, re);
    double phase_delta = spnr_wrap_pi(phase - a->prev_phase[k]);
    double phase_accel = spnr_wrap_pi(phase_delta - a->prev_phase_delta[k]);
    a->prev_phase[k] = phase;
    a->prev_phase_delta[k] = phase_delta;

    if (a->learned_frames < 12) {
      double init_alpha = a->learned_frames == 0 ? 0.0 : 0.75;
      a->noise[k] = init_alpha * a->noise[k] + (1.0 - init_alpha) * p;
      a->smooth[k] = a->noise[k];
      a->presence[k] = 0.0;
      a->salience[k] = 0.0;
      a->coherence[k] = 0.0;
      a->ridge[k] = 0.0;
      a->floor_bias[k] = 1.0;
      a->gain[k] = 1.0;
      continue;
    }

    a->smooth[k] = alpha_smooth * a->smooth[k] + (1.0 - alpha_smooth) * p;
    double snr = p / (a->noise[k] + eps);
    double snr_db = 10.0 * log10(max(snr, eps));
    double left = k > 0 ? a->power[k - 1] : p;
    double right = k < a->msize - 1 ? a->power[k + 1] : p;
    double left2 = k > 1 ? a->power[k - 2] : left;
    double right2 = k < a->msize - 2 ? a->power[k + 2] : right;
    double local_ref = 0.40 * (left + right) + 0.10 * (left2 + right2) + eps;
    double peak_ratio = p / local_ref;
    double peak = spnr_clip((peak_ratio - 1.20) / 3.50, 0.0, 1.0);
    double snr_presence = spnr_clip((snr_db + 6.0) / 20.0, 0.0, 1.0);
    double inst_presence = spnr_clip(0.65 * snr_presence + 0.35 * peak, 0.0, 1.0);

    if (inst_presence > a->presence[k]) {
      a->presence[k] = (1.0 - attack) * a->presence[k] + attack * inst_presence;
    } else {
      a->presence[k] = (1.0 - release) * a->presence[k] + release * inst_presence;
    }

    a->salience[k] = 0.92 * a->salience[k] + 0.08 * peak;
    double phase_stability = 1.0 - spnr_clip(fabs(phase_accel) / PI, 0.0, 1.0);
    double phase_lock = spnr_clip((phase_stability - 0.74) / 0.26, 0.0, 1.0);
    double weak_evidence = spnr_clip(0.62 * snr_presence + 0.38 * peak, 0.0, 1.0);
    double evidence_gate = spnr_clip((max(snr_presence, peak) - 0.22) / 0.44, 0.0, 1.0);
    double coherent_candidate = spnr_clip(
      0.74 * phase_lock * weak_evidence
        + 0.18 * peak * snr_presence
        + 0.08 * peak * phase_lock,
      0.0, 1.0);
    coherent_candidate *= evidence_gate;
    if (snr_db < -3.0 && peak < 0.10 && phase_lock < 0.70) {
      coherent_candidate *= 0.20;
    } else if (snr_db < 1.5 && peak < 0.08 && phase_lock < 0.55) {
      coherent_candidate *= 0.45;
    }

    double coherence_attack = coherent_candidate > a->coherence[k]
      ? 0.08 + 0.12 * evidence_gate
      : 0.050 + 0.050 * (1.0 - evidence_gate);
    a->coherence[k] =
      (1.0 - coherence_attack) * a->coherence[k] + coherence_attack * coherent_candidate;

    double left_presence = k > 0 ? a->presence[k - 1] : a->presence[k];
    double right_presence = k < a->msize - 1 ? a->presence[k + 1] : a->presence[k];
    double neighbor_presence = max(left_presence, right_presence);
    double left_guard = k > 0 ? max(a->coherence[k - 1], a->ridge[k - 1]) : 0.0;
    double right_guard = k < a->msize - 1 ? max(a->coherence[k + 1], a->ridge[k + 1]) : 0.0;
    double neighbor_guard = max(left_guard, right_guard);
    double spectral_evidence = spnr_clip(
      0.52 * a->presence[k] + 0.28 * a->salience[k] + 0.20 * peak,
      0.0, 1.0);
    double island_support = spnr_clip(
      0.50 * neighbor_presence + 0.35 * neighbor_guard + 0.15 * a->salience[k],
      0.0, 1.0);
    double island_gate = spnr_clip((island_support - 0.08) / 0.44, 0.0, 1.0);
    double ridge_candidate = spnr_clip(
      0.60 * a->coherence[k]
        + 0.25 * spectral_evidence
        + 0.15 * island_support * max(a->coherence[k], peak),
      0.0, 1.0);
    double ridge_attack = ridge_candidate > a->ridge[k] ? 0.12 : 0.040;
    a->ridge[k] = (1.0 - ridge_attack) * a->ridge[k] + ridge_attack * ridge_candidate;

    double coherent_guard = max(a->coherence[k], a->ridge[k]);
    double coherent_signal = spnr_clip(
      0.62 * coherent_guard + 0.22 * spectral_evidence + 0.16 * island_support,
      0.0, 1.0);
    coherent_signal *= 0.55 + 0.45 * island_gate;
    double broadband_signal = spnr_clip(
      0.50 * a->presence[k] + 0.35 * neighbor_presence + 0.15 * snr_presence,
      0.0, 1.0);
    broadband_signal *= spnr_clip((snr_db + 3.0) / 12.0, 0.0, 1.0)
      * (0.25 + 0.75 * max(island_gate, coherent_guard));
    double peak_without_lock = peak * (1.0 - phase_lock) * (1.0 - coherent_guard);
    double protect = max(coherent_signal, broadband_signal);
    protect *= (1.0 - 0.30 * peak_without_lock);
    protect = spnr_clip(protect, 0.0, 1.0);

    double noise_alpha = protect > 0.65 ? 0.9994 : protect > 0.35 ? 0.996 : snr < 1.4 ? 0.88 : 0.965;
    double noise_candidate = min(p, a->smooth[k]);
    a->noise[k] = noise_alpha * a->noise[k] + (1.0 - noise_alpha) * noise_candidate;
    if (a->noise[k] > a->smooth[k] * 1.8 && protect < 0.25) {
      a->noise[k] = a->smooth[k] * 1.8;
    }

    double noise_like = spnr_clip(1.0 - protect, 0.0, 1.0);
    double locked_peak = peak * spnr_clip(0.65 * phase_lock + 0.35 * coherent_guard, 0.0, 1.0);
    double orphan_noise = noise_like * (1.0 - island_gate) * (1.0 - 0.50 * locked_peak);
    double sparse_floor = sparse_band * orphan_noise;
    double deep_floor = pow(noise_like, 1.20) * (1.0 - 0.40 * locked_peak);
    deep_floor = spnr_clip(deep_floor, 0.0, 1.0);
    double floor_pressure = 1.0 + a->aggressiveness * (
      0.85 * (1.0 - protect) + 2.95 * deep_floor + 0.85 * sparse_floor);
    floor_pressure = spnr_clip(floor_pressure, 1.0, 5.15);
    double effective_noise = a->noise[k] * floor_pressure;
    double over = 0.92
      + 1.95 * a->aggressiveness * (1.0 - 0.80 * protect)
      + 0.72 * a->aggressiveness * deep_floor
      + 0.38 * a->aggressiveness * sparse_floor;
    double clean_power = max(p - over * effective_noise, 0.0);
    double wiener = sqrt(clean_power / p);
    double floor_gain = 0.006
      + 0.22 * protect
      + 0.10 * a->coherence[k]
      + 0.05 * a->ridge[k]
      + 0.14 * protect * island_gate
      + 0.06 * locked_peak;
    floor_gain = spnr_clip(floor_gain, 0.006, 0.68);
    double target = max(wiener, floor_gain);
    target = spnr_clip(target, 0.006, 1.0);
    a->floor_bias[k] = 1.0 / floor_pressure;

    double temporal = target > a->prev_gain[k]
      ? 0.32 + 0.18 * protect
      : 0.16 + 0.18 * noise_like;
    a->gain[k] = (1.0 - temporal) * a->prev_gain[k] + temporal * target;
    a->prev_gain[k] = a->gain[k];
  }

  double presence_peak = 0.0;
  double salience_peak = 0.0;
  double coherence_peak = 0.0;
  double ridge_peak = 0.0;
  double gain_sum = 0.0;
  double min_gain = 1.0;
  double noise_sum = 0.0;
  double floor_pressure_sum = 0.0;
  double power_peak = 0.0;
  double confidence_sum = 0.0;
  double island_peak = 0.0;
  int strong_confidence_bins = 0;
  int coherent_bins = 0;
  int diag_bins = 0;

  for (int k = 1; k < a->msize - 1; k++) {
    double left_presence = k > 0 ? a->presence[k - 1] : a->presence[k];
    double right_presence = k < a->msize - 1 ? a->presence[k + 1] : a->presence[k];
    double neighbor_presence = max(left_presence, right_presence);
    double coherent_guard = max(a->coherence[k], a->ridge[k]);
    double paired_spectral = sqrt(spnr_clip(
      a->presence[k] * max(a->salience[k], 0.35 * neighbor_presence),
      0.0, 1.0));
    double bin_confidence = max(coherent_guard, paired_spectral);
    double left_guard = max(a->coherence[k - 1], a->ridge[k - 1]);
    double right_guard = max(a->coherence[k + 1], a->ridge[k + 1]);
    double neighbor_guard = max(left_guard, right_guard);
    double island_support = max(neighbor_guard, 0.55 * neighbor_presence);
    double island_confidence = sqrt(spnr_clip(bin_confidence * island_support, 0.0, 1.0));
    if (a->presence[k] > presence_peak) { presence_peak = a->presence[k]; }
    if (a->salience[k] > salience_peak) { salience_peak = a->salience[k]; }
    if (a->coherence[k] > coherence_peak) { coherence_peak = a->coherence[k]; }
    if (a->ridge[k] > ridge_peak) { ridge_peak = a->ridge[k]; }
    if (island_confidence > island_peak) { island_peak = island_confidence; }
    if (a->gain[k] < min_gain) { min_gain = a->gain[k]; }
    if (a->power[k] > power_peak) { power_peak = a->power[k]; }
    gain_sum += a->gain[k];
    noise_sum += a->noise[k];
    floor_pressure_sum += 1.0 / max(a->floor_bias[k], eps);
    confidence_sum += bin_confidence;
    if (bin_confidence > 0.68) { strong_confidence_bins++; }
    if (a->ridge[k] > 0.56 || (a->coherence[k] > 0.62 && a->salience[k] > 0.12)) { coherent_bins++; }
    diag_bins++;
  }

  if (diag_bins > 0) {
    double mean_noise = noise_sum / (double)diag_bins;
    double mean_floor_pressure = floor_pressure_sum / (double)diag_bins;
    double mean_confidence = confidence_sum / (double)diag_bins;
    double confidence_occupancy = (double)strong_confidence_bins / (double)diag_bins;
    double coherent_occupancy = (double)coherent_bins / (double)diag_bins;
    double localized = spnr_clip((0.28 - confidence_occupancy) / 0.18, 0.0, 1.0);
    double coherent_peak = max(coherence_peak, ridge_peak);
    double paired_peak = min(presence_peak, max(salience_peak + 0.08, coherent_peak));
    double peak_signal = max(island_peak, 0.65 * min(coherent_peak, paired_peak));
    a->diag_presence_peak = presence_peak;
    a->diag_salience_peak = salience_peak;
    a->diag_coherence_peak = coherence_peak;
    a->diag_ridge_peak = ridge_peak;
    a->diag_mean_gain = gain_sum / (double)diag_bins;
    a->diag_min_gain = min_gain;
    a->diag_noise_floor_db = 10.0 * log10(max(mean_noise, eps));
    a->diag_floor_reduction_db = 10.0 * log10(max(mean_floor_pressure, eps));
    double effective_floor = mean_noise / max(mean_floor_pressure, 1.0);
    a->diag_dynamic_range_db = 10.0 * log10(max(power_peak / max(effective_floor, eps), eps));
    a->diag_signal_confidence = spnr_clip(
      0.78 * peak_signal * localized
        + 0.12 * mean_confidence * localized
        + 0.10 * sqrt(coherent_occupancy) * localized,
      0.0, 1.0);
  }

  if (a->learned_frames < 1000000) { a->learned_frames++; }
}

static void spnr_apply_output_agc(SPNR a, double* out, int n) {
  double e = 0.0;

  for (int i = 0; i < n; i++) {
    double v = out[2 * i + 0];
    e += v * v;
  }

  double rms = sqrt(e / (double)max(n, 1));
  double coherent_lift =
    spnr_clip((max(a->diag_coherence_peak, a->diag_ridge_peak) - 0.44) / 0.28, 0.0, 1.0)
    * spnr_clip((a->diag_salience_peak - 0.32) / 0.22, 0.0, 1.0);
  double gate_confidence = spnr_clip(a->diag_signal_confidence + 0.16 * coherent_lift, 0.0, 1.0);
  double gate_inst = spnr_clip((gate_confidence - 0.26) / 0.38, 0.0, 1.0);
  if (a->learned_frames < 24) { gate_inst = 0.0; }

  double gate_alpha = gate_inst < a->agc_gate
    ? spnr_time_alpha((double)n, a->rate, 1.600)
    : spnr_time_alpha((double)n, a->rate, 0.650);
  a->agc_gate = gate_alpha * a->agc_gate + (1.0 - gate_alpha) * gate_inst;
  a->diag_agc_gate = a->agc_gate;

  if (a->agc_env <= 1.0e-12) { a->agc_env = rms; }
  double env_alpha = rms > a->agc_env
    ? spnr_time_alpha((double)n, a->rate, 0.045)
    : spnr_time_alpha((double)n, a->rate, 0.950);
  a->agc_env = env_alpha * a->agc_env + (1.0 - env_alpha) * rms;

  double desired = 1.0;
  double gate_drive = spnr_clip((a->agc_gate - 0.10) / 0.60, 0.0, 1.0);
  gate_drive = pow(gate_drive, 1.35);
  if (a->agc_run && gate_drive > 0.0 && a->agc_env > 1.0e-7) {
    double gated_target = a->target_rms * (0.58 + 0.22 * gate_drive);
    double makeup_limit = min(a->max_gain, 6.5);
    double gated_max = 1.0 + gate_drive * (makeup_limit - 1.0);
    desired = spnr_clip(gated_target / a->agc_env, 0.35, gated_max);
  }

  double prev_gain = a->agc_gain;
  if (a->agc_run) {
    double alpha = desired < a->agc_gain
      ? spnr_time_alpha((double)n, a->rate, a->agc_attack)
      : spnr_time_alpha((double)n, a->rate, a->agc_release);
    a->agc_gain = alpha * a->agc_gain + (1.0 - alpha) * desired;
  }

  double out_e = 0.0;
  for (int i = 0; i < n; i++) {
    double v = out[2 * i + 0];
    if (a->agc_run) {
      double frac = (double)(i + 1) / (double)max(n, 1);
      double g = prev_gain + (a->agc_gain - prev_gain) * frac;
      v *= g;
      out[2 * i + 0] = v;
    }
    out_e += v * v;
  }

  a->diag_output_rms = sqrt(out_e / (double)max(n, 1));
}

void xspnr(SPNR a, int pos) {
  if (a->run && pos == a->position) {
    int i, j, k, sbuff, sbegin;
    double scale = 1.0 / (double)(a->fsize * a->ovrlp);
    double input_e = 0.0;

    for (i = 0; i < 2 * a->bsize; i += 2) {
      input_e += a->in[i] * a->in[i];
      a->inaccum[a->iainidx] = a->in[i];
      a->iainidx = (a->iainidx + 1) % a->iasize;
    }

    a->diag_input_rms = sqrt(input_e / (double)max(a->bsize, 1));

    a->nsamps += a->bsize;

    while (a->nsamps >= a->fsize) {
      for (i = 0, j = a->iaoutidx; i < a->fsize; i++, j = (j + 1) % a->iasize) {
        a->forfftin[i] = a->window[i] * a->inaccum[j];
      }

      a->iaoutidx = (a->iaoutidx + a->incr) % a->iasize;
      a->nsamps -= a->incr;
      fftw_execute(a->Rfor);
      spnr_calc_gain(a);

      for (i = 0; i < a->msize; i++) {
        double g = a->gain[i];
        a->revfftin[2 * i + 0] = g * a->forfftout[2 * i + 0];
        a->revfftin[2 * i + 1] = g * a->forfftout[2 * i + 1];
      }

      fftw_execute(a->Rrev);

      for (i = 0; i < a->fsize; i++) {
        a->save[a->saveidx][i] = scale * a->window[i] * a->revfftout[i];
      }

      for (i = a->ovrlp; i > 0; i--) {
        sbuff = (a->saveidx + i) % a->ovrlp;
        sbegin = a->incr * (a->ovrlp - i);

        for (j = sbegin, k = a->oainidx; j < a->incr + sbegin; j++, k = (k + 1) % a->oasize) {
          if (i == a->ovrlp) {
            a->outaccum[k] = a->save[sbuff][j];
          } else {
            a->outaccum[k] += a->save[sbuff][j];
          }
        }
      }

      a->saveidx = (a->saveidx + 1) % a->ovrlp;
      a->oainidx = (a->oainidx + a->incr) % a->oasize;
    }

    for (i = 0; i < a->bsize; i++) {
      a->out[2 * i + 0] = a->outaccum[a->oaoutidx];
      a->out[2 * i + 1] = 0.0;
      a->oaoutidx = (a->oaoutidx + 1) % a->oasize;
    }

    spnr_apply_output_agc(a, a->out, a->bsize);
  } else if (a->out != a->in) {
    memcpy(a->out, a->in, a->bsize * sizeof(complex));
  }
}

void setBuffers_spnr(SPNR a, double* in, double* out) {
  a->in = in;
  a->out = out;
}

void setSamplerate_spnr(SPNR a, int rate) {
  spnr_decalc(a);
  a->rate = (double)rate;
  spnr_calc(a);
}

void setSize_spnr(SPNR a, int size) {
  spnr_decalc(a);
  a->bsize = size;
  a->fsize = max(spnr_next_pow2(size), 4096);
  spnr_calc(a);
}

PORT
void SetRXASPNRRun(int channel, int run) {
  SPNR a = rxa[channel].spnr.p;

  if (a->run != run) {
    RXAbp1Check(channel, rxa[channel].amd.p->run, rxa[channel].snba.p->run,
                rxa[channel].emnr.p->run, rxa[channel].anf.p->run,
                rxa[channel].anr.p->run, rxa[channel].rnnr.p->run,
                rxa[channel].sbnr.p->run, run);
    EnterCriticalSection(&ch[channel].csDSP);
    a->run = run;
    RXAbp1Set(channel);
    LeaveCriticalSection(&ch[channel].csDSP);
  }
}

PORT
void SetRXASPNRPosition(int channel, int position) {
  SPNR a = rxa[channel].spnr.p;
  EnterCriticalSection(&ch[channel].csDSP);
  a->position = position;
  rxa[channel].bp1.p->position = position;
  flush_spnr(a);
  LeaveCriticalSection(&ch[channel].csDSP);
}

PORT
void SetRXASPNRAggressiveness(int channel, double aggressiveness) {
  if (aggressiveness < 0.0 || aggressiveness > 1.0) { return; }
  EnterCriticalSection(&ch[channel].csDSP);
  rxa[channel].spnr.p->aggressiveness = aggressiveness;
  LeaveCriticalSection(&ch[channel].csDSP);
}

PORT
void SetRXASPNRAgcRun(int channel, int run) {
  EnterCriticalSection(&ch[channel].csDSP);
  rxa[channel].spnr.p->agc_run = run ? 1 : 0;
  LeaveCriticalSection(&ch[channel].csDSP);
}

PORT
void SetRXASPNRAgcTarget(int channel, double target) {
  if (target < 0.005 || target > 0.50) { return; }
  EnterCriticalSection(&ch[channel].csDSP);
  rxa[channel].spnr.p->target_rms = target;
  LeaveCriticalSection(&ch[channel].csDSP);
}

PORT
int GetRXASPNRDiagnostics(int channel, int* run, int* position, int* learned_frames,
                          int* agc_run, double* aggressiveness, double* target_rms,
                          double* max_gain, double* agc_gain,
                          double* presence_peak, double* salience_peak,
                          double* mean_gain, double* min_gain,
                          double* noise_floor_db, double* input_rms,
                          double* output_rms) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (run) { *run = a->run; }
  if (position) { *position = a->position; }
  if (learned_frames) { *learned_frames = a->learned_frames; }
  if (agc_run) { *agc_run = a->agc_run; }
  if (aggressiveness) { *aggressiveness = a->aggressiveness; }
  if (target_rms) { *target_rms = a->target_rms; }
  if (max_gain) { *max_gain = a->max_gain; }
  if (agc_gain) { *agc_gain = a->agc_gain; }
  if (presence_peak) { *presence_peak = a->diag_presence_peak; }
  if (salience_peak) { *salience_peak = a->diag_salience_peak; }
  if (mean_gain) { *mean_gain = a->diag_mean_gain; }
  if (min_gain) { *min_gain = a->diag_min_gain; }
  if (noise_floor_db) { *noise_floor_db = a->diag_noise_floor_db; }
  if (input_rms) { *input_rms = a->diag_input_rms; }
  if (output_rms) { *output_rms = a->diag_output_rms; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}

PORT
int GetRXASPNRAdvancedDiagnostics(int channel, double* coherence_peak, double* ridge_peak,
                                  double* floor_reduction_db, double* dynamic_range_db) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (coherence_peak) { *coherence_peak = a->diag_coherence_peak; }
  if (ridge_peak) { *ridge_peak = a->diag_ridge_peak; }
  if (floor_reduction_db) { *floor_reduction_db = a->diag_floor_reduction_db; }
  if (dynamic_range_db) { *dynamic_range_db = a->diag_dynamic_range_db; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}

PORT
int GetRXASPNRDeepDiagnostics(int channel, double* signal_confidence, double* agc_gate) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (signal_confidence) { *signal_confidence = a->diag_signal_confidence; }
  if (agc_gate) { *agc_gate = a->diag_agc_gate; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}
