/*
MAC.v Multiply and Accumulate Module

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

module MAC(

input clk,
input reset,
input rden,
input wren,
input signed [WIDTH-1:0] data_in_M1,
input signed [WIDTH-1:0] data_in_M2,
output reg signed [(WIDTH * 2)-1:0] accum_out

);

parameter WIDTH = 24;

reg signed [((WIDTH * 2)-1):0] mult_temp;
reg signed [(WIDTH * 2):0] accum_temp;

always @(posedge clk)
begin
	if (reset) begin
		accum_temp <= 0;
		mult_temp <= 0;
		accum_out <= accum_out;
	end else begin
		if (wren) begin
			mult_temp <= data_in_M1 * data_in_M2;
			accum_temp <= accum_temp + mult_temp;	
		end else if (rden) begin
			accum_out <= accum_temp[((WIDTH * 2)):1];
		end
	end
end

endmodule
