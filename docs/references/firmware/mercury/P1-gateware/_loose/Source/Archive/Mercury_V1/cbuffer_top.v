/*
cbuffer_top.v Circular Buffer Module

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

module cbuffer_top(

input clk,
input reset,
input signed [DATA_WIDTH-1:0] data_in,
input wren,
input rden,
output reg signed [DATA_WIDTH-1:0] data_out

);

parameter DATA_WIDTH = 48;
parameter ADDR_WIDTH = 8;
parameter RAM_DEPTH = (1 << ADDR_WIDTH);
 
reg [ADDR_WIDTH-1:0] wr_pointer;
reg [ADDR_WIDTH-1:0] rd_pointer;
reg signed [DATA_WIDTH-1:0] mem [0:RAM_DEPTH-1];

/* integer i;

//for simulation
initial begin
	for (i=0; i<RAM_DEPTH; i=i+1)
		mem[i] <= 48'sh0;		
end
*/

// handle the write pointer
always @(posedge clk)
begin : WRITE_POINTER
	if (reset) begin
		wr_pointer <= 0;
		rd_pointer <= 0;		
	end else if (wren) begin
		wr_pointer <= wr_pointer + 1;
		rd_pointer <= wr_pointer;		
	end else if (rden) begin
		rd_pointer <= rd_pointer - 1;		
	end		
end

// writing the buffer
always @(posedge clk)
begin : WRITE_RAM
	if (wren) begin
		mem[wr_pointer] <= data_in;
	end 
end

// reading the buffer
always @(posedge clk)
begin : READ_RAM
	if (rden) begin
		data_out <= mem[rd_pointer];	
	end
end

endmodule