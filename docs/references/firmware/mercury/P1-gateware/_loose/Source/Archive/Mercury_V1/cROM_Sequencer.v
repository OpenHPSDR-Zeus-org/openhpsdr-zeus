/*
cROM Sequencer Module

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

module cROM_Sequencer(clk, reset, data);
		
	input clk;
	input reset;
	output signed [COEF_WIDTH-1:0] data;
	
	parameter COEF_WIDTH = 24;
	parameter TAPS = 256;
	parameter MAX_ADR = 8;
	
	reg [MAX_ADR-1:0] sequence_counter;
	
	// this is the coefficient ROM instance
	cROM cROM_inst(
	.clk(clk),
	.reset(reset),
	.addr(sequence_counter),
	.data(data)
	);
	
	always @(posedge clk) begin
		if (reset) begin
			sequence_counter <= 0;
		end else if (sequence_counter < (TAPS-1)) begin
			sequence_counter <= sequence_counter + 1;
		end	else begin
			sequence_counter <= (TAPS-1);
		end		
	end
	
endmodule
