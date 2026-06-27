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


//  SP_fifo receive control  - Copyright 2009, 2010, 2011, 2012, 2014  Phil Harman VK6(A)PH

//  17 Dec 2014 - modified code for use with ANAN-10E. Send 8k of samples then 8k of zeros. 

/*
	The SP_fifo is filled with 8k consecutive samples from the ADC. Then fill with 8k of zeros.
	The code loops	until the fifo is empty then fills again.	
*/




module sp_rcv_ctrl2 (
							input  clk,
							input  sp_fifo_wrempty,
							input  sp_fifo_wrfull,
							input  reset,
							output  write,
							output  have_sp_data,
							output reg zero_fill
						);

reg state;
reg wrenable;

always @(posedge clk)
begin
  if (reset) begin 
    wrenable <= 1'b0;
	 state <= 0;
	 zero_fill <= 0;
  end 
 
// load SP_fifo alternatively with 8k raw 16 bit ADC samples or zeros every time it is empty    
case(state)
0: begin 
		if (sp_fifo_wrempty) begin  		// enable write to SP_fifo
			wrenable <= 1'b1;
			state <= 1;
		end 
   end 
   
1: begin 
		if (sp_fifo_wrfull) begin			// disable write to SP_fifo when its full
			wrenable <= 1'b0;
			zero_fill <= !zero_fill;		// toggle input to FIFO, either ADC or zeros.
			state <= 0;
		end
   end 
//
//// now fill with 8k of zeros - zero_fill when set selects this
//2: begin
//	if (sp_fifo_wrempty) begin 		
//			wrenable <= 1'b1;
//			zero_fill <= 1'b1;
//			state <= 3;
//		 end 
//   end 
//	
//3:	begin
//		if (sp_fifo_wrfull) begin			// disable write to SP_fifo when its full
//			zero_fill <= 1'b0;
//			wrenable <= 1'b0;
//			state <= 0;
//		end
//	end

default: state <= 0;
endcase
end

assign write = wrenable;   
assign have_sp_data = !wrenable;	 	// indicate data is availble to be read


endmodule
