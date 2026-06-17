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

static int spnr_finite(double v) {
  return v == v && v > -1.0e300 && v < 1.0e300;
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
  a->dry = (double*)malloc0(a->bsize * sizeof(double));
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
  a->signal_prob = (double*)malloc0(a->msize * sizeof(double));
  a->signal_prob_smooth = (double*)malloc0(a->msize * sizeof(double));
  a->salience = (double*)malloc0(a->msize * sizeof(double));
  a->prev_phase = (double*)malloc0(a->msize * sizeof(double));
  a->prev_phase_delta = (double*)malloc0(a->msize * sizeof(double));
  a->coherence = (double*)malloc0(a->msize * sizeof(double));
  a->ridge = (double*)malloc0(a->msize * sizeof(double));
  a->floor_bias = (double*)malloc0(a->msize * sizeof(double));
  a->gain = (double*)malloc0(a->msize * sizeof(double));
  a->prev_gain = (double*)malloc0(a->msize * sizeof(double));
  a->gain_smooth = (double*)malloc0(a->msize * sizeof(double));
  a->prior_snr = (double*)malloc0(a->msize * sizeof(double));

  for (i = 0; i < a->msize; i++) {
    a->noise[i] = 1.0e-9;
    a->smooth[i] = 1.0e-9;
    a->signal_prob[i] = 0.0;
    a->signal_prob_smooth[i] = 0.0;
    a->floor_bias[i] = 1.0;
    a->gain[i] = 1.0;
    a->prev_gain[i] = 1.0;
    a->gain_smooth[i] = 1.0;
    a->prior_snr[i] = 0.0;
  }

  a->learned_frames = 0;
  a->nsamps = 0;
  a->saveidx = 0;
  a->agc_gain = 1.0;
  a->agc_gate = 0.0;
  a->agc_env = 0.0;
  a->agc_level_drive = 0.0;
  a->agc_makeup_gain = 1.0;
  a->agc_recovery_hold = 0.0;
  a->agc_continuity_hold = 0.0;
  a->agc_recent_speech_hold = 0.0;
  a->agc_weak_signal_memory = 0.0;
  a->adjacent_noise_usable = 0;
  a->adjacent_noise_bins = 0;
  a->adjacent_noise_left_bins = 0;
  a->adjacent_noise_right_bins = 0;
  a->adjacent_noise_floor_db = -120.0;
  a->adjacent_noise_p10_db = -120.0;
  a->adjacent_noise_p50_db = -120.0;
  a->adjacent_noise_p90_db = -120.0;
  a->adjacent_noise_left_floor_db = -120.0;
  a->adjacent_noise_right_floor_db = -120.0;
  a->adjacent_noise_slope_db_per_khz = 0.0;
  a->adjacent_noise_rejected_pct = 100.0;
  a->adjacent_noise_trust = 0.0;
  a->adjacent_noise_side_balance = 0.0;
  a->adjacent_noise_asymmetry_db = 0.0;
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
  a->diag_signal_probability = 0.0;
  a->diag_texture_fill = 0.0;
  a->diag_mask_smoothing = 0.0;
  a->diag_signal_confidence = 0.0;
  a->diag_agc_gate = 0.0;
  a->diag_level_drive = 0.0;
  a->diag_recovery_drive = 0.0;
  a->diag_makeup_gain = 1.0;
  a->diag_output_peak = 0.0;
  a->diag_peak_evidence = 0.0;
  a->diag_peak_limit = 0.0;
  a->diag_peak_reduction_db = 0.0;
  a->diag_adjacent_noise_trust = 0.0;
  a->diag_adjacent_noise_drive = 0.0;
  a->Rfor = fftw_plan_dft_r2c_1d(a->fsize, a->forfftin, (fftw_complex*)a->forfftout, FFTW_ESTIMATE);
  a->Rrev = fftw_plan_dft_c2r_1d(a->fsize, (fftw_complex*)a->revfftin, a->revfftout, FFTW_ESTIMATE);
  spnr_calc_window(a);
}

static void spnr_decalc(SPNR a) {
  int i;
  _aligned_free(a->prior_snr);
  _aligned_free(a->gain_smooth);
  _aligned_free(a->prev_gain);
  _aligned_free(a->gain);
  _aligned_free(a->floor_bias);
  _aligned_free(a->ridge);
  _aligned_free(a->coherence);
  _aligned_free(a->prev_phase_delta);
  _aligned_free(a->prev_phase);
  _aligned_free(a->salience);
  _aligned_free(a->signal_prob_smooth);
  _aligned_free(a->signal_prob);
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
  _aligned_free(a->dry);
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
  a->max_gain = 48.0;
  a->agc_attack = 0.420;
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
  memset(a->dry, 0, a->bsize * sizeof(double));
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
    a->signal_prob[i] = 0.0;
    a->signal_prob_smooth[i] = 0.0;
    a->salience[i] = 0.0;
    a->prev_phase[i] = 0.0;
    a->prev_phase_delta[i] = 0.0;
    a->coherence[i] = 0.0;
    a->ridge[i] = 0.0;
    a->floor_bias[i] = 1.0;
    a->gain[i] = 1.0;
    a->prev_gain[i] = 1.0;
    a->gain_smooth[i] = 1.0;
    a->prior_snr[i] = 0.0;
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
  a->agc_level_drive = 0.0;
  a->agc_makeup_gain = 1.0;
  a->agc_recovery_hold = 0.0;
  a->agc_continuity_hold = 0.0;
  a->agc_recent_speech_hold = 0.0;
  a->agc_weak_signal_memory = 0.0;
  a->adjacent_noise_usable = 0;
  a->adjacent_noise_bins = 0;
  a->adjacent_noise_left_bins = 0;
  a->adjacent_noise_right_bins = 0;
  a->adjacent_noise_floor_db = -120.0;
  a->adjacent_noise_p10_db = -120.0;
  a->adjacent_noise_p50_db = -120.0;
  a->adjacent_noise_p90_db = -120.0;
  a->adjacent_noise_left_floor_db = -120.0;
  a->adjacent_noise_right_floor_db = -120.0;
  a->adjacent_noise_slope_db_per_khz = 0.0;
  a->adjacent_noise_rejected_pct = 100.0;
  a->adjacent_noise_trust = 0.0;
  a->adjacent_noise_side_balance = 0.0;
  a->adjacent_noise_asymmetry_db = 0.0;
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
  a->diag_signal_probability = 0.0;
  a->diag_texture_fill = 0.0;
  a->diag_mask_smoothing = 0.0;
  a->diag_signal_confidence = 0.0;
  a->diag_agc_gate = 0.0;
  a->diag_level_drive = 0.0;
  a->diag_recovery_drive = 0.0;
  a->diag_makeup_gain = 1.0;
  a->diag_output_peak = 0.0;
  a->diag_peak_evidence = 0.0;
  a->diag_peak_limit = 0.0;
  a->diag_peak_reduction_db = 0.0;
  a->diag_adjacent_noise_trust = 0.0;
  a->diag_adjacent_noise_drive = 0.0;
}

