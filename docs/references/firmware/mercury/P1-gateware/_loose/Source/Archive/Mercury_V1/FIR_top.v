/*
FIR_top.v FIR module

Part of FIR module for HPSDR MERCURY/QS1R Project
Copyright (C) 2007  Philip A. Covington, N8VB

The author can be reached at:
p.covington@gmail.com

This library is free software; you can redistribute it and/or
modify it under the terms of the GNU Library General Public
License as published by the Free Software Foundation; either
version 2 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Library General Public License for more details.

You should have received a copy of the GNU Library General Public
License along with this library; if not, write to the
Free Software Foundation, Inc., 51 Franklin St, Fifth Floor,
Boston, MA  02110-1301, USA.
*/

// Uses:
// cbuffer_top.v
// cROM_Sequencer.v (uses cROM.v)
// MAC.v

module FIR_top(

input clk,
input reset,
input signed [INPUT_WIDTH-1:0] data_in_I,
input signed [INPUT_WIDTH-1:0] data_in_Q,
input strobe_in,

output signed [OUTPUT_WIDTH-1:0] data_out_I,
output signed [OUTPUT_WIDTH-1:0] data_out_Q,
output reg strobe_out

);

parameter INPUT_WIDTH = 24;
parameter OUTPUT_WIDTH = 48;
parameter TAPS = 9'd256;

parameter IDLE_STATE = 8'd0;
parameter BEGIN_MAC = 8'd1;
parameter DO_MAC = 8'd2;
parameter ACCUM_OUT = 8'd3;
parameter STROBE_OUT = 8'd4;

reg coeff_reset;
reg buff_wren;
reg buff_rden;
reg mac_wren;
reg mac_rden;
reg mac_reset;

wire signed [INPUT_WIDTH-1:0] coeff_data;
wire signed [INPUT_WIDTH-1:0] data_buff_I;
wire signed [INPUT_WIDTH-1:0] data_buff_Q;

cbuffer_top cbuffer_inst
(
.clk(clk),
.reset(reset),
.data_in({data_in_Q, data_in_I}),
.wren(buff_wren),
.rden(buff_rden),
.data_out({data_buff_Q, data_buff_I})
);

cROM_Sequencer cROM_seq_inst
(
.clk(clk), 
.reset(coeff_reset), 
.data(coeff_data)
);

MAC MAC_inst_I
(
.clk(clk),
.reset(mac_reset),
.wren(mac_wren),
.rden(mac_rden),
.data_in_M1(data_buff_I),
.data_in_M2(coeff_data),
.accum_out(data_out_I)
);

MAC MAC_inst_Q
(
.clk(clk),
.reset(mac_reset),
.wren(mac_wren),
.rden(mac_rden),
.data_in_M1(data_buff_Q),
.data_in_M2(coeff_data),
.accum_out(data_out_Q)
);

reg [7:0] state;
reg [8:0] cycle_counter;
reg [1:0] stb_state;

always @(posedge clk)
begin
	if (reset) begin
		coeff_reset <= 1'b1;
		buff_wren <= 1'b0;
		buff_rden <= 1'b0;
		mac_wren <= 1'b0;
		mac_rden <= 1'b0;
		mac_reset <= 1'b1;
		state <= IDLE_STATE;
		strobe_out <= 1'b0;
		stb_state <= 2'd0;		
		cycle_counter <= 9'd0;
	end else begin
		case (state)
			IDLE_STATE: begin
				if (strobe_in) begin
					buff_wren <= 1'b1;
					cycle_counter <= 9'd0;
					strobe_out <= 1'b0;
					state <= BEGIN_MAC;
				end else begin
					state <= IDLE_STATE;
					cycle_counter <= 9'd0;
					strobe_out <= 1'b0;
					coeff_reset <= 1'b1;
					mac_reset <= 1'b1;
				end
			end
			BEGIN_MAC: begin
				buff_wren <= 1'b0;
				buff_rden <= 1'b1;
				mac_wren <= 1'b1;
				coeff_reset <= 1'b0;
				mac_reset <= 1'b0;
				cycle_counter <= cycle_counter + 1;
				state <= DO_MAC;
			end
			DO_MAC: begin
				if (cycle_counter >= (TAPS+1)) begin
					buff_rden <= 1'b0;
					coeff_reset <= 1'b1;
					mac_wren <= 1'b0;
					state <= ACCUM_OUT;
				end else begin
					cycle_counter <= cycle_counter + 1;
					state <= DO_MAC;
				end
			end
			ACCUM_OUT: begin
				mac_rden <= 1'b1;
				state <= STROBE_OUT;
			end
			STROBE_OUT: begin
				if (stb_state == 2'd2) begin
					strobe_out <= 1'b1;
				end
				stb_state <= stb_state + 2'd2; // decimate by two
				state <= IDLE_STATE;	
			end			
		endcase
	end
end

endmodule

