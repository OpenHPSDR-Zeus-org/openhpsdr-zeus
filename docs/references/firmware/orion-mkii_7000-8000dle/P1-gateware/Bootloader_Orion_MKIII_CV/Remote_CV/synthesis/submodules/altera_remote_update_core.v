//altremote_update CBX_AUTO_BLACKBOX="ALL" CBX_SINGLE_OUTPUT_FILE="ON" check_app_pof="false" config_device_addr_width=24 DEVICE_FAMILY="Cyclone V" in_data_width=24 is_epcq="true" operation_mode="remote" out_data_width=24 busy clock ctl_nupdt data_in data_out param read_param reconfig reset reset_timer write_param
//VERSION_BEGIN 20.1 cbx_altremote_update 2020:11:11:17:03:37:SJ cbx_cycloneii 2020:11:11:17:03:37:SJ cbx_lpm_add_sub 2020:11:11:17:03:37:SJ cbx_lpm_compare 2020:11:11:17:03:37:SJ cbx_lpm_counter 2020:11:11:17:03:37:SJ cbx_lpm_decode 2020:11:11:17:03:37:SJ cbx_lpm_shiftreg 2020:11:11:17:03:37:SJ cbx_mgl 2020:11:11:17:50:46:SJ cbx_nadder 2020:11:11:17:03:37:SJ cbx_nightfury 2020:11:11:17:03:37:SJ cbx_stratix 2020:11:11:17:03:37:SJ cbx_stratixii 2020:11:11:17:03:37:SJ  VERSION_END
// synthesis VERILOG_INPUT_VERSION VERILOG_2001
// altera message_off 10463



// Copyright (C) 2020  Intel Corporation. All rights reserved.
//  Your use of Intel Corporation's design tools, logic functions 
//  and other software and tools, and any partner logic 
//  functions, and any output files from any of the foregoing 
//  (including device programming or simulation files), and any 
//  associated documentation or information are expressly subject 
//  to the terms and conditions of the Intel Program License 
//  Subscription Agreement, the Intel Quartus Prime License Agreement,
//  the Intel FPGA IP License Agreement, or other applicable license
//  agreement, including, without limitation, that your use is for
//  the sole purpose of programming logic devices manufactured by
//  Intel and sold by Intel or its authorized distributors.  Please
//  refer to the applicable agreement for further details, at
//  https://fpgasoftware.intel.com/eula.