static void spnr_calc_gain(SPNR a) {
  const double eps = 1.0e-18;
  const double attack = 0.22;
  const double release = 0.035;
  const double alpha_smooth = spnr_time_alpha((double)a->incr, a->rate, 0.050);
  const double alpha_signal_prob = spnr_time_alpha((double)a->incr, a->rate, 0.152);
  const double eps_h1 = 31.622776601683793;
  const double eps_h1r = eps_h1 / (1.0 + eps_h1);

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
  double adjacent_trust = a->adjacent_noise_usable ? spnr_clip(a->adjacent_noise_trust, 0.0, 1.0) : 0.0;
  double adjacent_span_db = a->adjacent_noise_p90_db - a->adjacent_noise_p10_db;
  if (!spnr_finite(adjacent_span_db)) { adjacent_span_db = 12.0; }
  adjacent_span_db = spnr_clip(adjacent_span_db, 0.0, 30.0);
  double adjacent_flatness = spnr_clip((9.0 - adjacent_span_db) / 9.0, 0.0, 1.0);
  double adjacent_reject_guard = spnr_clip((45.0 - a->adjacent_noise_rejected_pct) / 45.0, 0.0, 1.0);
  double adjacent_side_balance = spnr_clip(a->adjacent_noise_side_balance, 0.0, 1.0);
  double adjacent_asymmetry_db = fabs(a->adjacent_noise_asymmetry_db);
  if (!spnr_finite(adjacent_asymmetry_db)) { adjacent_asymmetry_db = 0.0; }
  double adjacent_asymmetry_guard = spnr_clip((8.0 - adjacent_asymmetry_db) / 8.0, 0.0, 1.0);
  double adjacent_side_guard = 0.72 + 0.18 * adjacent_side_balance + 0.10 * adjacent_asymmetry_guard;
  if (a->adjacent_noise_left_bins <= 0 || a->adjacent_noise_right_bins <= 0) {
    adjacent_side_guard *= 0.72;
  }
  adjacent_side_guard = spnr_clip(adjacent_side_guard, 0.0, 1.0);
  double adjacent_clean_guard = adjacent_trust
    * adjacent_flatness
    * adjacent_reject_guard
    * adjacent_side_guard;
  double adjacent_profile_guard = adjacent_trust * (
    0.36 + 0.34 * adjacent_flatness + 0.18 * adjacent_reject_guard + 0.12 * adjacent_side_guard);
  double adjacent_drive_peak = 0.0;
  for (int k = 0; k < a->msize; k++) {
    double p = a->power[k];
    double re = a->forfftout[2 * k + 0];
    double im = a->forfftout[2 * k + 1];
    double phase = atan2(im, re);
    double phase_delta = spnr_wrap_pi(phase - a->prev_phase[k]);
    double phase_accel = spnr_wrap_pi(phase_delta - a->prev_phase_delta[k]);
    double left = k > 0 ? a->power[k - 1] : p;
    double right = k < a->msize - 1 ? a->power[k + 1] : p;
    double left2 = k > 1 ? a->power[k - 2] : left;
    double right2 = k < a->msize - 2 ? a->power[k + 2] : right;
    double local_ref = 0.40 * (left + right) + 0.10 * (left2 + right2) + eps;
    double peak_ratio = p / local_ref;
    double peak = spnr_clip((peak_ratio - 1.20) / 3.50, 0.0, 1.0);
    a->prev_phase[k] = phase;
    a->prev_phase_delta[k] = phase_delta;

    if (a->learned_frames < 12) {
      double init_alpha = a->learned_frames == 0 ? 0.0 : 0.75;
      double seed_guard = spnr_clip((peak_ratio - 1.35) / 2.80, 0.0, 1.0);
      double local_seed = min(p, local_ref * (1.10 + 0.25 * (1.0 - seed_guard)));
      double noise_seed = (1.0 - 0.85 * seed_guard) * p + 0.85 * seed_guard * local_seed;
      a->noise[k] = init_alpha * a->noise[k] + (1.0 - init_alpha) * noise_seed;
      a->smooth[k] = init_alpha * a->smooth[k] + (1.0 - init_alpha) * p;
      a->presence[k] = max(a->presence[k], 0.18 * seed_guard);
      a->signal_prob[k] = max(a->signal_prob[k], seed_guard);
      a->signal_prob_smooth[k] = max(a->signal_prob_smooth[k], 0.20 * seed_guard);
      a->salience[k] = max(a->salience[k], 0.24 * peak);
      a->coherence[k] = 0.0;
      a->ridge[k] = 0.0;
      a->floor_bias[k] = 1.0;
      a->gain[k] = 1.0;
      a->prior_snr[k] = 0.0;
      continue;
    }

    a->smooth[k] = alpha_smooth * a->smooth[k] + (1.0 - alpha_smooth) * p;
    double snr = p / (a->noise[k] + eps);
    double snr_db = 10.0 * log10(max(snr, eps));
    double ph1 = 1.0 / (1.0 + (1.0 + eps_h1) * exp(-eps_h1r * min(snr, 80.0)));
    a->signal_prob_smooth[k] =
      alpha_signal_prob * a->signal_prob_smooth[k] + (1.0 - alpha_signal_prob) * ph1;
    if (a->signal_prob_smooth[k] > 0.99) { ph1 = min(ph1, 0.99); }
    a->signal_prob[k] = ph1;
    double signal_probability = spnr_clip(
      0.72 * ph1 + 0.28 * a->signal_prob_smooth[k],
      0.0, 1.0);
    double probability_presence = spnr_clip((signal_probability - 0.28) / 0.62, 0.0, 1.0);
    double posterior_excess = max(snr - 1.0, 0.0);
    double prev_frame_gain = a->prev_gain[k];
    double snr_presence = spnr_clip((snr_db + 6.0) / 20.0, 0.0, 1.0);
    double inst_presence = spnr_clip(
      0.54 * snr_presence + 0.34 * peak + 0.12 * probability_presence,
      0.0, 1.0);

    if (inst_presence > a->presence[k]) {
      a->presence[k] = (1.0 - attack) * a->presence[k] + attack * inst_presence;
    } else {
      a->presence[k] = (1.0 - release) * a->presence[k] + release * inst_presence;
    }

    a->salience[k] = 0.92 * a->salience[k] + 0.08 * peak;
    double phase_stability = 1.0 - spnr_clip(fabs(phase_accel) / PI, 0.0, 1.0);
    double phase_lock = spnr_clip((phase_stability - 0.74) / 0.26, 0.0, 1.0);
    double weak_evidence = spnr_clip(
      0.54 * snr_presence + 0.34 * peak + 0.12 * probability_presence,
      0.0, 1.0);
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
      0.45 * a->presence[k]
        + 0.24 * a->salience[k]
        + 0.19 * peak
        + 0.07 * probability_presence,
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
    double weak_island_guard = sqrt(spnr_clip(
      max(coherent_signal, coherent_guard) * max(island_gate, 0.65 * neighbor_guard),
      0.0, 1.0));
    double narrow_shape = spnr_clip((peak - 0.14) / 0.42, 0.0, 1.0)
      * spnr_clip((phase_lock - 0.68) / 0.30, 0.0, 1.0);
    double narrow_tone_guard = sqrt(spnr_clip(
      a->presence[k] * max(a->salience[k], max(coherent_guard, peak)),
      0.0, 1.0));
    narrow_tone_guard *= narrow_shape * (0.25 + 0.75 * spnr_clip(
      (coherent_guard - 0.22) / 0.48,
      0.0, 1.0));
    protect = max(protect, narrow_tone_guard);
    weak_island_guard = max(weak_island_guard, narrow_tone_guard);
    double probability_context = spnr_clip(
      (max(island_gate, coherent_guard) - 0.20) / 0.55,
      0.0, 1.0);
    double probability_shape = spnr_clip(
      (max(a->salience[k], peak) - 0.16) / 0.42,
      0.0, 1.0);
    double probability_guard = probability_presence * probability_context * probability_shape;
    protect = max(protect, 0.34 * probability_guard);
    weak_island_guard = max(weak_island_guard, 0.42 * probability_guard);

    double prior_gate = spnr_clip(
      0.46 * max(coherent_signal, coherent_guard)
        + 0.32 * weak_island_guard
        + 0.22 * narrow_tone_guard,
      0.0, 1.0);
    prior_gate *= spnr_clip((island_support - 0.06) / 0.42, 0.0, 1.0);
    double projected_prior = a->prior_snr[k]
      * (0.58 + 0.42 * spnr_clip(prev_frame_gain, 0.0, 1.0));
    double dd_candidate = 0.94 * projected_prior + 0.06 * posterior_excess;
    double prior_decay = 0.58 + 0.34 * prior_gate + 0.06 * max(protect, coherent_guard);
    double gated_prior = prior_gate * dd_candidate;
    a->prior_snr[k] = spnr_clip(max(a->prior_snr[k] * prior_decay, gated_prior), 0.0, 80.0);
    double prior_lift = prior_gate * spnr_clip((a->prior_snr[k] - 0.25) / 6.0, 0.0, 1.0);
    double learned_weak_guard = sqrt(spnr_clip(
      max(prior_lift, 0.46 * probability_guard)
        * max(weak_island_guard, max(coherent_guard, island_gate)),
      0.0, 1.0));
    learned_weak_guard *= spnr_clip(
      (0.62 * a->presence[k] + 0.38 * max(a->salience[k], peak) - 0.10) / 0.62,
      0.0, 1.0);
    protect = max(protect, 0.46 * learned_weak_guard);
    weak_island_guard = max(weak_island_guard, learned_weak_guard);
    double learn_protect = max(protect, max(0.42 * prior_lift, 0.38 * probability_guard));

    double noise_alpha = learn_protect > 0.65 ? 0.9994 : learn_protect > 0.35 ? 0.996 : snr < 1.4 ? 0.88 : 0.965;
    double noise_hold_probability = max(0.25 * signal_probability, probability_guard);
    double expected_noise = (1.0 - noise_hold_probability) * p + noise_hold_probability * a->noise[k];
    double noise_candidate = min(expected_noise, a->smooth[k]);
    a->noise[k] = noise_alpha * a->noise[k] + (1.0 - noise_alpha) * noise_candidate;
    if (a->noise[k] > a->smooth[k] * 1.8 && learn_protect < 0.25) {
      a->noise[k] = a->smooth[k] * 1.8;
    }

    double noise_like = spnr_clip(1.0 - max(protect, 0.18 * prior_lift), 0.0, 1.0);
    double locked_peak = peak * spnr_clip(0.65 * phase_lock + 0.35 * coherent_guard, 0.0, 1.0);
    double orphan_noise = noise_like * (1.0 - island_gate) * (1.0 - 0.50 * locked_peak);
    double sparse_floor = sparse_band * orphan_noise;
    double deep_floor = pow(noise_like, 1.20) * (1.0 - 0.40 * locked_peak);
    deep_floor = spnr_clip(deep_floor, 0.0, 1.0);
    double phase_random = spnr_clip((0.82 - phase_lock) / 0.82, 0.0, 1.0);
    phase_random *= spnr_clip((0.72 - max(coherent_guard, island_gate)) / 0.72, 0.0, 1.0);
    double adjacent_phase_noise = adjacent_clean_guard
      * phase_random
      * pow(noise_like, 0.86)
      * (1.0 - 0.86 * protect)
      * (1.0 - 0.82 * weak_island_guard)
      * (1.0 - 0.82 * learned_weak_guard)
      * (1.0 - 0.78 * probability_guard)
      * (1.0 - 0.72 * narrow_tone_guard);
    adjacent_phase_noise = spnr_clip(adjacent_phase_noise, 0.0, 1.0);
    double adjacent_noise_drive = 0.0;
    if (adjacent_profile_guard > 0.0 && a->learned_frames >= 18) {
      adjacent_noise_drive = adjacent_profile_guard
        * orphan_noise
        * pow(noise_like, 0.72)
        * (1.0 - 0.92 * protect)
        * (1.0 - 0.92 * weak_island_guard)
        * (1.0 - 0.94 * narrow_tone_guard)
        * (1.0 - 0.86 * learned_weak_guard)
        * (1.0 - 0.68 * probability_guard);
      adjacent_noise_drive *= 0.82 + 0.32 * adjacent_phase_noise;
      adjacent_noise_drive = max(adjacent_noise_drive, 0.46 * adjacent_phase_noise * orphan_noise);
      adjacent_noise_drive = spnr_clip(adjacent_noise_drive, 0.0, 1.0);
      if (adjacent_noise_drive > adjacent_drive_peak) { adjacent_drive_peak = adjacent_noise_drive; }
    }
    double floor_pressure = 1.0 + a->aggressiveness * (
      0.88 * (1.0 - protect) + 3.12 * deep_floor + 0.95 * sparse_floor
        + (1.18 + 0.22 * adjacent_clean_guard) * adjacent_noise_drive
        + 0.52 * adjacent_phase_noise);
    floor_pressure = spnr_clip(floor_pressure, 1.0, 5.95);
    floor_pressure *= 1.0 - 0.34 * weak_island_guard;
    floor_pressure *= 1.0 - 0.40 * narrow_tone_guard;
    floor_pressure *= 1.0 - 0.30 * learned_weak_guard;
    floor_pressure *= 1.0 - 0.12 * prior_lift;
    floor_pressure = spnr_clip(floor_pressure, 1.0, 5.95);
    double effective_noise = a->noise[k] * floor_pressure;
    double over = 0.92
      + 1.95 * a->aggressiveness * (1.0 - 0.80 * protect)
      + 0.78 * a->aggressiveness * deep_floor
      + 0.44 * a->aggressiveness * sparse_floor
      + (0.34 + 0.10 * adjacent_clean_guard) * a->aggressiveness * adjacent_noise_drive
      + 0.16 * a->aggressiveness * adjacent_phase_noise;
    over *= 1.0 - 0.22 * weak_island_guard;
    over *= 1.0 - 0.30 * narrow_tone_guard;
    over *= 1.0 - 0.20 * learned_weak_guard;
    over *= 1.0 - 0.10 * prior_lift;
    double clean_power = max(p - over * effective_noise, 0.0);
    double wiener = sqrt(clean_power / p);
    double floor_gain = 0.005
      + 0.22 * protect
      + 0.10 * a->coherence[k]
      + 0.05 * a->ridge[k]
      + 0.14 * protect * island_gate
      + 0.06 * locked_peak
      + 0.16 * weak_island_guard
      + 0.42 * narrow_tone_guard
      + 0.13 * learned_weak_guard
      + 0.075 * prior_lift;
    floor_gain = spnr_clip(floor_gain, 0.005, 0.80);
    double target = max(wiener, floor_gain);
    double dd_snr = max(
      a->prior_snr[k],
      posterior_excess * (0.32 + 0.68 * max(protect, probability_guard)));
    double dd_gain = sqrt(dd_snr / (1.0 + dd_snr + eps));
    double speech_bridge = max(
      max(weak_island_guard, learned_weak_guard),
      max(0.62 * probability_guard, 0.58 * narrow_tone_guard));
    double texture_bridge = sqrt(spnr_clip(
      a->presence[k] * max(a->salience[k], 0.28 * neighbor_presence),
      0.0, 1.0));
    texture_bridge *= spnr_clip((max(snr_presence, peak) - 0.10) / 0.52, 0.0, 1.0)
      * (0.28 + 0.72 * spnr_clip((island_support - 0.030) / 0.450, 0.0, 1.0));
    speech_bridge = max(speech_bridge, 0.58 * texture_bridge);
    speech_bridge *= max(
      spnr_clip((island_support - 0.055) / 0.360, 0.0, 1.0),
      max(
        0.70 * spnr_clip((coherent_guard - 0.125) / 0.420, 0.0, 1.0),
        0.50 * texture_bridge));
    double speech_floor = dd_gain * (0.25 + 0.64 * speech_bridge)
      + 0.052 * speech_bridge;
    speech_floor *= 1.0 - 0.68 * orphan_noise * (1.0 - speech_bridge);
    target = max(target, speech_floor);
    target = spnr_clip(target, 0.005, 1.0);
    a->floor_bias[k] = 1.0 / floor_pressure;

    double drop_hold = spnr_clip(
      1.0 - 0.70 * weak_island_guard - 0.22 * prior_lift
        - 0.26 * learned_weak_guard - 0.18 * speech_bridge,
      0.035, 1.0);
    double temporal = target > a->prev_gain[k]
      ? 0.30 + 0.18 * protect
      : 0.052 + 0.155 * noise_like * drop_hold;
    a->gain[k] = (1.0 - temporal) * a->prev_gain[k] + temporal * target;
    a->prev_gain[k] = a->gain[k];
  }

  a->diag_adjacent_noise_trust = adjacent_trust;
  a->diag_adjacent_noise_drive = adjacent_drive_peak;

  a->diag_texture_fill = 0.0;
  a->diag_mask_smoothing = 0.0;
  if (a->learned_frames >= 18) {
    double pre_sum = 0.0;
    double post_sum = 0.0;
    for (int k = 1; k < a->msize - 1; k++) {
      pre_sum += a->power[k];
      post_sum += a->gain[k] * a->gain[k] * a->power[k];
    }

    double post_pre_ratio = post_sum / max(pre_sum, eps);
    double artifact_mix = spnr_clip((0.62 - post_pre_ratio) / 0.42, 0.0, 1.0);
    double enhancement_mix = spnr_clip((0.76 - post_pre_ratio) / 0.56, 0.0, 1.0);
    artifact_mix = max(artifact_mix, 0.55 * enhancement_mix);
    double texture_fill_peak = 0.0;
    double mask_smoothing_peak = 0.0;
    if (artifact_mix > 0.0) {
      int radius = 1 + (int)(3.0 * artifact_mix + 0.5);
      if (radius > 4) { radius = 4; }

      for (int k = 0; k < a->msize; k++) {
        int lo = k - radius;
        int hi = k + radius;
        if (lo < 0) { lo = 0; }
        if (hi >= a->msize) { hi = a->msize - 1; }

        double sum = 0.0;
        double weight_sum = 0.0;
        for (int m = lo; m <= hi; m++) {
          double w = 1.0 / (1.0 + fabs((double)(m - k)));
          sum += w * a->gain[m];
          weight_sum += w;
        }
        a->gain_smooth[k] = sum / max(weight_sum, eps);
      }

      for (int k = 1; k < a->msize - 1; k++) {
        double neighbor_presence = max(a->presence[k - 1], a->presence[k + 1]);
        double coherent_guard = max(a->coherence[k], a->ridge[k]);
        double paired_guard = sqrt(spnr_clip(
          a->presence[k] * max(a->salience[k], 0.35 * neighbor_presence),
          0.0, 1.0));
        double local_guard = max(coherent_guard, paired_guard);
        double signal_probability = spnr_clip(
          0.72 * a->signal_prob[k] + 0.28 * a->signal_prob_smooth[k],
          0.0, 1.0);
        double probability_shape = spnr_clip((signal_probability - 0.10) / 0.55, 0.0, 1.0)
          * spnr_clip((local_guard - 0.12) / 0.50, 0.0, 1.0);
        double signal_guard = max(local_guard, 0.55 * probability_shape);
        double isolated_excess = spnr_clip(
          (a->gain[k] - a->gain_smooth[k]) / max(a->gain[k], 0.001),
          0.0, 1.0);
        double smooth_mix = artifact_mix * isolated_excess * (1.0 - 0.90 * signal_guard);
        if (smooth_mix > mask_smoothing_peak) { mask_smoothing_peak = smooth_mix; }

        if (smooth_mix > 0.0 && a->gain_smooth[k] < a->gain[k]) {
          a->gain[k] = (1.0 - smooth_mix) * a->gain[k] + smooth_mix * a->gain_smooth[k];
          a->prev_gain[k] = a->gain[k];
        }

        double coherent_shape = spnr_clip((local_guard - 0.24) / 0.42, 0.0, 1.0);
        double hole_depth = spnr_clip(
          (a->gain_smooth[k] - a->gain[k]) / max(a->gain_smooth[k], 0.001),
          0.0, 1.0);
        double fill_shape = max(coherent_shape, 0.60 * probability_shape);
        double fill_mix = enhancement_mix * fill_shape * hole_depth * (0.42 + 0.10 * probability_shape);
        if (fill_mix > texture_fill_peak) { texture_fill_peak = fill_mix; }
        if (fill_mix > 0.0 && a->gain_smooth[k] > a->gain[k]) {
          double lift_cap = min(a->gain_smooth[k], a->gain[k] + 0.20 + 0.16 * fill_shape);
          a->gain[k] = (1.0 - fill_mix) * a->gain[k] + fill_mix * lift_cap;
          a->prev_gain[k] = a->gain[k];
        }
      }
    }
    a->diag_texture_fill = texture_fill_peak;
    a->diag_mask_smoothing = mask_smoothing_peak;
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
  double prior_peak = 0.0;
  double probability_peak = 0.0;
  double probability_sum = 0.0;
  double probability_memory_peak = 0.0;
  double probability_memory_sum = 0.0;
  int strong_confidence_bins = 0;
  int coherent_bins = 0;
  int probability_bins = 0;
  int probability_memory_bins = 0;
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
    double prior_confidence = spnr_clip(a->prior_snr[k] / 8.0, 0.0, 1.0) * island_confidence;
    double signal_probability = spnr_clip(
      0.72 * a->signal_prob[k] + 0.28 * a->signal_prob_smooth[k],
      0.0, 1.0);
    double probability_presence = spnr_clip((signal_probability - 0.28) / 0.62, 0.0, 1.0);
    double probability_context = spnr_clip(
      (max(island_confidence, coherent_guard) - 0.22) / 0.52,
      0.0, 1.0);
    double probability_shape = spnr_clip(
      (0.56 * a->salience[k] + 0.44 * a->presence[k] - 0.42) / 0.44,
      0.0, 1.0);
    double probability_support = probability_presence * probability_context * probability_shape;
    double probability_memory = signal_probability * spnr_clip(
      (max(island_confidence, coherent_guard) - 0.10) / 0.60,
      0.0, 1.0);
    if (a->presence[k] > presence_peak) { presence_peak = a->presence[k]; }
    if (a->salience[k] > salience_peak) { salience_peak = a->salience[k]; }
    if (a->coherence[k] > coherence_peak) { coherence_peak = a->coherence[k]; }
    if (a->ridge[k] > ridge_peak) { ridge_peak = a->ridge[k]; }
    if (island_confidence > island_peak) { island_peak = island_confidence; }
    if (prior_confidence > prior_peak) { prior_peak = prior_confidence; }
    if (probability_support > probability_peak) { probability_peak = probability_support; }
    if (probability_memory > probability_memory_peak) { probability_memory_peak = probability_memory; }
    if (a->gain[k] < min_gain) { min_gain = a->gain[k]; }
    if (a->power[k] > power_peak) { power_peak = a->power[k]; }
    gain_sum += a->gain[k];
    noise_sum += a->noise[k];
    floor_pressure_sum += 1.0 / max(a->floor_bias[k], eps);
    confidence_sum += bin_confidence;
    probability_sum += probability_support;
    probability_memory_sum += probability_memory;
    if (bin_confidence > 0.68) { strong_confidence_bins++; }
    if (a->ridge[k] > 0.56 || (a->coherence[k] > 0.62 && a->salience[k] > 0.12)) { coherent_bins++; }
    if (probability_support > 0.62) { probability_bins++; }
    if (probability_memory > 0.30) { probability_memory_bins++; }
    diag_bins++;
  }

  if (diag_bins > 0) {
    double mean_noise = noise_sum / (double)diag_bins;
    double mean_floor_pressure = floor_pressure_sum / (double)diag_bins;
    double mean_confidence = confidence_sum / (double)diag_bins;
    double mean_probability = probability_sum / (double)diag_bins;
    double mean_probability_memory = probability_memory_sum / (double)diag_bins;
    double confidence_occupancy = (double)strong_confidence_bins / (double)diag_bins;
    double coherent_occupancy = (double)coherent_bins / (double)diag_bins;
    double probability_occupancy = (double)probability_bins / (double)diag_bins;
    double probability_memory_occupancy = (double)probability_memory_bins / (double)diag_bins;
    double localized = spnr_clip((0.28 - confidence_occupancy) / 0.18, 0.0, 1.0);
    double coherent_peak = max(coherence_peak, ridge_peak);
    double paired_peak = min(presence_peak, max(salience_peak + 0.08, coherent_peak));
    double peak_signal = max(island_peak, 0.65 * min(coherent_peak, paired_peak));
    peak_signal = max(peak_signal, 0.42 * prior_peak);
    double probability_signal = spnr_clip(
      0.76 * probability_peak * localized
        + 0.14 * mean_probability * localized
        + 0.10 * sqrt(probability_occupancy) * localized,
      0.0, 1.0);
    double probability_memory_signal = spnr_clip(
      0.74 * probability_memory_peak * localized
        + 0.16 * mean_probability_memory * localized
        + 0.10 * sqrt(probability_memory_occupancy) * localized,
      0.0, 1.0);
    probability_signal = max(probability_signal, 0.42 * probability_memory_signal);
    peak_signal = max(peak_signal, 0.58 * probability_signal);
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
    a->diag_signal_probability = probability_signal;
    a->diag_signal_confidence = spnr_clip(
      0.64 * peak_signal * localized
        + 0.12 * mean_confidence * localized
        + 0.08 * sqrt(coherent_occupancy) * localized
        + 0.08 * prior_peak * localized
        + 0.08 * probability_signal,
      0.0, 1.0);
  }

  if (a->learned_frames < 1000000) { a->learned_frames++; }
}

