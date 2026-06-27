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

 When moving from conventional to sync DDC mode for PureSignal we first inhibit any further writes to the Rx0 fifo.
 We then need to ensure there is no Rx0 DDC data currently being sent (phy_ready indicates this).

 We then reset the Rx0 fifo. We then wait until the 48 to 8 bit converter is looking for the first byte of data from DDC Rx0,
 (convert_state indicates this). We then can enable writes to the Rx0 fifo again.  

 */

module Mux_clear ( 
				input clock,
				input Mux,
				input phy_ready,
				input convert_state,
				input fifo_empty,
				output reg fifo_write_enable,
				output reg fifo_clear
				);

reg [1:0]state;
reg [16:0]counter;
	
always @ (posedge clock)   
begin 
	case (state)
	0: begin 
			if (Mux) begin 	// if Mux is set flush the FIFO buffer
					fifo_write_enable <= 0;  // prevent writing to fifo input
					// wait for output side of fifo to empty
					if (phy_ready) begin 
						fifo_clear <= 1;	
						state <= 1;
					end 
			end
			else fifo_write_enable <= 1; 		// enable writing to fifo input
		end
// wait until the fifo is empty		
	1: begin 
			if (convert_state  && fifo_empty) begin 			// wait until 48 to 8 converter is in correct state. 
					fifo_write_enable <= 1; 
					fifo_clear <= 0;
					state <= 2;
			end 
		end 	
// wait for Mux to be released. 
	2: begin
			if (!Mux)
				state = 0;
		end 

	endcase
end

endmodule
