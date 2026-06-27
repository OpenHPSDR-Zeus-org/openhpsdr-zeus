//
//  HPSDR - High Performance Software Defined Radio
//
//  Metis code. 
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA


//  SP_fifo receive control  - Copyright 2009, 2010, 2011  Phil Harman VK6APH

/*
	Mercury sends raw 16 bit ADC samples to Metis via Atlas A12. The SP_fifo is filled 
	with 4096 consecutive samples. The code loops until the fifo is empty then sets the 
	write. Once the SP_fifo is full wire is reset.
	
	spd_rdy comes from nWire receiver on Atlas A12. Pulse is high when data is avaliable.
	Pulse is too wide to use as is so is gated with delayed version of itself.

*/




module sp_rcv_ctrl (clk, reset, spd_rdy, spd_ack, 
					sp_fifo_wrempty, sp_fifo_wrfull, write, have_sp_data );

input wire clk;
input wire spd_rdy;
input wire sp_fifo_wrempty;
input wire sp_fifo_wrfull;
input wire reset;

output reg  spd_ack;
output wire write;
output wire have_sp_data;

reg state;
reg sp_wrreq;
reg wrenable;

always @(posedge clk)
begin
  if (reset) begin 
    spd_ack <= 1'b0;
    sp_wrreq <= 1'b0;
  end 
  else begin
    spd_ack <= spd_rdy; 				// one IF_clk delay
    sp_wrreq <= spd_rdy & !spd_ack;     // create fifo write pulse
  end 
 
// load SP_fifo with 4096 raw 16 bit ADC samples every time it is empty    
case(state)
0: begin 
	if (sp_fifo_wrempty) begin  // enable write to SP_fifo
		wrenable <= 1'b1;
		state <= 1'b1;
	end 
   end 
   
1: begin 
	if (sp_fifo_wrfull) begin	// disable write to SP_fifo
	   wrenable <= 1'b0;
	   state <= 1'b0;
	end
   end 
default: state <= 1'b0;
endcase
end

assign write = (sp_wrreq && wrenable);   // gate fifo write pulse
assign have_sp_data = !wrenable;			 // indicate data is availble to be read


endmodule