static void spnr_apply_output_agc(SPNR a, double* out, int n) {
  double e = 0.0;
  double prior_weak_signal_memory = a->agc_weak_signal_memory;
  double prior_recent_speech_hold = a->agc_recent_speech_hold;

  for (int i = 0; i < n; i++) {
    double v = out[2 * i + 0];
    e += v * v;
  }

  double rms = sqrt(e / (double)max(n, 1));
  double coherent_lift =
    spnr_clip((max(a->diag_coherence_peak, a->diag_ridge_peak) - 0.44) / 0.28, 0.0, 1.0)
    * spnr_clip((a->diag_salience_peak - 0.32) / 0.22, 0.0, 1.0);
  double coherent_drive = spnr_clip(
    (max(a->diag_coherence_peak, a->diag_ridge_peak) - 0.34) / 0.34,
    0.0, 1.0);
  double probability_drive = spnr_clip((a->diag_signal_probability - 0.16) / 0.42, 0.0, 1.0);
  double gate_confidence = spnr_clip(
    a->diag_signal_confidence + 0.18 * coherent_lift + 0.10 * probability_drive,
    0.0, 1.0);
  double signal_drive = spnr_clip((gate_confidence - 0.18) / 0.30, 0.0, 1.0);
  double spectral_drive = max(
    spnr_clip((a->diag_mean_gain - 0.10) / 0.30, 0.0, 1.0),
    0.70 * coherent_drive);
  double weak_recovery_drive = signal_drive * (0.25 + 0.75 * max(spectral_drive, coherent_lift));
  double gate_inst = spnr_clip((gate_confidence - 0.18) / 0.34, 0.0, 1.0);
  double weak_input_drive = a->target_rms > 1.0e-9
    ? spnr_clip((0.85 * a->target_rms - a->diag_input_rms) / (0.65 * a->target_rms), 0.0, 1.0)
    : 0.0;
  double faint_evidence = sqrt(
    spnr_clip((a->diag_presence_peak - 0.48) / 0.32, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_salience_peak - 0.34) / 0.24, 0.0, 1.0),
      0.75 * probability_drive));
  double faint_recovery_drive = weak_input_drive
    * faint_evidence
    * spnr_clip((gate_confidence - 0.20) / 0.28, 0.0, 1.0);
  if (faint_recovery_drive > 0.035) {
    weak_recovery_drive = max(weak_recovery_drive, 0.14 + 0.48 * faint_recovery_drive);
  }
  double weak_memory_relief = spnr_clip(
    (a->agc_weak_signal_memory - 0.16) / 0.42,
    0.0, 1.0)
    * max(
      spnr_clip((gate_confidence - 0.255) / 0.145, 0.0, 1.0),
      0.58 * spnr_clip((a->diag_mask_smoothing - 0.300) / 0.160, 0.0, 1.0));
  double low_confidence_noise = spnr_clip((0.36 - a->diag_signal_confidence) / 0.22, 0.0, 1.0);
  double low_probability_noise = spnr_clip((0.24 - a->diag_signal_probability) / 0.24, 0.0, 1.0);
  double low_gate_noise = spnr_clip((0.54 - gate_inst) / 0.36, 0.0, 1.0);
  double low_evidence_noise_drive = weak_input_drive * spnr_clip(
    0.46 * low_confidence_noise
      + 0.34 * low_probability_noise
      + 0.20 * low_gate_noise
      - 0.10 * coherent_lift,
    0.0, 1.0);
  low_evidence_noise_drive *= 1.0 - 0.38 * weak_memory_relief;
  double borderline_speech_relief = weak_input_drive
    * spnr_clip((gate_confidence - 0.255) / 0.145, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.165) / 0.145, 0.0, 1.0),
      0.48 * spnr_clip((a->diag_mask_smoothing - 0.285) / 0.190, 0.0, 1.0))
    * spnr_clip((a->diag_recovery_drive - 0.105) / 0.255, 0.0, 1.0);
  borderline_speech_relief *= 1.0 - 0.58 * spnr_clip(
    (low_evidence_noise_drive - 0.58) / 0.32,
    0.0, 1.0);
  if (borderline_speech_relief > 0.0) {
    low_evidence_noise_drive *= 1.0 - 0.26 * borderline_speech_relief;
    weak_recovery_drive = max(
      weak_recovery_drive,
      0.080 + 0.235 * borderline_speech_relief);
  }
  double adjacent_suppressed_relief = 0.0;
  if (a->adjacent_noise_usable && a->learned_frames >= 24) {
    double adjacent_context = sqrt(spnr_clip(
      spnr_clip((a->adjacent_noise_trust - 0.58) / 0.28, 0.0, 1.0)
        * spnr_clip((a->diag_adjacent_noise_drive - 0.46) / 0.36, 0.0, 1.0),
      0.0, 1.0));
    double suppressed_texture = max(
      spnr_clip((a->diag_mask_smoothing - 0.315) / 0.150, 0.0, 1.0),
      max(
        0.82 * spnr_clip((a->diag_signal_probability - 0.105) / 0.115, 0.0, 1.0),
        0.62 * spnr_clip((a->diag_signal_confidence - 0.255) / 0.115, 0.0, 1.0)));
    double suppressed_input = a->target_rms > 1.0e-9
      ? spnr_clip((a->diag_input_rms - a->target_rms * 0.010) / (a->target_rms * 0.075), 0.0, 1.0)
      : 0.0;
    double weak_input_guard = a->target_rms > 1.0e-9
      ? spnr_clip((a->target_rms * 0.90 - a->diag_input_rms) / (a->target_rms * 0.80), 0.0, 1.0)
      : 0.0;
    adjacent_suppressed_relief = adjacent_context
      * suppressed_texture
      * suppressed_input
      * weak_input_guard
      * spnr_clip((gate_confidence - 0.210) / 0.170, 0.0, 1.0);
    adjacent_suppressed_relief *= 1.0 - 0.62 * spnr_clip(
      (low_evidence_noise_drive - 0.82) / 0.18,
      0.0, 1.0);
    if (adjacent_suppressed_relief > 0.0) {
      low_evidence_noise_drive *= 1.0 - 0.36 * adjacent_suppressed_relief;
      weak_recovery_drive = max(
        weak_recovery_drive,
        0.070 + 0.260 * adjacent_suppressed_relief);
      gate_inst = max(
        gate_inst,
        0.115 + 0.285 * adjacent_suppressed_relief);
    }
  }
  double weak_fragment_continuity_relief = weak_input_drive
    * max(
      spnr_clip((prior_weak_signal_memory - 0.018) / 0.165, 0.0, 1.0),
      0.62 * spnr_clip((prior_recent_speech_hold - 0.035) / 0.240, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.315) / 0.135, 0.0, 1.0),
      spnr_clip((a->diag_signal_probability - 0.105) / 0.095, 0.0, 1.0))
    * spnr_clip((gate_confidence - 0.245) / 0.105, 0.0, 1.0);
  weak_fragment_continuity_relief *= 1.0 - 0.76 * spnr_clip(
    (low_evidence_noise_drive - 0.68) / 0.24,
    0.0, 1.0);
  if (weak_fragment_continuity_relief > 0.0) {
    low_evidence_noise_drive *= 1.0 - 0.34 * weak_fragment_continuity_relief;
    weak_recovery_drive = max(
      weak_recovery_drive,
      0.095 + 0.335 * weak_fragment_continuity_relief);
    gate_inst = max(
      gate_inst,
      0.145 + 0.285 * weak_fragment_continuity_relief);
  }
  double weak_tail_continuity_relief = weak_input_drive
    * max(
      spnr_clip((prior_weak_signal_memory - 0.070) / 0.400, 0.0, 1.0),
      0.78 * spnr_clip((prior_recent_speech_hold - 0.045) / 0.300, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.270) / 0.175, 0.0, 1.0),
      max(
        0.70 * spnr_clip((a->diag_signal_probability - 0.060) / 0.145, 0.0, 1.0),
        0.58 * spnr_clip((gate_confidence - 0.190) / 0.150, 0.0, 1.0)))
    * spnr_clip((gate_confidence - 0.175) / 0.165, 0.0, 1.0);
  double weak_tail_memory_bridge = weak_input_drive
    * max(
      spnr_clip((prior_weak_signal_memory - 0.030) / 0.340, 0.0, 1.0),
      0.72 * spnr_clip((prior_recent_speech_hold - 0.025) / 0.300, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.245) / 0.200, 0.0, 1.0),
      max(
        0.78 * spnr_clip((a->diag_signal_probability - 0.090) / 0.135, 0.0, 1.0),
        0.52 * spnr_clip((gate_confidence - 0.225) / 0.150, 0.0, 1.0)))
    * spnr_clip((gate_confidence - 0.220) / 0.150, 0.0, 1.0);
  weak_tail_memory_bridge *= 1.0 - 0.70 * spnr_clip(
    (low_evidence_noise_drive - 0.82) / 0.18,
    0.0, 1.0);
  weak_tail_continuity_relief = max(
    weak_tail_continuity_relief,
    min(weak_tail_memory_bridge, 0.72));
  weak_tail_continuity_relief *= 1.0 - 0.66 * spnr_clip(
    (low_evidence_noise_drive - 0.78) / 0.22,
    0.0, 1.0);
  if (weak_tail_continuity_relief > 0.0) {
    low_evidence_noise_drive *= 1.0 - 0.32 * weak_tail_continuity_relief;
    weak_recovery_drive = max(
      weak_recovery_drive,
      0.075 + 0.310 * weak_tail_continuity_relief);
    gate_inst = max(
      gate_inst,
      0.145 + 0.285 * weak_tail_continuity_relief);
  }
  if (low_evidence_noise_drive > 0.0) {
    double low_evidence_hold = 1.0 - 0.70 * low_evidence_noise_drive;
    low_evidence_hold = max(
      low_evidence_hold,
      1.0 - (0.70
        - 0.18 * weak_fragment_continuity_relief
        - 0.28 * weak_tail_continuity_relief) * low_evidence_noise_drive);
    gate_inst *= 1.0 - (0.45
      - 0.10 * weak_fragment_continuity_relief
      - 0.16 * weak_tail_continuity_relief) * low_evidence_noise_drive;
    weak_recovery_drive *= low_evidence_hold;
  }
  double learned_weak_speech_inst = weak_input_drive
    * spnr_clip((gate_confidence - 0.270) / 0.130, 0.0, 1.0)
    * max(
      spnr_clip((gate_inst - 0.34) / 0.38, 0.0, 1.0),
      0.56 * spnr_clip((a->diag_recovery_drive - 0.100) / 0.240, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.285) / 0.175, 0.0, 1.0),
      0.62 * spnr_clip((a->diag_signal_probability - 0.100) / 0.125, 0.0, 1.0));
  learned_weak_speech_inst *= 1.0 - 0.86 * spnr_clip(
    (low_evidence_noise_drive - 0.18) / 0.42,
    0.0, 1.0);
  if (weak_tail_continuity_relief > 0.0) {
    learned_weak_speech_inst = max(
      learned_weak_speech_inst,
      0.040 + 0.210 * weak_tail_continuity_relief);
  }
  double learned_weak_alpha = learned_weak_speech_inst > a->agc_weak_signal_memory
    ? spnr_time_alpha((double)n, a->rate, 0.055)
    : spnr_time_alpha((double)n, a->rate,
      0.780 + 0.640 * low_evidence_noise_drive
        + 0.420 * weak_fragment_continuity_relief
        + 0.520 * weak_tail_continuity_relief);
  a->agc_weak_signal_memory = learned_weak_alpha * a->agc_weak_signal_memory
    + (1.0 - learned_weak_alpha) * learned_weak_speech_inst;
  double weak_memory_noise_cut = spnr_clip(
    (low_evidence_noise_drive
        - (0.48 + 0.13 * weak_fragment_continuity_relief
          + 0.16 * weak_tail_continuity_relief))
      / (0.34 + 0.10 * weak_fragment_continuity_relief
        + 0.12 * weak_tail_continuity_relief),
    0.0, 1.0);
  if (weak_memory_noise_cut > 0.0) {
    a->agc_weak_signal_memory *= 1.0 - (0.62
        - 0.20 * weak_fragment_continuity_relief
        - 0.18 * weak_tail_continuity_relief) * spnr_clip(
      weak_memory_noise_cut,
      0.0, 1.0);
  }
  if (a->agc_weak_signal_memory > 0.006) {
    weak_recovery_drive = max(
      weak_recovery_drive,
      0.15 + 0.62 * a->agc_weak_signal_memory);
    gate_inst = max(gate_inst, 0.22 + 0.58 * a->agc_weak_signal_memory);
  }
  if (a->learned_frames < 24) { gate_inst = 0.0; }

  double gate_release_tau = low_evidence_noise_drive > 0.0
    ? 0.240 + 1.260 * (1.0 - low_evidence_noise_drive)
    : 3.000;
  double gate_alpha = gate_inst < a->agc_gate
    ? spnr_time_alpha((double)n, a->rate, gate_release_tau)
    : spnr_time_alpha((double)n, a->rate, 0.420);
  a->agc_gate = gate_alpha * a->agc_gate + (1.0 - gate_alpha) * gate_inst;
  a->diag_agc_gate = a->agc_gate;

  if (a->agc_env <= 1.0e-12) { a->agc_env = rms; }
  double env_drive = spnr_clip(a->agc_gate, 0.0, 1.0);
  double env_drop = a->agc_env > rms && a->agc_env > 1.0e-9
    ? spnr_clip((a->agc_env - rms) / a->agc_env, 0.0, 1.0)
    : 0.0;
  double env_release_drive = spnr_clip(weak_recovery_drive * env_drop, 0.0, 1.0);
  double slow_env_release = spnr_clip(a->agc_release, 0.600, 4.500);
  double fast_env_release = 0.360 + 0.340 * (1.0 - signal_drive);
  double env_release = slow_env_release
    - (slow_env_release - fast_env_release) * env_release_drive;
  double env_alpha = rms > a->agc_env
    ? spnr_time_alpha((double)n, a->rate, 0.035 + 0.030 * (1.0 - env_drive))
    : spnr_time_alpha((double)n, a->rate, env_release);
  a->agc_env = env_alpha * a->agc_env + (1.0 - env_alpha) * rms;

  double desired = 1.0;
  double gate_drive = spnr_clip((a->agc_gate - 0.08) / 0.44, 0.0, 1.0);
  double level_inst = pow(max(gate_drive, 0.85 * weak_recovery_drive), 0.70);
  level_inst *= 1.0 - (0.58
    - 0.12 * weak_fragment_continuity_relief
    - 0.20 * weak_tail_continuity_relief) * low_evidence_noise_drive;
  double level_release_tau = low_evidence_noise_drive > 0.0
    ? 0.420 + 1.200 * (1.0 - low_evidence_noise_drive)
    : 3.200;
  double level_alpha = level_inst < a->agc_level_drive
    ? spnr_time_alpha((double)n, a->rate, level_release_tau)
    : spnr_time_alpha((double)n, a->rate, 0.180);
  a->agc_level_drive = level_alpha * a->agc_level_drive + (1.0 - level_alpha) * level_inst;
  double level_drive = spnr_clip(a->agc_level_drive, 0.0, 1.0);
  a->diag_level_drive = level_drive;
  if (a->agc_run && level_drive > 0.0 && a->agc_env > 1.0e-7) {
    double gated_target = a->target_rms * (0.88 + 0.12 * level_drive);
    double gated_max = 1.0 + level_drive * (a->max_gain - 1.0);
    desired = spnr_clip(gated_target / a->agc_env, 0.22, gated_max);
  }

  double prev_gain = a->agc_gain;
  if (a->agc_run) {
    double recovery_drive = max(level_drive, weak_recovery_drive);
    double alpha = desired < a->agc_gain
      ? spnr_time_alpha((double)n, a->rate, a->agc_attack)
      : spnr_time_alpha((double)n, a->rate, 0.560 + 1.840 * (1.0 - recovery_drive));
    double next_gain = alpha * a->agc_gain + (1.0 - alpha) * desired;
    double block_seconds = a->rate > 0.0 ? (double)n / a->rate : 0.0;
    double max_up = pow(10.0, ((5.5 + 16.5 * recovery_drive) * block_seconds) / 20.0);
    double max_down = pow(10.0, ((3.5 + 5.0 * level_drive) * block_seconds) / 20.0);
    if (max_up > 1.0 && next_gain > a->agc_gain * max_up) {
      next_gain = a->agc_gain * max_up;
    }
    if (max_down > 1.0 && next_gain < a->agc_gain / max_down) {
      next_gain = a->agc_gain / max_down;
    }
    a->agc_gain = next_gain;
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

  double out_rms = sqrt(out_e / (double)max(n, 1));
  double makeup_rms = a->target_rms * (0.94 + 0.06 * level_drive);
  double weak_level_gap = out_rms > 1.0e-9
    ? spnr_clip((makeup_rms / out_rms - 1.0) / 5.0, 0.0, 1.0)
    : 0.0;
  double recovery_inst = weak_recovery_drive
    * spnr_clip((a->agc_gate - 0.30) / 0.36, 0.0, 1.0)
    * weak_level_gap;
  double recovery_alpha = recovery_inst > a->agc_recovery_hold
    ? spnr_time_alpha((double)n, a->rate, 0.090)
    : spnr_time_alpha((double)n, a->rate, 1.200);
  a->agc_recovery_hold = recovery_alpha * a->agc_recovery_hold
    + (1.0 - recovery_alpha) * recovery_inst;
  if (low_evidence_noise_drive > 0.0) {
    a->agc_recovery_hold *= 1.0 - 0.40 * low_evidence_noise_drive
      * (1.0
        - 0.42 * weak_fragment_continuity_relief
        - 0.32 * weak_tail_continuity_relief);
  }
  double makeup_drive = max(
    weak_recovery_drive * spnr_clip((level_drive - 0.18) / 0.62, 0.0, 1.0),
    a->agc_recovery_hold * (0.72 + 0.28 * level_drive));
  makeup_drive = max(
    makeup_drive,
    0.16 * weak_fragment_continuity_relief
      * spnr_clip((a->target_rms * 0.34 - out_rms) / (a->target_rms * 0.30), 0.0, 1.0));
  makeup_drive = max(
    makeup_drive,
    0.165 * weak_tail_continuity_relief
      * spnr_clip((a->target_rms * 0.30 - out_rms) / (a->target_rms * 0.26), 0.0, 1.0));
  makeup_drive *= 1.0 - (0.70
    - 0.16 * weak_fragment_continuity_relief
    - 0.24 * weak_tail_continuity_relief) * low_evidence_noise_drive;
  a->diag_recovery_drive = spnr_clip(max(weak_recovery_drive, a->agc_recovery_hold), 0.0, 1.0);

  double recent_speech_inst = weak_input_drive
    * spnr_clip((gate_confidence - 0.285) / 0.170, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.150) / 0.160, 0.0, 1.0),
      0.52 * spnr_clip((a->agc_weak_signal_memory - 0.300) / 0.300, 0.0, 1.0))
    * max(
      spnr_clip((a->agc_gate - 0.420) / 0.280, 0.0, 1.0),
      0.66 * spnr_clip((a->diag_recovery_drive - 0.280) / 0.320, 0.0, 1.0));
  recent_speech_inst *= 1.0 - 0.78 * spnr_clip(
    (low_evidence_noise_drive - 0.28) / 0.48,
    0.0, 1.0);
  double recent_output_speech_inst = weak_input_drive
    * spnr_clip((out_rms - a->target_rms * 0.135) / (a->target_rms * 0.360), 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_confidence - 0.230) / 0.190, 0.0, 1.0),
      0.66 * spnr_clip((a->diag_signal_probability - 0.090) / 0.185, 0.0, 1.0))
    * max(
      spnr_clip((level_drive - 0.300) / 0.430, 0.0, 1.0),
      0.58 * spnr_clip((a->agc_gate - 0.220) / 0.360, 0.0, 1.0));
  recent_output_speech_inst *= 1.0 - 0.46 * spnr_clip(
    (low_evidence_noise_drive - 0.64) / 0.30,
    0.0, 1.0);
  if (recent_output_speech_inst > 0.0) {
    recent_speech_inst = max(
      recent_speech_inst,
      0.024 + 0.180 * recent_output_speech_inst);
  }
  if (weak_tail_continuity_relief > 0.0) {
    recent_speech_inst = max(
      recent_speech_inst,
      0.018 + 0.145 * weak_tail_continuity_relief);
  }
  double recent_speech_alpha = recent_speech_inst > a->agc_recent_speech_hold
    ? spnr_time_alpha((double)n, a->rate, 0.060)
    : spnr_time_alpha((double)n, a->rate,
      0.620 + 0.540 * (1.0 - low_evidence_noise_drive)
        + 0.500 * weak_tail_continuity_relief);
  a->agc_recent_speech_hold = recent_speech_alpha * a->agc_recent_speech_hold
    + (1.0 - recent_speech_alpha) * recent_speech_inst;
  if (low_evidence_noise_drive > 0.68 + 0.10 * weak_tail_continuity_relief) {
    a->agc_recent_speech_hold *= 1.0 - 0.62 * spnr_clip(
      (low_evidence_noise_drive - 0.68 - 0.10 * weak_tail_continuity_relief)
        / (0.26 + 0.10 * weak_tail_continuity_relief),
      0.0, 1.0);
  }

  double makeup_target = 1.0;
  if (a->agc_run && out_rms > 1.0e-9 && makeup_drive > 0.0) {
    double persistent_cap = 2.06 + 0.24 * weak_input_drive
      * spnr_clip((gate_confidence - 0.32) / 0.30, 0.0, 1.0);
    double deficit = spnr_clip(makeup_rms / out_rms, 1.0, persistent_cap);
    makeup_target = 1.0 + makeup_drive * (deficit - 1.0);
  }
  double makeup_release_drive = max(
    1.0 - weak_input_drive,
    spnr_clip((out_rms - a->target_rms * 0.34) / (a->target_rms * 0.44), 0.0, 1.0));
  makeup_release_drive = max(
    makeup_release_drive,
    low_evidence_noise_drive * (1.0
      - 0.24 * weak_fragment_continuity_relief
      - 0.46 * weak_tail_continuity_relief));
  makeup_release_drive = max(
    makeup_release_drive,
    spnr_clip((a->diag_input_rms - a->target_rms * 0.78) / (a->target_rms * 0.42), 0.0, 1.0));
  double makeup_release_tau = 0.420 + 1.100 * (1.0 - makeup_release_drive);
  double makeup_alpha = makeup_target < a->agc_makeup_gain
    ? spnr_time_alpha((double)n, a->rate, makeup_release_tau)
    : spnr_time_alpha((double)n, a->rate, 0.260 + 0.520 * (1.0 - makeup_drive));
  double next_makeup_gain = makeup_alpha * a->agc_makeup_gain
    + (1.0 - makeup_alpha) * makeup_target;
  double makeup_seconds = a->rate > 0.0 ? (double)n / a->rate : 0.0;
  double makeup_max_up = pow(10.0, ((2.6 + 3.8 * makeup_drive) * makeup_seconds) / 20.0);
  double makeup_max_down = pow(10.0, ((3.2 + 7.0 * makeup_release_drive
    + 3.5 * (1.0 - makeup_drive)) * makeup_seconds) / 20.0);
  if (makeup_max_up > 1.0 && next_makeup_gain > a->agc_makeup_gain * makeup_max_up) {
    next_makeup_gain = a->agc_makeup_gain * makeup_max_up;
  }
  if (makeup_max_down > 1.0 && next_makeup_gain < a->agc_makeup_gain / makeup_max_down) {
    next_makeup_gain = a->agc_makeup_gain / makeup_max_down;
  }
  a->agc_makeup_gain = next_makeup_gain;
  a->diag_makeup_gain = a->agc_makeup_gain;

  if (a->agc_run && a->agc_makeup_gain > 1.001) {
    for (int i = 0; i < n; i++) {
      out[2 * i + 0] *= a->agc_makeup_gain;
    }
    out_rms *= a->agc_makeup_gain;
  }

  double rescue_gate_drive = max(
    spnr_clip((a->agc_gate - 0.22) / 0.36, 0.0, 1.0),
    0.70 * probability_drive);
  double rescue_confidence_drive = spnr_clip((gate_confidence - 0.06) / 0.30, 0.0, 1.0);
  double rescue_level_drive = spnr_clip(
    (max(level_drive, weak_recovery_drive) - 0.24) / 0.50,
    0.0, 1.0);
  rescue_level_drive = max(
    rescue_level_drive,
    0.55 * spnr_clip((a->agc_gate - 0.45) / 0.35, 0.0, 1.0));
  double faint_rescue_drive = weak_input_drive
    * rescue_gate_drive
    * rescue_confidence_drive
    * rescue_level_drive;
  faint_rescue_drive *= 1.0 - 0.75 * low_evidence_noise_drive;
  double output_below_input = (a->diag_input_rms > 1.0e-9 && out_rms > 1.0e-9)
    ? spnr_clip((0.98 * a->diag_input_rms / out_rms - 1.0) / 1.70, 0.0, 1.0)
    : 0.0;
  double weak_fragment_floor_drive = weak_input_drive
    * spnr_clip((a->agc_gate - 0.34) / 0.36, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.13) / 0.26, 0.0, 1.0);
  weak_fragment_floor_drive *= 1.0 - 0.75 * low_evidence_noise_drive;
  double faint_floor_rms = a->target_rms * (
    0.64 + 0.10 * weak_fragment_floor_drive
    + 0.28 * faint_rescue_drive + 0.10 * probability_drive);
  double output_below_floor = out_rms > 1.0e-9
    ? spnr_clip((faint_floor_rms / out_rms - 1.0) / 3.30, 0.0, 1.0)
    : 0.0;
  double rescue_need = max(output_below_floor, 0.65 * output_below_input);
  if (a->agc_run && faint_rescue_drive > 0.010 && rescue_need > 0.0 && out_rms > 1.0e-9) {
    double rescue_target_rms = max(1.24 * a->diag_input_rms, faint_floor_rms);
    rescue_target_rms = min(rescue_target_rms, a->target_rms * (0.98 + 0.04 * probability_drive));
    double direct_rescue = spnr_clip(rescue_target_rms / out_rms, 1.0, 5.60);
    double rescue_blend = spnr_clip(
      0.46 + 0.52 * faint_rescue_drive * (0.45 + 0.55 * rescue_need),
      0.0, 0.96);
    double rescue_gain = 1.0 + rescue_blend * (direct_rescue - 1.0);
    if (rescue_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= rescue_gain;
      }
      out_rms *= rescue_gain;
    }
  }

  double probability_onset_gate =
    spnr_clip((a->diag_signal_probability - 0.16) / 0.06, 0.0, 1.0);
  double weak_dropout_gate_open = spnr_clip(
    (a->agc_gate - (0.30 - 0.04 * probability_onset_gate)) / 0.34,
    0.0, 1.0);
  double weak_dropout_drive = weak_input_drive
    * weak_dropout_gate_open
    * spnr_clip((gate_confidence - 0.14) / 0.22, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.52 - out_rms) / (a->target_rms * 0.34), 0.0, 1.0);
  weak_dropout_drive *= 1.0 - 0.75 * low_evidence_noise_drive;
  double mixed_fragment_drive = weak_input_drive
    * spnr_clip((a->agc_gate - 0.34) / 0.32, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.16) / 0.24, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.70 - out_rms) / (a->target_rms * 0.48), 0.0, 1.0);
  mixed_fragment_drive *= 1.0 - 0.75 * low_evidence_noise_drive;
  weak_dropout_drive = max(weak_dropout_drive, 0.38 * mixed_fragment_drive);
  if (a->agc_run && weak_dropout_drive > 0.008 && out_rms > 1.0e-9) {
    double weak_dropout_floor_rms = a->target_rms * (0.72 + 0.08 * weak_dropout_drive);
    double weak_dropout_lift = spnr_clip(weak_dropout_floor_rms / out_rms, 1.0, 6.20);
    double dropout_blend = spnr_clip(0.70 + 0.20 * weak_dropout_drive, 0.0, 0.92);
    double dropout_gain = 1.0 + dropout_blend * (weak_dropout_lift - 1.0);
    if (dropout_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= dropout_gain;
      }
      out_rms *= dropout_gain;
    }
  }

  double signal_valley_drive = spnr_clip(
    (a->diag_input_rms - a->target_rms * 0.46) / (a->target_rms * 0.80),
    0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.52) / 0.30, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.24) / 0.26, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.70 - out_rms) / (a->target_rms * 0.40), 0.0, 1.0);
  if (a->diag_input_rms > 1.0e-9 && out_rms > 1.0e-9) {
    signal_valley_drive *= spnr_clip(
      (0.88 * a->diag_input_rms / out_rms - 1.0) / 2.20,
      0.0, 1.0);
  }
  if (a->agc_run && signal_valley_drive > 0.010 && out_rms > 1.0e-9) {
    double signal_valley_floor_rms = a->target_rms * (0.70 + 0.10 * signal_valley_drive);
    double signal_valley_lift = spnr_clip(signal_valley_floor_rms / out_rms, 1.0, 3.80);
    double signal_valley_blend = spnr_clip(0.38 + 0.34 * signal_valley_drive, 0.0, 0.72);
    double signal_valley_gain = 1.0 + signal_valley_blend * (signal_valley_lift - 1.0);
    if (signal_valley_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= signal_valley_gain;
      }
      out_rms *= signal_valley_gain;
    }
  }

  double strong_valley_drive = spnr_clip(
    (a->diag_input_rms - a->target_rms * 0.72) / (a->target_rms * 0.64),
    0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.52) / 0.30, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.24) / 0.24, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.68 - out_rms) / (a->target_rms * 0.34), 0.0, 1.0);
  if (a->diag_input_rms > 1.0e-9 && out_rms > 1.0e-9) {
    strong_valley_drive *= spnr_clip(
      (0.74 * a->diag_input_rms / out_rms - 1.0) / 2.00,
      0.0, 1.0);
  }
  if (a->agc_run && strong_valley_drive > 0.012 && out_rms > 1.0e-9) {
    double strong_valley_floor_rms = a->target_rms * (0.68 + 0.08 * strong_valley_drive);
    double strong_valley_lift = spnr_clip(strong_valley_floor_rms / out_rms, 1.0, 3.20);
    double strong_valley_blend = spnr_clip(0.36 + 0.30 * strong_valley_drive, 0.0, 0.68);
    double strong_valley_gain = 1.0 + strong_valley_blend * (strong_valley_lift - 1.0);
    if (strong_valley_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= strong_valley_gain;
      }
      out_rms *= strong_valley_gain;
    }
  }

  double limit_rms = a->target_rms * (1.12 + 0.10 * level_drive);
  if (a->agc_run && out_rms > limit_rms && limit_rms > 1.0e-7) {
    double limiter = limit_rms / out_rms;
    for (int i = 0; i < n; i++) {
      out[2 * i + 0] *= limiter;
    }
    out_rms *= limiter;
  }
  if (a->agc_run && low_evidence_noise_drive > 0.10 && out_rms > 1.0e-9) {
    double low_evidence_ceiling_rms = a->target_rms *
      (0.00035 + 0.0030 * (1.0 - low_evidence_noise_drive));
    if (weak_tail_continuity_relief > 0.0) {
      double tail_ceiling_rms = a->target_rms
        * (0.0060 + 0.0950 * weak_tail_continuity_relief);
      tail_ceiling_rms = min(tail_ceiling_rms, a->target_rms * 0.082);
      low_evidence_ceiling_rms = max(low_evidence_ceiling_rms, tail_ceiling_rms);
    }
    double low_evidence_mix = spnr_clip((low_evidence_noise_drive - 0.04) / 0.10, 0.0, 1.0);
    low_evidence_mix *= 1.0
      - 0.18 * weak_fragment_continuity_relief
      - 0.56 * weak_tail_continuity_relief;
    if (out_rms > low_evidence_ceiling_rms && low_evidence_mix > 0.0) {
      double trim = low_evidence_ceiling_rms / out_rms;
      double low_evidence_gain = 1.0 + low_evidence_mix * (trim - 1.0);
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= low_evidence_gain;
      }
      out_rms *= low_evidence_gain;
    }
  }

  double recent_tail_preserve_drive = weak_input_drive
    * max(prior_recent_speech_hold, 0.56 * prior_weak_signal_memory)
    * max(
      spnr_clip((level_drive - 0.240) / 0.500, 0.0, 1.0),
      0.56 * spnr_clip((a->agc_gate - 0.160) / 0.420, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.215) / 0.235, 0.0, 1.0),
      max(
        0.62 * spnr_clip((a->diag_signal_probability - 0.055) / 0.150, 0.0, 1.0),
        0.46 * spnr_clip((gate_confidence - 0.195) / 0.170, 0.0, 1.0)))
    * spnr_clip((a->target_rms * 0.205 - out_rms) / (a->target_rms * 0.190), 0.0, 1.0);
  recent_tail_preserve_drive *= 1.0 - 0.38 * spnr_clip(
    (low_evidence_noise_drive - 0.82) / 0.18,
    0.0, 1.0);
  if (a->agc_run && recent_tail_preserve_drive > 0.004 && out_rms > 1.0e-9) {
    double recent_tail_floor_rms = max(
      0.62 * a->diag_input_rms,
      a->target_rms * (0.034 + 0.090 * recent_tail_preserve_drive));
    recent_tail_floor_rms = min(recent_tail_floor_rms, a->target_rms * 0.180);
    double recent_tail_lift = spnr_clip(recent_tail_floor_rms / out_rms, 1.0, 22.00);
    double recent_tail_blend = spnr_clip(
      0.30 + 0.28 * recent_tail_preserve_drive,
      0.0, 0.64);
    double recent_tail_gain = 1.0 + recent_tail_blend * (recent_tail_lift - 1.0);
    if (recent_tail_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= recent_tail_gain;
      }
      out_rms *= recent_tail_gain;
    }
  }

  double stable_weak_context_drive = weak_input_drive
    * spnr_clip((a->diag_input_rms - a->target_rms * 0.045) / (a->target_rms * 0.520), 0.0, 1.0)
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.240) / 0.220, 0.0, 1.0),
      0.72 * spnr_clip((a->diag_signal_probability - 0.110) / 0.140, 0.0, 1.0))
    * max(
      spnr_clip((level_drive - 0.400) / 0.420, 0.0, 1.0),
      0.52 * spnr_clip((a->agc_gate - 0.300) / 0.320, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.46 - out_rms) / (a->target_rms * 0.430), 0.0, 1.0);
  stable_weak_context_drive *= 1.0 - 0.50 * spnr_clip(
    (low_evidence_noise_drive - 0.70) / 0.25,
    0.0, 1.0);
  if (a->agc_run && stable_weak_context_drive > 0.004 && out_rms > 1.0e-9) {
    double context_floor_rms = max(
      0.45 * a->diag_input_rms,
      a->target_rms * (0.045 + 0.085 * stable_weak_context_drive));
    context_floor_rms = min(context_floor_rms, a->target_rms * 0.280);
    double context_floor_lift = spnr_clip(context_floor_rms / out_rms, 1.0, 22.00);
    double context_floor_blend = spnr_clip(
      0.38 + 0.30 * stable_weak_context_drive,
      0.0, 0.72);
    double context_floor_gain = 1.0 + context_floor_blend * (context_floor_lift - 1.0);
    if (context_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= context_floor_gain;
      }
      out_rms *= context_floor_gain;
    }
  }

  double stable_blackout_shape = max(
    spnr_clip((a->diag_mask_smoothing - 0.180) / 0.220, 0.0, 1.0),
    max(
      0.72 * spnr_clip((a->diag_signal_probability - 0.085) / 0.135, 0.0, 1.0),
      0.55 * spnr_clip((a->diag_mean_gain - 0.080) / 0.180, 0.0, 1.0)));
  double stable_blackout_control = max(
    spnr_clip((level_drive - 0.260) / 0.310, 0.0, 1.0),
    max(
      0.50 * spnr_clip((a->agc_gate - 0.160) / 0.260, 0.0, 1.0),
      0.46 * spnr_clip((gate_confidence - 0.220) / 0.130, 0.0, 1.0)));
  double stable_blackout_drive = weak_input_drive
    * spnr_clip((a->diag_input_rms - a->target_rms * 0.035) / (a->target_rms * 0.560), 0.0, 1.0)
    * stable_blackout_shape
    * stable_blackout_control
    * spnr_clip((a->target_rms * 0.135 - out_rms) / (a->target_rms * 0.130), 0.0, 1.0);
  stable_blackout_drive *= 1.0 - 0.44 * spnr_clip(
    (low_evidence_noise_drive - 0.76) / 0.22,
    0.0, 1.0);
  if (a->agc_run && stable_blackout_drive > 0.004 && out_rms > 1.0e-9) {
    double blackout_floor_rms = max(
      0.30 * a->diag_input_rms,
      a->target_rms * (0.0090 + 0.0300 * stable_blackout_drive));
    blackout_floor_rms = min(blackout_floor_rms, a->target_rms * 0.115);
    double blackout_lift = spnr_clip(blackout_floor_rms / out_rms, 1.0, 26.00);
    double blackout_blend = spnr_clip(
      0.32 + 0.28 * stable_blackout_drive,
      0.0, 0.60);
    double blackout_gain = 1.0 + blackout_blend * (blackout_lift - 1.0);
    if (blackout_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= blackout_gain;
      }
      out_rms *= blackout_gain;
    }
  }

  double adjacent_suppressed_floor_drive = adjacent_suppressed_relief
    * spnr_clip((a->target_rms * 0.185 - out_rms) / (a->target_rms * 0.170), 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.115) / 0.120, 0.0, 1.0),
      0.72 * spnr_clip((a->diag_mask_smoothing - 0.335) / 0.135, 0.0, 1.0));
  adjacent_suppressed_floor_drive *= 1.0 - 0.56 * spnr_clip(
    (low_evidence_noise_drive - 0.70) / 0.24,
    0.0, 1.0);
  if (a->agc_run && adjacent_suppressed_floor_drive > 0.006 && out_rms > 1.0e-9) {
    double adjacent_floor_rms = max(
      0.46 * a->diag_input_rms,
      a->target_rms * (0.018 + 0.045 * adjacent_suppressed_floor_drive));
    adjacent_floor_rms = min(adjacent_floor_rms, a->target_rms * 0.160);
    double adjacent_floor_lift = spnr_clip(adjacent_floor_rms / out_rms, 1.0, 24.00);
    double adjacent_floor_blend = spnr_clip(
      0.32 + 0.30 * adjacent_suppressed_floor_drive,
      0.0, 0.62);
    double adjacent_floor_gain = 1.0 + adjacent_floor_blend * (adjacent_floor_lift - 1.0);
    if (adjacent_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= adjacent_floor_gain;
      }
      out_rms *= adjacent_floor_gain;
    }
  }

  double recent_continuity_drive = weak_input_drive
    * a->agc_recent_speech_hold
    * spnr_clip((a->diag_input_rms - a->target_rms * 0.16) / (a->target_rms * 0.58), 0.0, 1.0)
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.220) / 0.180, 0.0, 1.0),
      spnr_clip((a->diag_signal_probability - 0.105) / 0.135, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.50 - out_rms) / (a->target_rms * 0.46), 0.0, 1.0);
  recent_continuity_drive *= 1.0 - 0.72 * spnr_clip(
    (low_evidence_noise_drive - 0.56) / 0.36,
    0.0, 1.0);
  if (a->agc_run && recent_continuity_drive > 0.006 && out_rms > 1.0e-9) {
    double recent_floor_rms = max(
      0.82 * a->diag_input_rms,
      a->target_rms * (0.105 + 0.125 * recent_continuity_drive));
    recent_floor_rms = min(recent_floor_rms, a->target_rms * 0.40);
    double recent_floor_lift = spnr_clip(recent_floor_rms / out_rms, 1.0, 18.00);
    double recent_floor_blend = spnr_clip(
      0.34 + 0.30 * recent_continuity_drive,
      0.0, 0.70);
    double recent_floor_gain = 1.0 + recent_floor_blend * (recent_floor_lift - 1.0);
    if (recent_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= recent_floor_gain;
      }
      out_rms *= recent_floor_gain;
    }
  }

  double weak_tail_afterglow_drive = weak_input_drive
    * max(a->agc_recent_speech_hold, 0.66 * a->agc_continuity_hold)
    * spnr_clip((a->agc_gate - 0.440) / 0.220, 0.0, 1.0)
    * spnr_clip((a->diag_mask_smoothing - 0.318) / 0.140, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.190 - out_rms) / (a->target_rms * 0.175), 0.0, 1.0);
  weak_tail_afterglow_drive *= 1.0 - 0.70 * spnr_clip(
    (low_evidence_noise_drive - 0.78) / 0.20,
    0.0, 1.0);
  if (a->agc_run && weak_tail_afterglow_drive > 0.006 && out_rms > 1.0e-9) {
    double afterglow_floor_rms = max(
      0.92 * a->diag_input_rms,
      a->target_rms * (0.040 + 0.045 * weak_tail_afterglow_drive));
    afterglow_floor_rms = min(afterglow_floor_rms, a->target_rms * 0.115);
    double afterglow_lift = spnr_clip(afterglow_floor_rms / out_rms, 1.0, 16.00);
    double afterglow_blend = spnr_clip(
      0.24 + 0.28 * weak_tail_afterglow_drive,
      0.0, 0.56);
    double afterglow_gain = 1.0 + afterglow_blend * (afterglow_lift - 1.0);
    if (afterglow_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= afterglow_gain;
      }
      out_rms *= afterglow_gain;
    }
  }

  double speech_tail_hole_drive = weak_input_drive
    * max(a->agc_recent_speech_hold, 0.60 * a->agc_continuity_hold)
    * spnr_clip((level_drive - 0.620) / 0.280, 0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.420) / 0.280, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_mask_smoothing - 0.280) / 0.180, 0.0, 1.0),
      0.44 * spnr_clip((gate_confidence - 0.200) / 0.170, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.180 - out_rms) / (a->target_rms * 0.170), 0.0, 1.0);
  speech_tail_hole_drive *= 1.0 - 0.86 * spnr_clip(
    (low_evidence_noise_drive - 0.72) / 0.20,
    0.0, 1.0);
  if (a->agc_run && speech_tail_hole_drive > 0.006 && out_rms > 1.0e-9) {
    double speech_tail_hole_floor_rms = max(
      0.58 * a->diag_input_rms,
      a->target_rms * (0.018 + 0.046 * speech_tail_hole_drive));
    speech_tail_hole_floor_rms = min(speech_tail_hole_floor_rms, a->target_rms * 0.160);
    double speech_tail_hole_lift = spnr_clip(speech_tail_hole_floor_rms / out_rms, 1.0, 14.00);
    double speech_tail_hole_blend = spnr_clip(
      0.28 + 0.24 * speech_tail_hole_drive,
      0.0, 0.58);
    double speech_tail_hole_gain = 1.0 + speech_tail_hole_blend * (speech_tail_hole_lift - 1.0);
    if (speech_tail_hole_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= speech_tail_hole_gain;
      }
      out_rms *= speech_tail_hole_gain;
    }
  }

  double recent_mask_tail_bridge_drive = weak_input_drive
    * max(a->agc_recent_speech_hold, 0.70 * a->agc_continuity_hold)
    * spnr_clip((a->agc_gate - 0.580) / 0.220, 0.0, 1.0)
    * spnr_clip((a->diag_mask_smoothing - 0.300) / 0.140, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.220) / 0.080, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.240 - out_rms) / (a->target_rms * 0.220), 0.0, 1.0);
  recent_mask_tail_bridge_drive *= 1.0 - 0.78 * spnr_clip(
    (low_evidence_noise_drive - 0.70) / 0.22,
    0.0, 1.0);
  if (a->agc_run && recent_mask_tail_bridge_drive > 0.006 && out_rms > 1.0e-9) {
    double bridge_floor_rms = max(
      0.74 * a->diag_input_rms,
      a->target_rms * (0.050 + 0.070 * recent_mask_tail_bridge_drive));
    bridge_floor_rms = min(bridge_floor_rms, a->target_rms * 0.220);
    double bridge_lift = spnr_clip(bridge_floor_rms / out_rms, 1.0, 12.00);
    double bridge_blend = spnr_clip(
      0.26 + 0.22 * recent_mask_tail_bridge_drive,
      0.0, 0.50);
    double bridge_gain = 1.0 + bridge_blend * (bridge_lift - 1.0);
    if (bridge_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= bridge_gain;
      }
      out_rms *= bridge_gain;
    }
  }

  double learned_weak_floor_drive = weak_input_drive
    * a->agc_weak_signal_memory
    * max(
      spnr_clip((a->agc_gate - 0.42) / 0.34, 0.0, 1.0),
      0.52 * spnr_clip((a->diag_recovery_drive - 0.135) / 0.200, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.58 - out_rms) / (a->target_rms * 0.46), 0.0, 1.0);
  learned_weak_floor_drive *= 1.0 - 0.80 * spnr_clip(
    (low_evidence_noise_drive - 0.24) / 0.42,
    0.0, 1.0);
  if (a->agc_run && learned_weak_floor_drive > 0.006 && out_rms > 1.0e-9) {
    double learned_floor_rms = a->target_rms * (0.36 + 0.14 * learned_weak_floor_drive);
    double learned_floor_lift = spnr_clip(learned_floor_rms / out_rms, 1.0, 10.00);
    double learned_floor_blend = spnr_clip(
      0.46 + 0.30 * learned_weak_floor_drive,
      0.0, 0.76);
    double learned_floor_gain = 1.0 + learned_floor_blend * (learned_floor_lift - 1.0);
    if (learned_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= learned_floor_gain;
      }
      out_rms *= learned_floor_gain;
    }
  }
  double memory_floor_drive = weak_input_drive
    * spnr_clip((a->agc_weak_signal_memory - 0.24) / 0.40, 0.0, 1.0)
    * max(
      spnr_clip((a->agc_gate - 0.38) / 0.34, 0.0, 1.0),
      0.66 * spnr_clip((a->diag_recovery_drive - 0.200) / 0.300, 0.0, 1.0))
    * spnr_clip((gate_confidence - 0.260) / 0.150, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.66 - out_rms) / (a->target_rms * 0.54), 0.0, 1.0);
  memory_floor_drive *= 1.0 - 0.66 * spnr_clip(
    (low_evidence_noise_drive - 0.32) / 0.46,
    0.0, 1.0);
  if (a->agc_run && memory_floor_drive > 0.006 && out_rms > 1.0e-9) {
    double memory_floor_rms = a->target_rms * (0.42 + 0.18 * memory_floor_drive);
    double memory_floor_lift = spnr_clip(memory_floor_rms / out_rms, 1.0, 13.00);
    double memory_floor_blend = spnr_clip(
      0.48 + 0.34 * memory_floor_drive,
      0.0, 0.82);
    double memory_floor_gain = 1.0 + memory_floor_blend * (memory_floor_lift - 1.0);
    if (memory_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= memory_floor_gain;
      }
      out_rms *= memory_floor_gain;
    }
  }
  double weak_recovery_floor_drive = weak_input_drive
    * spnr_clip((gate_confidence - 0.310) / 0.065, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_recovery_drive - 0.220) / 0.085, 0.0, 1.0),
      0.68 * spnr_clip((a->agc_gate - 0.560) / 0.250, 0.0, 1.0))
    * spnr_clip((a->diag_signal_probability - 0.165) / 0.075, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.44 - out_rms) / (a->target_rms * 0.38), 0.0, 1.0);
  weak_recovery_floor_drive *= 1.0 - 0.82 * spnr_clip(
    (low_evidence_noise_drive - 0.24) / 0.36,
    0.0, 1.0);
  if (a->agc_run && weak_recovery_floor_drive > 0.006 && out_rms > 1.0e-9) {
    double recovery_floor_rms = a->target_rms * (0.16 + 0.08 * weak_recovery_floor_drive);
    double recovery_floor_lift = spnr_clip(recovery_floor_rms / out_rms, 1.0, 20.00);
    double recovery_floor_blend = spnr_clip(
      0.22 + 0.30 * weak_recovery_floor_drive,
      0.0, 0.52);
    double recovery_floor_gain = 1.0 + recovery_floor_blend * (recovery_floor_lift - 1.0);
    if (recovery_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= recovery_floor_gain;
      }
      out_rms *= recovery_floor_gain;
    }
  }
  double gate_stable_whisper_drive = weak_input_drive
    * spnr_clip((a->agc_gate - 0.460) / 0.240, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.275) / 0.085, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.132) / 0.070, 0.0, 1.0),
      0.70 * spnr_clip((a->diag_recovery_drive - 0.148) / 0.120, 0.0, 1.0))
    * spnr_clip((a->diag_mask_smoothing - 0.300) / 0.145, 0.0, 1.0)
    * spnr_clip((a->target_rms * 0.40 - out_rms) / (a->target_rms * 0.34), 0.0, 1.0);
  gate_stable_whisper_drive *= 1.0 - 0.84 * spnr_clip(
    (low_evidence_noise_drive - 0.28) / 0.42,
    0.0, 1.0);
  if (a->agc_run && gate_stable_whisper_drive > 0.006 && out_rms > 1.0e-9) {
    double whisper_floor_rms = a->target_rms * (0.14 + 0.06 * gate_stable_whisper_drive);
    double whisper_floor_lift = spnr_clip(whisper_floor_rms / out_rms, 1.0, 18.00);
    double whisper_floor_blend = spnr_clip(
      0.16 + 0.28 * gate_stable_whisper_drive,
      0.0, 0.46);
    double whisper_floor_gain = 1.0 + whisper_floor_blend * (whisper_floor_lift - 1.0);
    if (whisper_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= whisper_floor_gain;
      }
      out_rms *= whisper_floor_gain;
    }
  }
  double gate_anchor_fragment_drive = weak_input_drive
    * spnr_clip((a->agc_gate - 0.620) / 0.180, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.292) / 0.095, 0.0, 1.0)
    * spnr_clip((a->diag_mask_smoothing - 0.165) / 0.185, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.145) / 0.070, 0.0, 1.0),
      0.66 * spnr_clip((a->diag_recovery_drive - 0.168) / 0.115, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.34 - out_rms) / (a->target_rms * 0.30), 0.0, 1.0);
  gate_anchor_fragment_drive *= 1.0 - 0.88 * spnr_clip(
    (low_evidence_noise_drive - 0.18) / 0.34,
    0.0, 1.0);
  if (a->agc_run && gate_anchor_fragment_drive > 0.006 && out_rms > 1.0e-9) {
    double anchor_floor_rms = a->target_rms * (0.13 + 0.07 * gate_anchor_fragment_drive);
    double anchor_floor_lift = spnr_clip(anchor_floor_rms / out_rms, 1.0, 14.00);
    double anchor_floor_blend = spnr_clip(
      0.16 + 0.24 * gate_anchor_fragment_drive,
      0.0, 0.42);
    double anchor_floor_gain = 1.0 + anchor_floor_blend * (anchor_floor_lift - 1.0);
    if (anchor_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= anchor_floor_gain;
      }
      out_rms *= anchor_floor_gain;
    }
  }
  double continuity_gate_support = max(
    spnr_clip((a->agc_gate - 0.28) / 0.26, 0.0, 1.0),
    0.46 * spnr_clip((a->diag_recovery_drive - 0.135) / 0.16, 0.0, 1.0));
  double continuity_conf_support = spnr_clip(
    (gate_confidence - 0.245) / 0.125,
    0.0, 1.0);
  double continuity_probability_support = max(
    spnr_clip((a->diag_signal_probability - 0.125) / 0.090, 0.0, 1.0),
    0.70 * spnr_clip((a->diag_recovery_drive - 0.135) / 0.17, 0.0, 1.0));
  double continuity_inst = weak_input_drive
    * continuity_gate_support
    * continuity_conf_support
    * continuity_probability_support
    * spnr_clip((a->target_rms * 0.58 - out_rms) / (a->target_rms * 0.48), 0.0, 1.0);
  continuity_inst *= 1.0 - 0.74 * spnr_clip(
    (low_evidence_noise_drive - 0.32) / 0.48,
    0.0, 1.0);
  double continuity_alpha = continuity_inst > a->agc_continuity_hold
    ? spnr_time_alpha((double)n, a->rate, 0.070)
    : spnr_time_alpha((double)n, a->rate,
      0.230 + 0.250 * (1.0 - low_evidence_noise_drive));
  a->agc_continuity_hold = continuity_alpha * a->agc_continuity_hold
    + (1.0 - continuity_alpha) * continuity_inst;
  if (low_evidence_noise_drive > 0.58) {
    a->agc_continuity_hold *= 1.0 - 0.68 * spnr_clip(
      (low_evidence_noise_drive - 0.58) / 0.32,
      0.0, 1.0);
  }
  double continuity_current_gate = spnr_clip((a->agc_gate - 0.34) / 0.24, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.145) / 0.080, 0.0, 1.0),
      max(
        0.72 * spnr_clip((a->diag_recovery_drive - 0.160) / 0.140, 0.0, 1.0),
        0.60 * spnr_clip((gate_confidence - 0.292) / 0.090, 0.0, 1.0)));
  double continuity_memory_drive = a->agc_continuity_hold *
    continuity_current_gate *
    (1.0 - 0.70 * spnr_clip((low_evidence_noise_drive - 0.34) / 0.46, 0.0, 1.0));
  double gate_continuity_drive = weak_input_drive
    * max(
      spnr_clip((a->agc_gate - 0.32) / 0.26, 0.0, 1.0),
      0.35 * spnr_clip((a->diag_recovery_drive - 0.18) / 0.16, 0.0, 1.0))
    * spnr_clip((gate_confidence - 0.255) / 0.145, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.130) / 0.095, 0.0, 1.0),
      0.72 * spnr_clip((a->diag_recovery_drive - 0.145) / 0.18, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.48 - out_rms) / (a->target_rms * 0.38), 0.0, 1.0);
  gate_continuity_drive *= 1.0 - 0.62 * spnr_clip(
    (low_evidence_noise_drive - 0.30) / 0.50,
    0.0, 1.0);
  gate_continuity_drive = max(gate_continuity_drive, 0.58 * continuity_memory_drive);
  if (a->agc_run && gate_continuity_drive > 0.006 && out_rms > 1.0e-9) {
    double continuity_floor_rms = a->target_rms * (0.22 + 0.12 * gate_continuity_drive);
    double continuity_lift = spnr_clip(continuity_floor_rms / out_rms, 1.0, 15.00);
    double continuity_blend = spnr_clip(0.44 + 0.30 * gate_continuity_drive, 0.0, 0.74);
    double continuity_gain = 1.0 + continuity_blend * (continuity_lift - 1.0);
    if (continuity_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= continuity_gain;
      }
      out_rms *= continuity_gain;
    }
  }
  double probability_onset_support = spnr_clip(
    (a->diag_signal_probability - 0.150) / 0.080,
    0.0, 1.0);
  double probability_floor_drive = weak_input_drive
    * spnr_clip((a->diag_signal_probability - 0.145) / 0.105, 0.0, 1.0)
    * spnr_clip((gate_confidence - 0.245) / 0.150, 0.0, 1.0)
    * max(
      spnr_clip((a->agc_gate - 0.20) / 0.24, 0.0, 1.0),
      max(0.42 * probability_drive, 0.52 * probability_onset_support))
    * spnr_clip((a->target_rms * 0.56 - out_rms) / (a->target_rms * 0.42), 0.0, 1.0);
  if (a->diag_signal_confidence < 0.27) {
    probability_floor_drive *= spnr_clip(
      (a->diag_signal_confidence - 0.235) / 0.035,
      0.0, 1.0);
  }
  probability_floor_drive *= 1.0 - 0.45 * spnr_clip(
    (low_evidence_noise_drive - 0.22) / 0.48,
    0.0, 1.0);
  if (a->agc_run && probability_floor_drive > 0.006 && out_rms > 1.0e-9) {
    double probability_floor_rms = a->target_rms * (0.34 + 0.12 * probability_floor_drive);
    double probability_floor_lift = spnr_clip(probability_floor_rms / out_rms, 1.0, 12.00);
    double probability_floor_blend = spnr_clip(
      0.46 + 0.34 * probability_floor_drive,
      0.0, 0.80);
    double probability_floor_gain = 1.0 + probability_floor_blend * (probability_floor_lift - 1.0);
    if (probability_floor_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= probability_floor_gain;
      }
      out_rms *= probability_floor_gain;
    }
  }

  double borderline_onset_drive = weak_input_drive
    * spnr_clip((gate_confidence - 0.292) / 0.055, 0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.185) / 0.080, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.130) / 0.055, 0.0, 1.0),
      0.55 * spnr_clip((a->diag_recovery_drive - 0.120) / 0.120, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.260 - out_rms) / (a->target_rms * 0.240), 0.0, 1.0);
  borderline_onset_drive *= 1.0 - 0.58 * spnr_clip(
    (low_evidence_noise_drive - 0.32) / 0.36,
    0.0, 1.0);
  if (a->agc_run && borderline_onset_drive > 0.006 && out_rms > 1.0e-9) {
    double borderline_onset_floor_rms = max(
      0.82 * a->diag_input_rms,
      a->target_rms * (0.064 + 0.080 * borderline_onset_drive));
    borderline_onset_floor_rms = min(borderline_onset_floor_rms, a->target_rms * 0.180);
    double borderline_onset_lift = spnr_clip(borderline_onset_floor_rms / out_rms, 1.0, 14.00);
    double borderline_onset_blend = spnr_clip(
      0.42 + 0.24 * borderline_onset_drive,
      0.0, 0.62);
    double borderline_onset_gain = 1.0 + borderline_onset_blend * (borderline_onset_lift - 1.0);
    if (borderline_onset_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= borderline_onset_gain;
      }
      out_rms *= borderline_onset_gain;
    }
  }

  double probability_edge_drive = weak_input_drive
    * spnr_clip((gate_confidence - 0.285) / 0.130, 0.0, 1.0)
    * spnr_clip((a->diag_signal_probability - 0.145) / 0.115, 0.0, 1.0)
    * max(
      spnr_clip((level_drive - 0.360) / 0.360, 0.0, 1.0),
      0.58 * spnr_clip((a->agc_gate - 0.220) / 0.260, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.360 - out_rms) / (a->target_rms * 0.330), 0.0, 1.0);
  probability_edge_drive *= 1.0 - 0.62 * spnr_clip(
    (low_evidence_noise_drive - 0.34) / 0.44,
    0.0, 1.0);
  if (a->agc_run && probability_edge_drive > 0.006 && out_rms > 1.0e-9) {
    double probability_edge_floor_rms = max(
      0.86 * a->diag_input_rms,
      a->target_rms * (0.035 + 0.085 * probability_edge_drive));
    probability_edge_floor_rms = min(probability_edge_floor_rms, a->target_rms * 0.260);
    double probability_edge_lift = spnr_clip(probability_edge_floor_rms / out_rms, 1.0, 12.00);
    double probability_edge_blend = spnr_clip(
      0.34 + 0.30 * probability_edge_drive,
      0.0, 0.68);
    double probability_edge_gain = 1.0 + probability_edge_blend * (probability_edge_lift - 1.0);
    if (probability_edge_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= probability_edge_gain;
      }
      out_rms *= probability_edge_gain;
    }
  }

  double speech_valley_core = weak_input_drive
    * spnr_clip((gate_confidence - 0.275) / 0.120, 0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.480) / 0.300, 0.0, 1.0)
    * max(
      spnr_clip((a->diag_signal_probability - 0.132) / 0.080, 0.0, 1.0),
      spnr_clip((a->agc_weak_signal_memory - 0.180) / 0.280, 0.0, 1.0))
    * max(
      spnr_clip((a->diag_recovery_drive - 0.250) / 0.200, 0.0, 1.0),
      0.60 * spnr_clip((a->diag_mask_smoothing - 0.300) / 0.160, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.42 - out_rms) / (a->target_rms * 0.36), 0.0, 1.0);
  double speech_tail_drive = weak_input_drive
    * spnr_clip((gate_confidence - 0.258) / 0.120, 0.0, 1.0)
    * spnr_clip((a->agc_gate - 0.452) / 0.260, 0.0, 1.0)
    * max(
      max(
        spnr_clip((a->diag_signal_probability - 0.118) / 0.105, 0.0, 1.0),
        spnr_clip((a->agc_weak_signal_memory - 0.130) / 0.250, 0.0, 1.0)),
      max(
        0.76 * spnr_clip((a->diag_recovery_drive - 0.230) / 0.200, 0.0, 1.0),
        0.62 * spnr_clip((a->diag_mask_smoothing - 0.315) / 0.140, 0.0, 1.0)))
    * max(
      spnr_clip((a->diag_recovery_drive - 0.210) / 0.240, 0.0, 1.0),
      max(
        0.72 * spnr_clip((a->diag_mask_smoothing - 0.310) / 0.170, 0.0, 1.0),
        0.62 * spnr_clip((a->agc_weak_signal_memory - 0.240) / 0.280, 0.0, 1.0)))
    * spnr_clip((a->target_rms * 0.50 - out_rms) / (a->target_rms * 0.44), 0.0, 1.0);
  double speech_valley_drive = max(speech_valley_core, 0.78 * speech_tail_drive);
  speech_valley_drive *= 1.0 - 0.82 * spnr_clip(
    (low_evidence_noise_drive - 0.20) / 0.40,
    0.0, 1.0);
  if (a->agc_run && speech_valley_drive > 0.006 && out_rms > 1.0e-9) {
    double speech_valley_floor_rms = a->target_rms * (0.48 + 0.20 * speech_valley_drive);
    double speech_valley_lift = spnr_clip(speech_valley_floor_rms / out_rms, 1.0, 16.00);
    double speech_valley_blend = spnr_clip(
      0.54 + 0.30 * speech_valley_drive,
      0.0, 0.84);
    double speech_valley_gain = 1.0 + speech_valley_blend * (speech_valley_lift - 1.0);
    if (speech_valley_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= speech_valley_gain;
      }
      out_rms *= speech_valley_gain;
    }
  }
  double mask_stable_tail_drive = weak_input_drive
    * spnr_clip((gate_confidence - 0.260) / 0.120, 0.0, 1.0)
    * spnr_clip((a->diag_mask_smoothing - 0.305) / 0.110, 0.0, 1.0)
    * max(
      max(
        spnr_clip((a->diag_signal_probability - 0.125) / 0.095, 0.0, 1.0),
        spnr_clip((a->agc_weak_signal_memory - 0.100) / 0.240, 0.0, 1.0)),
      0.76 * spnr_clip((a->diag_recovery_drive - 0.190) / 0.190, 0.0, 1.0))
    * max(
      spnr_clip((a->agc_gate - 0.350) / 0.210, 0.0, 1.0),
      0.58 * spnr_clip((a->agc_continuity_hold - 0.040) / 0.220, 0.0, 1.0))
    * spnr_clip((a->target_rms * 0.34 - out_rms) / (a->target_rms * 0.30), 0.0, 1.0);
  mask_stable_tail_drive *= 1.0 - 0.72 * spnr_clip(
    (low_evidence_noise_drive - 0.30) / 0.42,
    0.0, 1.0);
  if (a->agc_run && mask_stable_tail_drive > 0.006 && out_rms > 1.0e-9) {
    double mask_tail_floor_rms = a->target_rms * (0.38 + 0.05 * mask_stable_tail_drive);
    double mask_tail_lift = spnr_clip(mask_tail_floor_rms / out_rms, 1.0, 12.00);
    double mask_tail_blend = spnr_clip(
      0.54 + 0.20 * mask_stable_tail_drive,
      0.0, 0.70);
    double mask_tail_gain = 1.0 + mask_tail_blend * (mask_tail_lift - 1.0);
    if (mask_tail_gain > 1.001) {
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= mask_tail_gain;
      }
      out_rms *= mask_tail_gain;
    }
  }

  if (a->agc_run && weak_input_drive > 0.30
      && a->diag_input_rms > 1.0e-9 && out_rms > 1.0e-9) {
    double voice_certainty = spnr_clip(
      0.34 * spnr_clip((gate_confidence - 0.300) / 0.180, 0.0, 1.0)
        + 0.26 * spnr_clip((a->diag_signal_probability - 0.180) / 0.160, 0.0, 1.0)
        + 0.12 * spnr_clip((a->agc_recent_speech_hold - 0.080) / 0.300, 0.0, 1.0)
        + 0.22 * spnr_clip((a->agc_gate - 0.480) / 0.220, 0.0, 1.0)
        + 0.18 * spnr_clip((a->diag_recovery_drive - 0.280) / 0.200, 0.0, 1.0)
        - 0.36 * low_evidence_noise_drive,
      0.0, 1.0);
    double over_lift_cap_db = 3.0 + 10.0 * voice_certainty;
    double over_lift_cap = pow(10.0, over_lift_cap_db / 20.0);
    double over_lift_ceiling = a->diag_input_rms * over_lift_cap;
    if (out_rms > over_lift_ceiling) {
      double trim = over_lift_ceiling / out_rms;
      double trim_blend = spnr_clip(
        0.64 + 0.26 * low_evidence_noise_drive - 0.24 * voice_certainty,
        0.34,
        0.86);
      double over_lift_gain = 1.0 + trim_blend * (trim - 1.0);
      for (int i = 0; i < n; i++) {
        out[2 * i + 0] *= over_lift_gain;
      }
      out_rms *= over_lift_gain;
    }
  }

  double out_peak = 0.0;
  for (int i = 0; i < n; i++) {
    double v = fabs(out[2 * i + 0]);
    if (v > out_peak) { out_peak = v; }
  }
  double peak_evidence = max(
    probability_drive,
    spnr_clip((gate_confidence - 0.32) / 0.34, 0.0, 1.0));
  double peak_limit = 0.58 + 0.16 * peak_evidence + 0.04 * (1.0 - level_drive);
  a->diag_output_peak = out_peak;
  a->diag_peak_evidence = peak_evidence;
  a->diag_peak_limit = peak_limit;
  a->diag_peak_reduction_db = 0.0;
  if (a->agc_run && out_peak > peak_limit && peak_limit > 1.0e-7) {
    double peak_knee = peak_limit * (0.94 - 0.04 * level_drive);
    double peak_span = max(peak_limit - peak_knee, 1.0e-7);
    out_e = 0.0;
    for (int i = 0; i < n; i++) {
      double v = out[2 * i + 0];
      double av = fabs(v);
      if (av > peak_knee) {
        double shaped = peak_knee + peak_span * tanh((av - peak_knee) / peak_span);
        v = v < 0.0 ? -shaped : shaped;
        out[2 * i + 0] = v;
      }
      out_e += v * v;
    }
    out_rms = sqrt(out_e / (double)max(n, 1));
    a->diag_peak_reduction_db = 20.0 * log10(max(out_peak, 1.0e-12) / max(peak_limit, 1.0e-12));
  }

  a->diag_output_rms = out_rms;
}

