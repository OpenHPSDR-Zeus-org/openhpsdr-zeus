/*  spnr.h

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2026 Zeus contributors

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

Signal-preserving noise reduction for experimental Zeus NR5.
*/

#ifndef _spnr_h
#define _spnr_h

typedef struct _spnr {
  int run;
  int position;
  int bsize;
  double* in;
  double* out;
  int fsize;
  int ovrlp;
  int incr;
  double rate;
  double* window;
  int iasize;
  double* inaccum;
  double* forfftin;
  double* forfftout;
  int msize;
  double* revfftin;
  double* revfftout;
  double** save;
  int oasize;
  double* outaccum;
  int nsamps;
  int iainidx;
  int iaoutidx;
  int init_oainidx;
  int oainidx;
  int oaoutidx;
  int saveidx;
  fftw_plan Rfor;
  fftw_plan Rrev;
  double* power;
  double* noise;
  double* smooth;
  double* presence;
  double* salience;
  double* prev_phase;
  double* prev_phase_delta;
  double* coherence;
  double* ridge;
  double* floor_bias;
  double* gain;
  double* prev_gain;
  int learned_frames;
  double aggressiveness;
  int agc_run;
  double target_rms;
  double max_gain;
  double agc_gain;
  double agc_gate;
  double agc_env;
  double agc_attack;
  double agc_release;
  double diag_input_rms;
  double diag_output_rms;
  double diag_presence_peak;
  double diag_salience_peak;
  double diag_coherence_peak;
  double diag_ridge_peak;
  double diag_mean_gain;
  double diag_min_gain;
  double diag_noise_floor_db;
  double diag_floor_reduction_db;
  double diag_dynamic_range_db;
  double diag_signal_confidence;
  double diag_agc_gate;
} spnr, *SPNR;

extern SPNR create_spnr(int run, int position, int size, double* in, double* out,
                        int fsize, int ovrlp, int rate);

extern void destroy_spnr(SPNR a);

extern void flush_spnr(SPNR a);

extern void xspnr(SPNR a, int pos);

extern void setBuffers_spnr(SPNR a, double* in, double* out);

extern void setSamplerate_spnr(SPNR a, int rate);

extern void setSize_spnr(SPNR a, int size);

#endif
