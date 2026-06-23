/* rnnoise_data.c — Zeus minimal vendor (init_rnnoise only).
   Upstream xiph/rnnoise src/rnnoise_data.c is ~78 MB: default-model weight
   arrays (all inside #ifndef USE_WEIGHTS_FILE) + init_rnnoise(). Zeus builds
   with -DUSE_WEIGHTS_FILE and ships NO baked-in model, so the weight blocks
   compile out and only init_rnnoise() remains. This is that function,
   extracted verbatim from upstream @ 70f1d256, so the repo carries
   kilobytes not 78 MB. init_rnnoise() binds weights by NAME from the
   runtime-loaded WeightArray list, independent of the omitted default
   weights; only the architecture in rnnoise_data.h must match the loaded
   model. Re-extract alongside the header when re-vendoring (see VENDORING.md). */
#include "rnnoise_data.h"

int init_rnnoise(RNNoise *model, const WeightArray *arrays) {
    if (linear_init(&model->conv1, arrays, "conv1_bias", NULL, NULL,"conv1_weights_float", NULL, NULL, NULL, 195, 128)) return 1;
    if (linear_init(&model->conv2, arrays, "conv2_bias", "conv2_subias", "conv2_weights_int8","conv2_weights_float", NULL, NULL, "conv2_scale", 384, 384)) return 1;
    if (linear_init(&model->gru1_input, arrays, "gru1_input_bias", "gru1_input_subias", "gru1_input_weights_int8","gru1_input_weights_float", "gru1_input_weights_idx", NULL, "gru1_input_scale", 384, 1152)) return 1;
    if (linear_init(&model->gru1_recurrent, arrays, "gru1_recurrent_bias", "gru1_recurrent_subias", "gru1_recurrent_weights_int8","gru1_recurrent_weights_float", "gru1_recurrent_weights_idx", "gru1_recurrent_weights_diag", "gru1_recurrent_scale", 384, 1152)) return 1;
    if (linear_init(&model->gru2_input, arrays, "gru2_input_bias", "gru2_input_subias", "gru2_input_weights_int8","gru2_input_weights_float", "gru2_input_weights_idx", NULL, "gru2_input_scale", 384, 1152)) return 1;
    if (linear_init(&model->gru2_recurrent, arrays, "gru2_recurrent_bias", "gru2_recurrent_subias", "gru2_recurrent_weights_int8","gru2_recurrent_weights_float", "gru2_recurrent_weights_idx", "gru2_recurrent_weights_diag", "gru2_recurrent_scale", 384, 1152)) return 1;
    if (linear_init(&model->gru3_input, arrays, "gru3_input_bias", "gru3_input_subias", "gru3_input_weights_int8","gru3_input_weights_float", "gru3_input_weights_idx", NULL, "gru3_input_scale", 384, 1152)) return 1;
    if (linear_init(&model->gru3_recurrent, arrays, "gru3_recurrent_bias", "gru3_recurrent_subias", "gru3_recurrent_weights_int8","gru3_recurrent_weights_float", "gru3_recurrent_weights_idx", "gru3_recurrent_weights_diag", "gru3_recurrent_scale", 384, 1152)) return 1;
    if (linear_init(&model->dense_out, arrays, "dense_out_bias", NULL, NULL,"dense_out_weights_float", NULL, NULL, NULL, 1536, 32)) return 1;
    if (linear_init(&model->vad_dense, arrays, "vad_dense_bias", NULL, NULL,"vad_dense_weights_float", NULL, NULL, NULL, 1536, 1)) return 1;
    return 0;
}