void xspnr(SPNR a, int pos) {
  if (a->run && pos == a->position) {
    int i, j, k, sbuff, sbegin;
    double scale = 1.0 / (double)(a->fsize * a->ovrlp);
    double input_e = 0.0;

    for (i = 0; i < 2 * a->bsize; i += 2) {
      double input_sample = a->in[i];
      a->dry[i / 2] = input_sample;
      input_e += input_sample * input_sample;
      a->inaccum[a->iainidx] = input_sample;
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

    double dry_guard = spnr_clip(
      (0.48 * a->diag_presence_peak
        + 0.30 * a->diag_salience_peak
        + 0.24 * max(a->diag_coherence_peak, a->diag_ridge_peak)
        + 0.14 * a->diag_signal_probability
        + 0.12 * a->agc_weak_signal_memory
        - 0.25) / 0.43,
      0.0, 1.0);
    if (a->learned_frames < 24) { dry_guard = 0.0; }
    double dry_mix = 0.56 * dry_guard;
    if (dry_mix > 0.0) {
      for (i = 0; i < a->bsize; i++) {
        double wet = a->out[2 * i + 0];
        double dry = a->dry[i];
        a->out[2 * i + 0] = wet + dry_mix * (dry - wet);
      }
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
void SetRXASPNRAdjacentNoiseProfile(int channel, int usable, int bins,
                                    int left_bins, int right_bins,
                                    double floor_db, double p10_db, double p50_db,
                                    double p90_db, double left_floor_db,
                                    double right_floor_db, double slope_db_per_khz,
                                    double rejected_pct) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return;
  }

  left_bins = max(left_bins, 0);
  right_bins = max(right_bins, 0);
  if (!spnr_finite(left_floor_db)) { left_floor_db = floor_db; }
  if (!spnr_finite(right_floor_db)) { right_floor_db = floor_db; }

  if (!usable || bins < 24 || !spnr_finite(floor_db)) {
    a->adjacent_noise_usable = 0;
    a->adjacent_noise_bins = max(bins, 0);
    a->adjacent_noise_left_bins = left_bins;
    a->adjacent_noise_right_bins = right_bins;
    a->adjacent_noise_left_floor_db = spnr_finite(left_floor_db) ? left_floor_db : -120.0;
    a->adjacent_noise_right_floor_db = spnr_finite(right_floor_db) ? right_floor_db : -120.0;
    a->adjacent_noise_rejected_pct = spnr_finite(rejected_pct)
      ? spnr_clip(rejected_pct, 0.0, 100.0)
      : 100.0;
    a->adjacent_noise_trust = 0.0;
    a->adjacent_noise_side_balance = 0.0;
    a->adjacent_noise_asymmetry_db = 0.0;
    LeaveCriticalSection(&ch[channel].csDSP);
    return;
  }

  if (!spnr_finite(p10_db)) { p10_db = floor_db; }
  if (!spnr_finite(p50_db)) { p50_db = floor_db; }
  if (!spnr_finite(p90_db)) { p90_db = floor_db; }
  if (!spnr_finite(left_floor_db)) { left_floor_db = floor_db; }
  if (!spnr_finite(right_floor_db)) { right_floor_db = floor_db; }
  if (!spnr_finite(slope_db_per_khz)) { slope_db_per_khz = 0.0; }
  if (!spnr_finite(rejected_pct)) { rejected_pct = 100.0; }

  double side_balance = 0.0;
  double asymmetry_db = 0.0;
  double side_guard = 0.58;
  if (left_bins > 0 && right_bins > 0) {
    int side_min = min(left_bins, right_bins);
    int side_max = max(left_bins, right_bins);
    side_balance = (double)side_min / (double)max(side_max, 1);
    asymmetry_db = fabs(right_floor_db - left_floor_db);
    if (!spnr_finite(asymmetry_db)) { asymmetry_db = 0.0; }
    double asymmetry_guard = spnr_clip((7.0 - asymmetry_db) / 7.0, 0.0, 1.0);
    side_guard = 0.50 + 0.30 * side_balance + 0.20 * asymmetry_guard;
  }

  double spread_db = spnr_clip(p90_db - p10_db, 0.0, 30.0);
  double spread_guard = spnr_clip((10.0 - spread_db) / 10.0, 0.0, 1.0);
  double slope_guard = spnr_clip((3.0 - fabs(slope_db_per_khz)) / 3.0, 0.0, 1.0);
  double reject_guard = spnr_clip((40.0 - rejected_pct) / 40.0, 0.0, 1.0);
  double bin_guard = spnr_clip(((double)bins - 24.0) / 72.0, 0.0, 1.0);
  double candidate_trust = spread_guard * slope_guard * reject_guard * side_guard * (0.35 + 0.65 * bin_guard);
  candidate_trust = spnr_clip(candidate_trust, 0.0, 0.92);

  a->adjacent_noise_usable = 1;
  a->adjacent_noise_bins = bins;
  a->adjacent_noise_left_bins = left_bins;
  a->adjacent_noise_right_bins = right_bins;
  a->adjacent_noise_floor_db = floor_db;
  a->adjacent_noise_p10_db = p10_db;
  a->adjacent_noise_p50_db = p50_db;
  a->adjacent_noise_p90_db = p90_db;
  a->adjacent_noise_left_floor_db = left_floor_db;
  a->adjacent_noise_right_floor_db = right_floor_db;
  a->adjacent_noise_slope_db_per_khz = slope_db_per_khz;
  a->adjacent_noise_rejected_pct = spnr_clip(rejected_pct, 0.0, 100.0);
  a->adjacent_noise_trust = 0.72 * a->adjacent_noise_trust + 0.28 * candidate_trust;
  a->adjacent_noise_side_balance = spnr_clip(side_balance, 0.0, 1.0);
  a->adjacent_noise_asymmetry_db = spnr_clip(asymmetry_db, 0.0, 60.0);
  LeaveCriticalSection(&ch[channel].csDSP);
}

PORT
int GetRXASPNRAdjacentNoiseDiagnostics(int channel, int* usable, int* bins,
                                       int* left_bins, int* right_bins,
                                       double* floor_db, double* left_floor_db,
                                       double* right_floor_db, double* trust,
                                       double* drive, double* rejected_pct,
                                       double* side_balance, double* asymmetry_db) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (usable) { *usable = a->adjacent_noise_usable; }
  if (bins) { *bins = a->adjacent_noise_bins; }
  if (left_bins) { *left_bins = a->adjacent_noise_left_bins; }
  if (right_bins) { *right_bins = a->adjacent_noise_right_bins; }
  if (floor_db) { *floor_db = a->adjacent_noise_floor_db; }
  if (left_floor_db) { *left_floor_db = a->adjacent_noise_left_floor_db; }
  if (right_floor_db) { *right_floor_db = a->adjacent_noise_right_floor_db; }
  if (trust) { *trust = a->adjacent_noise_trust; }
  if (drive) { *drive = a->diag_adjacent_noise_drive; }
  if (rejected_pct) { *rejected_pct = a->adjacent_noise_rejected_pct; }
  if (side_balance) { *side_balance = a->adjacent_noise_side_balance; }
  if (asymmetry_db) { *asymmetry_db = a->adjacent_noise_asymmetry_db; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
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

PORT
int GetRXASPNRProbabilityDiagnostics(int channel, double* signal_probability,
                                     double* texture_fill, double* mask_smoothing) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (signal_probability) { *signal_probability = a->diag_signal_probability; }
  if (texture_fill) { *texture_fill = a->diag_texture_fill; }
  if (mask_smoothing) { *mask_smoothing = a->diag_mask_smoothing; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}

PORT
int GetRXASPNRPeakDiagnostics(int channel, double* output_peak, double* peak_evidence,
                              double* peak_limit, double* peak_reduction_db) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (output_peak) { *output_peak = a->diag_output_peak; }
  if (peak_evidence) { *peak_evidence = a->diag_peak_evidence; }
  if (peak_limit) { *peak_limit = a->diag_peak_limit; }
  if (peak_reduction_db) { *peak_reduction_db = a->diag_peak_reduction_db; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}

PORT
int GetRXASPNRAgcDiagnostics(int channel, double* level_drive, double* recovery_drive,
                             double* makeup_gain) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (level_drive) { *level_drive = a->diag_level_drive; }
  if (recovery_drive) { *recovery_drive = a->diag_recovery_drive; }
  if (makeup_gain) { *makeup_gain = a->diag_makeup_gain; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}

PORT
int GetRXASPNRMemoryDiagnostics(int channel, double* weak_signal_memory) {
  if (channel < 0 || channel >= MAX_CHANNELS) { return 0; }

  EnterCriticalSection(&ch[channel].csDSP);
  SPNR a = rxa[channel].spnr.p;
  if (!a) {
    LeaveCriticalSection(&ch[channel].csDSP);
    return 0;
  }
  if (weak_signal_memory) { *weak_signal_memory = a->agc_weak_signal_memory; }
  LeaveCriticalSection(&ch[channel].csDSP);

  return 1;
}
