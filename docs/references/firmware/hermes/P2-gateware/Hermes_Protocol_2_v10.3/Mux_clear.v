//-----------------------------------------------------------------------------
//                          Mux_clear.v
//-----------------------------------------------------------------------------

//
//  HPSDR - High Performance Software Defined Radio
//
//  openHPSDR  code. 
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


//  copyright 2010, 2011, 2012, 2013, 2014, 2015, 2016 Phil Harman VK6PH


/* 
	Used to reset Rx FIFOs whenever Mux changes state. Checks that FIFO is 
	actually empty before releasing clear signal.	
*/

module Mux_clear ( 
				input clock,
				input [7:0] Mux,
				input Rx_fifo_empty,
				output reg fifo_clear
				);

reg [7:0]previous_Mux;
reg test;
	
always @ (posedge clock)   
begin 
	case (test)
	0: begin 
		previous_Mux <= Mux;						// save current Sync state
			if (previous_Mux != Mux ) begin 	// if Mux changes state flush the FIFO buffer
					fifo_clear <= 1;	
					test <= 1;
			end
		end
		
	1: begin 
			if(Rx_fifo_empty) begin 
					test <= 0;
					fifo_clear <= 0;
			end
		end 		

	endcase
end

endmodule