//synthesis_resources = cyclonev_rublock 1 lpm_counter 2 reg 43 
//synopsys translate_off
`timescale 1 ps / 1 ps
//synopsys translate_on
(* ALTERA_ATTRIBUTE = {"suppress_da_rule_internal=c104;suppress_da_rule_internal=C101;suppress_da_rule_internal=C103"} *)
module  altera_remote_update_core
	( 
	busy,
	clock,
	ctl_nupdt,
	data_in,
	data_out,
	param,
	read_param,
	reconfig,
	reset,
	reset_timer,
	write_param) /* synthesis synthesis_clearbox=1 */;
	output   busy;
	input   clock;
	input   ctl_nupdt;
	input   [23:0]  data_in;
	output   [23:0]  data_out;
	input   [2:0]  param;
	input   read_param;
	input   reconfig;
	input   reset;
	input   reset_timer;
	input   write_param;
`ifndef ALTERA_RESERVED_QIS
// synopsys translate_off
`endif
	tri0   ctl_nupdt;
	tri0   [23:0]  data_in;
	tri0   [2:0]  param;
	tri0   read_param;
	tri0   reconfig;
	tri0   reset_timer;
	tri0   write_param;
`ifndef ALTERA_RESERVED_QIS
// synopsys translate_on
`endif

	reg	[0:0]	check_busy_dffe;
	reg	[23:0]	dffe4a;
	wire	[23:0]	wire_dffe4a_ena;
	reg	dffe5;
	reg	[2:0]	dffe6a;
	wire	[2:0]	wire_dffe6a_ena;
	reg	idle_state;
	reg	idle_write_wait;
	reg	read_address_state;
	wire	wire_read_address_state_ena;
	reg	read_data_state;
	reg	read_init_counter_state;
	reg	read_init_state;
	reg	read_post_state;
	reg	read_pre_data_state;
	reg	write_data_state;
	reg	write_init_counter_state;
	reg	write_init_state;
	reg	write_load_state;
	reg	write_post_data_state;
	reg	write_pre_data_state;
	reg	write_wait_state;
	wire  [5:0]   wire_cntr2_q;
	wire  [4:0]   wire_cntr3_q;
	wire  wire_sd1_regout;
	wire  bit_counter_all_done;
	wire  bit_counter_clear;
	wire  bit_counter_enable;
	wire  [5:0]  bit_counter_param_start;
	wire  bit_counter_param_start_match;
	wire  idle;
	wire  [2:0]  param_decoder_param_latch;
	wire  [4:0]  param_decoder_select;
	wire  power_up;
	wire  read_address;
	wire  read_data;
	wire  read_init;
	wire  read_init_counter;
	wire  read_post;
	wire  read_pre_data;
	wire  rublock_captnupdt;
	wire  rublock_clock;
	wire  rublock_reconfig;
	wire  rublock_reconfig_st;
	wire  rublock_regin;
	wire  rublock_regout;
	wire  rublock_regout_reg;
	wire  rublock_shiftnld;
	wire  select_shift_nloop;
	wire  shift_reg_clear;
	wire  shift_reg_load_enable;
	wire  shift_reg_serial_in;
	wire  shift_reg_serial_out;
	wire  shift_reg_shift_enable;
	wire  [5:0]  start_bit_decoder_out;
	wire  [4:0]  start_bit_decoder_param_select;
	wire  [5:0]  w22w;
	wire  [4:0]  w51w;
	wire  width_counter_all_done;
	wire  width_counter_clear;
	wire  width_counter_enable;
	wire  [4:0]  width_counter_param_width;
	wire  width_counter_param_width_match;
	wire  [4:0]  width_decoder_out;
	wire  [4:0]  width_decoder_param_select;
	wire  write_data;
	wire  write_init;
	wire  write_init_counter;
	wire  write_load;
	wire  write_post_data;
	wire  write_pre_data;
	wire  write_wait;

	// synopsys translate_off
	initial 
		 check_busy_dffe[0:0] = 0;
	// synopsys translate_on
	// synopsys translate_off
	initial
		dffe4a[0:0] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[0:0] <= 1'b0;
		else if  (wire_dffe4a_ena[0:0] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[0:0] <= 1'b0;
			else  dffe4a[0:0] <= ((shift_reg_load_enable & ((((data_in[0] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[0] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[1:1]));
	// synopsys translate_off
	initial
		dffe4a[1:1] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[1:1] <= 1'b0;
		else if  (wire_dffe4a_ena[1:1] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[1:1] <= 1'b0;
			else  dffe4a[1:1] <= ((shift_reg_load_enable & ((((data_in[1] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[1] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[2:2]));
	// synopsys translate_off
	initial
		dffe4a[2:2] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[2:2] <= 1'b0;
		else if  (wire_dffe4a_ena[2:2] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[2:2] <= 1'b0;
			else  dffe4a[2:2] <= ((shift_reg_load_enable & ((((data_in[2] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[2] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[3:3]));
	// synopsys translate_off
	initial
		dffe4a[3:3] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[3:3] <= 1'b0;
		else if  (wire_dffe4a_ena[3:3] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[3:3] <= 1'b0;
			else  dffe4a[3:3] <= ((shift_reg_load_enable & ((((data_in[3] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[3] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[4:4]));
	// synopsys translate_off
	initial
		dffe4a[4:4] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[4:4] <= 1'b0;
		else if  (wire_dffe4a_ena[4:4] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[4:4] <= 1'b0;
			else  dffe4a[4:4] <= ((shift_reg_load_enable & ((((data_in[4] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[4] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[5:5]));
	// synopsys translate_off
	initial
		dffe4a[5:5] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[5:5] <= 1'b0;
		else if  (wire_dffe4a_ena[5:5] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[5:5] <= 1'b0;
			else  dffe4a[5:5] <= ((shift_reg_load_enable & ((((data_in[5] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[5] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[6:6]));
	// synopsys translate_off
	initial
		dffe4a[6:6] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[6:6] <= 1'b0;
		else if  (wire_dffe4a_ena[6:6] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[6:6] <= 1'b0;
			else  dffe4a[6:6] <= ((shift_reg_load_enable & ((((data_in[6] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[6] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[7:7]));
	// synopsys translate_off
	initial
		dffe4a[7:7] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[7:7] <= 1'b0;
		else if  (wire_dffe4a_ena[7:7] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[7:7] <= 1'b0;
			else  dffe4a[7:7] <= ((shift_reg_load_enable & ((((data_in[7] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[7] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[8:8]));
	// synopsys translate_off
	initial
		dffe4a[8:8] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[8:8] <= 1'b0;
		else if  (wire_dffe4a_ena[8:8] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[8:8] <= 1'b0;
			else  dffe4a[8:8] <= ((shift_reg_load_enable & ((((data_in[8] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[8] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[9:9]));
	// synopsys translate_off
	initial
		dffe4a[9:9] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[9:9] <= 1'b0;
		else if  (wire_dffe4a_ena[9:9] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[9:9] <= 1'b0;
			else  dffe4a[9:9] <= ((shift_reg_load_enable & ((((data_in[9] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[9] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[10:10]));
	// synopsys translate_off
	initial
		dffe4a[10:10] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[10:10] <= 1'b0;
		else if  (wire_dffe4a_ena[10:10] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[10:10] <= 1'b0;
			else  dffe4a[10:10] <= ((shift_reg_load_enable & ((((data_in[10] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[10] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[11:11]));
	// synopsys translate_off
	initial
		dffe4a[11:11] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[11:11] <= 1'b0;
		else if  (wire_dffe4a_ena[11:11] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[11:11] <= 1'b0;
			else  dffe4a[11:11] <= ((shift_reg_load_enable & ((((data_in[11] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[11] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[12:12]));
	// synopsys translate_off
	initial
		dffe4a[12:12] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[12:12] <= 1'b0;
		else if  (wire_dffe4a_ena[12:12] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[12:12] <= 1'b0;
			else  dffe4a[12:12] <= ((shift_reg_load_enable & ((((data_in[12] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[12] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[13:13]));
	// synopsys translate_off
	initial
		dffe4a[13:13] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[13:13] <= 1'b0;
		else if  (wire_dffe4a_ena[13:13] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[13:13] <= 1'b0;
			else  dffe4a[13:13] <= ((shift_reg_load_enable & ((((data_in[13] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[13] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[14:14]));
	// synopsys translate_off
	initial
		dffe4a[14:14] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[14:14] <= 1'b0;
		else if  (wire_dffe4a_ena[14:14] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[14:14] <= 1'b0;
			else  dffe4a[14:14] <= ((shift_reg_load_enable & ((((data_in[14] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[14] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[15:15]));
	// synopsys translate_off
	initial
		dffe4a[15:15] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[15:15] <= 1'b0;
		else if  (wire_dffe4a_ena[15:15] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[15:15] <= 1'b0;
			else  dffe4a[15:15] <= ((shift_reg_load_enable & ((((data_in[15] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[15] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[16:16]));
	// synopsys translate_off
	initial
		dffe4a[16:16] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[16:16] <= 1'b0;
		else if  (wire_dffe4a_ena[16:16] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[16:16] <= 1'b0;
			else  dffe4a[16:16] <= ((shift_reg_load_enable & ((((data_in[16] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[16] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[17:17]));
	// synopsys translate_off
	initial
		dffe4a[17:17] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[17:17] <= 1'b0;
		else if  (wire_dffe4a_ena[17:17] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[17:17] <= 1'b0;
			else  dffe4a[17:17] <= ((shift_reg_load_enable & ((((data_in[17] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[17] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[18:18]));
	// synopsys translate_off
	initial
		dffe4a[18:18] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[18:18] <= 1'b0;
		else if  (wire_dffe4a_ena[18:18] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[18:18] <= 1'b0;
			else  dffe4a[18:18] <= ((shift_reg_load_enable & ((((data_in[18] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[18] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[19:19]));
	// synopsys translate_off
	initial
		dffe4a[19:19] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[19:19] <= 1'b0;
		else if  (wire_dffe4a_ena[19:19] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[19:19] <= 1'b0;
			else  dffe4a[19:19] <= ((shift_reg_load_enable & ((((data_in[19] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[19] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[20:20]));
	// synopsys translate_off
	initial
		dffe4a[20:20] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[20:20] <= 1'b0;
		else if  (wire_dffe4a_ena[20:20] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[20:20] <= 1'b0;
			else  dffe4a[20:20] <= ((shift_reg_load_enable & ((((data_in[20] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[20] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[21:21]));
	// synopsys translate_off
	initial
		dffe4a[21:21] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[21:21] <= 1'b0;
		else if  (wire_dffe4a_ena[21:21] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[21:21] <= 1'b0;
			else  dffe4a[21:21] <= ((shift_reg_load_enable & ((((data_in[21] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[21] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[22:22]));
	// synopsys translate_off
	initial
		dffe4a[22:22] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[22:22] <= 1'b0;
		else if  (wire_dffe4a_ena[22:22] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[22:22] <= 1'b0;
			else  dffe4a[22:22] <= ((shift_reg_load_enable & ((((data_in[22] & param[2]) & (~ param[1])) & (~ param[0])) | (data_in[22] & (~ ((param[2] & (~ param[1])) & (~ param[0])))))) | ((~ shift_reg_load_enable) & dffe4a[23:23]));
	// synopsys translate_off
	initial
		dffe4a[23:23] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe4a[23:23] <= 1'b0;
		else if  (wire_dffe4a_ena[23:23] == 1'b1) 
			if (shift_reg_clear == 1'b1) dffe4a[23:23] <= 1'b0;
			else  dffe4a[23:23] <= ((shift_reg_load_enable & data_in[23]) | ((~ shift_reg_load_enable) & shift_reg_serial_in));
	assign
		wire_dffe4a_ena = {24{((shift_reg_load_enable | shift_reg_shift_enable) | shift_reg_clear)}};
	// synopsys translate_off
	initial
		dffe5 = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe5 <= 1'b0;
		else  dffe5 <= rublock_regout;
	// synopsys translate_off
	initial
		dffe6a[0:0] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe6a[0:0] <= 1'b0;
		else if  (wire_dffe6a_ena[0:0] == 1'b1)   dffe6a[0:0] <= param[0:0];
	// synopsys translate_off
	initial
		dffe6a[1:1] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe6a[1:1] <= 1'b0;
		else if  (wire_dffe6a_ena[1:1] == 1'b1)   dffe6a[1:1] <= param[1:1];
	// synopsys translate_off
	initial
		dffe6a[2:2] = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) dffe6a[2:2] <= 1'b0;
		else if  (wire_dffe6a_ena[2:2] == 1'b1)   dffe6a[2:2] <= param[2:2];
	assign
		wire_dffe6a_ena = {3{(idle & (write_param | read_param))}};
	// synopsys translate_off
	initial
		idle_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) idle_state <= {1{1'b1}};
		else  idle_state <= ((((((idle & (~ read_param)) & (~ write_param)) | write_wait) | (read_data & width_counter_all_done)) | (read_post & width_counter_all_done)) | power_up);
	// synopsys translate_off
	initial
		idle_write_wait = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) idle_write_wait <= 1'b0;
		else  idle_write_wait <= (((((((idle & (~ read_param)) & (~ write_param)) | write_wait) | (read_data & width_counter_all_done)) | (read_post & width_counter_all_done)) | power_up) & write_load);
	// synopsys translate_off
	initial
		read_address_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_address_state <= 1'b0;
		else if  (wire_read_address_state_ena == 1'b1)   read_address_state <= (((read_param | write_param) & ((param[2] & (~ param[1])) & (~ param[0]))) & (~ (~ idle)));
	assign
		wire_read_address_state_ena = (read_param | write_param);
	// synopsys translate_off
	initial
		read_data_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_data_state <= 1'b0;
		else  read_data_state <= (((read_init_counter & bit_counter_param_start_match) | (read_pre_data & bit_counter_param_start_match)) | ((read_data & (~ width_counter_param_width_match)) & (~ width_counter_all_done)));
	// synopsys translate_off
	initial
		read_init_counter_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_init_counter_state <= 1'b0;
		else  read_init_counter_state <= read_init;
	// synopsys translate_off
	initial
		read_init_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_init_state <= 1'b0;
		else  read_init_state <= (idle & read_param);
	// synopsys translate_off
	initial
		read_post_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_post_state <= 1'b0;
		else  read_post_state <= (((read_data & width_counter_param_width_match) & (~ width_counter_all_done)) | (read_post & (~ width_counter_all_done)));
	// synopsys translate_off
	initial
		read_pre_data_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) read_pre_data_state <= 1'b0;
		else  read_pre_data_state <= ((read_init_counter & (~ bit_counter_param_start_match)) | (read_pre_data & (~ bit_counter_param_start_match)));
	// synopsys translate_off
	initial
		write_data_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_data_state <= 1'b0;
		else  write_data_state <= (((write_init_counter & bit_counter_param_start_match) | (write_pre_data & bit_counter_param_start_match)) | ((write_data & (~ width_counter_param_width_match)) & (~ bit_counter_all_done)));
	// synopsys translate_off
	initial
		write_init_counter_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_init_counter_state <= 1'b0;
		else  write_init_counter_state <= write_init;
	// synopsys translate_off
	initial
		write_init_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_init_state <= 1'b0;
		else  write_init_state <= (idle & write_param);
	// synopsys translate_off
	initial
		write_load_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_load_state <= 1'b0;
		else  write_load_state <= ((write_data & bit_counter_all_done) | (write_post_data & bit_counter_all_done));
	// synopsys translate_off
	initial
		write_post_data_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_post_data_state <= 1'b0;
		else  write_post_data_state <= (((write_data & width_counter_param_width_match) & (~ bit_counter_all_done)) | (write_post_data & (~ bit_counter_all_done)));
	// synopsys translate_off
	initial
		write_pre_data_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_pre_data_state <= 1'b0;
		else  write_pre_data_state <= ((write_init_counter & (~ bit_counter_param_start_match)) | (write_pre_data & (~ bit_counter_param_start_match)));
	// synopsys translate_off
	initial
		write_wait_state = 0;
	// synopsys translate_on
	always @ ( posedge clock or  posedge reset)
		if (reset == 1'b1) write_wait_state <= 1'b0;
		else  write_wait_state <= write_load;
	lpm_counter   cntr2
	( 
	.aclr(reset),
	.clock(clock),
	.cnt_en(bit_counter_enable),
	.cout(),
	.eq(),
	.q(wire_cntr2_q),
	.sclr(bit_counter_clear)
	`ifndef FORMAL_VERIFICATION
	// synopsys translate_off
	`endif
	,
	.aload(1'b0),
	.aset(1'b0),
	.cin(1'b1),
	.clk_en(1'b1),
	.data({6{1'b0}}),
	.sload(1'b0),
	.sset(1'b0),
	.updown(1'b1)
	`ifndef FORMAL_VERIFICATION
	// synopsys translate_on
	`endif
	);
	defparam
		cntr2.lpm_direction = "UP",
		cntr2.lpm_port_updown = "PORT_UNUSED",
		cntr2.lpm_width = 6,
		cntr2.lpm_type = "lpm_counter";
	lpm_counter   cntr3
	( 
	.aclr(reset),
	.clock(clock),
	.cnt_en(width_counter_enable),
	.cout(),
	.eq(),
	.q(wire_cntr3_q),
	.sclr(width_counter_clear)
	`ifndef FORMAL_VERIFICATION
	// synopsys translate_off
	`endif
	,
	.aload(1'b0),
	.aset(1'b0),
	.cin(1'b1),
	.clk_en(1'b1),
	.data({5{1'b0}}),
	.sload(1'b0),
	.sset(1'b0),
	.updown(1'b1)
	`ifndef FORMAL_VERIFICATION
	// synopsys translate_on
	`endif
	);
	defparam
		cntr3.lpm_direction = "UP",
		cntr3.lpm_port_updown = "PORT_UNUSED",
		cntr3.lpm_width = 5,
		cntr3.lpm_type = "lpm_counter";
	cyclonev_rublock   sd1
	( 
	.captnupdt(rublock_captnupdt),
	.clk(rublock_clock),
	.rconfig(rublock_reconfig),
	.regin(rublock_regin),
	.regout(wire_sd1_regout),
	.rsttimer(reset_timer),
	.shiftnld(rublock_shiftnld));
	assign
		bit_counter_all_done = (((((wire_cntr2_q[0] & wire_cntr2_q[1]) & (~ wire_cntr2_q[2])) & wire_cntr2_q[3]) & (~ wire_cntr2_q[4])) & wire_cntr2_q[5]),
		bit_counter_clear = (read_init | write_init),
		bit_counter_enable = (((((((((read_init | write_init) | read_init_counter) | write_init_counter) | read_pre_data) | write_pre_data) | read_data) | write_data) | read_post) | write_post_data),
		bit_counter_param_start = start_bit_decoder_out,
		bit_counter_param_start_match = ((((((~ w22w[0]) & (~ w22w[1])) & (~ w22w[2])) & (~ w22w[3])) & (~ w22w[4])) & (~ w22w[5])),
		busy = (~ idle),
		data_out = {((read_address & dffe4a[23]) | ((~ read_address) & dffe4a[23])), ((read_address & dffe4a[22]) | ((~ read_address) & dffe4a[22])), ((read_address & dffe4a[21]) | ((~ read_address) & dffe4a[21])), ((read_address & dffe4a[20]) | ((~ read_address) & dffe4a[20])), ((read_address & dffe4a[19]) | ((~ read_address) & dffe4a[19])), ((read_address & dffe4a[18]) | ((~ read_address) & dffe4a[18])), ((read_address & dffe4a[17]) | ((~ read_address) & dffe4a[17])), ((read_address & dffe4a[16]) | ((~ read_address) & dffe4a[16])), ((read_address & dffe4a[15]) | ((~ read_address) & dffe4a[15])), ((read_address & dffe4a[14]) | ((~ read_address) & dffe4a[14])), ((read_address & dffe4a[13]) | ((~ read_address) & dffe4a[13])), ((read_address & dffe4a[12]) | ((~ read_address) & dffe4a[12])), ((read_address & dffe4a[11]) | ((~ read_address) & dffe4a[11])), ((read_address & dffe4a[10]) | ((~ read_address) & dffe4a[10])), ((read_address & dffe4a[9]) | ((~ read_address) & dffe4a[9])), ((read_address & dffe4a[8]) | ((~ read_address) & dffe4a[8])), ((read_address & dffe4a[7]) | ((~ read_address) & dffe4a[7])), ((read_address & dffe4a[6]) | ((~ read_address) & dffe4a[6])), ((read_address & dffe4a[5]) | ((~ read_address) & dffe4a[5])), ((read_address & dffe4a[4]) | ((~ read_address) & dffe4a[4])), ((read_address & dffe4a[3]) | ((~ read_address) & dffe4a[3])), ((read_address & dffe4a[2]) | ((~ read_address) & dffe4a[2])), ((read_address & dffe4a[1]) | ((~ read_address) & dffe4a[1])), ((read_address & dffe4a[0]) | ((~ read_address) & dffe4a[0]))},
		idle = idle_state,
		param_decoder_param_latch = dffe6a,
		param_decoder_select = {((param_decoder_param_latch[0] & (~ param_decoder_param_latch[1])) & param_decoder_param_latch[2]), (((~ param_decoder_param_latch[0]) & (~ param_decoder_param_latch[1])) & param_decoder_param_latch[2]), ((param_decoder_param_latch[0] & param_decoder_param_latch[1]) & (~ param_decoder_param_latch[2])), (((~ param_decoder_param_latch[0]) & param_decoder_param_latch[1]) & (~ param_decoder_param_latch[2])), (((~ param_decoder_param_latch[0]) & (~ param_decoder_param_latch[1])) & (~ param_decoder_param_latch[2]))},
		power_up = (((((((((((((~ idle) & (~ read_init)) & (~ read_init_counter)) & (~ read_pre_data)) & (~ read_data)) & (~ read_post)) & (~ write_init)) & (~ write_init_counter)) & (~ write_pre_data)) & (~ write_data)) & (~ write_post_data)) & (~ write_load)) & (~ write_wait)),
		read_address = read_address_state,
		read_data = read_data_state,
		read_init = read_init_state,
		read_init_counter = read_init_counter_state,
		read_post = read_post_state,
		read_pre_data = read_pre_data_state,
		rublock_captnupdt = (~ write_load),
		rublock_clock = (~ (clock | idle_write_wait)),
		rublock_reconfig = rublock_reconfig_st,
		rublock_reconfig_st = (idle & reconfig),
		rublock_regin = ((rublock_regout_reg & (~ select_shift_nloop)) | (shift_reg_serial_out & select_shift_nloop)),
		rublock_regout = wire_sd1_regout,
		rublock_regout_reg = dffe5,
		rublock_shiftnld = (((((read_pre_data | write_pre_data) | read_data) | write_data) | read_post) | write_post_data),
		select_shift_nloop = ((read_data & (~ width_counter_param_width_match)) | (write_data & (~ width_counter_param_width_match))),
		shift_reg_clear = read_init,
		shift_reg_load_enable = (idle & write_param),
		shift_reg_serial_in = (rublock_regout_reg & select_shift_nloop),
		shift_reg_serial_out = dffe4a[0:0],
		shift_reg_shift_enable = (((read_data | write_data) | read_post) | write_post_data),
		start_bit_decoder_out = (((({6{1'b0}} | {1'b0, {5{start_bit_decoder_param_select[1]}}}) | {1'b0, {4{start_bit_decoder_param_select[2]}}, 1'b0}) | {{3{1'b0}}, {2{start_bit_decoder_param_select[3]}}, 1'b0}) | {{3{1'b0}}, start_bit_decoder_param_select[4], 1'b0, start_bit_decoder_param_select[4]}),
		start_bit_decoder_param_select = param_decoder_select,
		w22w = (wire_cntr2_q ^ bit_counter_param_start),
		w51w = (wire_cntr3_q ^ width_counter_param_width),
		width_counter_all_done = ((((wire_cntr3_q[0] & wire_cntr3_q[1]) & wire_cntr3_q[2]) & (~ wire_cntr3_q[3])) & wire_cntr3_q[4]),
		width_counter_clear = (read_init | write_init),
		width_counter_enable = ((read_data | write_data) | read_post),
		width_counter_param_width = width_decoder_out,
		width_counter_param_width_match = (((((~ w51w[0]) & (~ w51w[1])) & (~ w51w[2])) & (~ w51w[3])) & (~ w51w[4])),
		width_decoder_out = (((({{2{1'b0}}, width_decoder_param_select[0], 1'b0, width_decoder_param_select[0]} | {1'b0, {2{width_decoder_param_select[1]}}, {2{1'b0}}}) | {{4{1'b0}}, width_decoder_param_select[2]}) | {{2{width_decoder_param_select[3]}}, {3{1'b0}}}) | {{4{1'b0}}, width_decoder_param_select[4]}),
		width_decoder_param_select = param_decoder_select,
		write_data = write_data_state,
		write_init = write_init_state,
		write_init_counter = write_init_counter_state,
		write_load = write_load_state,
		write_post_data = write_post_data_state,
		write_pre_data = write_pre_data_state,
		write_wait = write_wait_state;
endmodule //altera_remote_update_core
//VALID FILE
