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

 When moving to/from conventional to sync DDC mode for PureSignal or Diversity modes we first inhibit any further writes to the Rx0 fifo.
 We then need to ensure there is no Rx0 DDC data currently being sent (phy_ready indicates this).

 We then reset the Rx0 fifo. We then wait until the 48 to 8 bit converter is looking for the first byte of data from DDC Rx0,
 (convert_state indicates this). We then can enable writes to the Rx0 fifo again.  

 */

module Mux_clear ( 
				input reset,
				input clock,
				input Mux,
				input phy_ready,
				input convert_state,
				input fifo_empty,
				input [15:0] SampleRate,
				output reg fifo_write_enable,
				output reg fifo_clear,				// used to reset DDC0
				output reg fifo_clear1				// used to reset DDC1
				);

reg [1:0]state;
reg previous_mux = 0;
reg [15:0]previous_SampleRate;
reg [15:0] counter;
	
	
always @ (posedge clock)   
begin 
	case (state)
	0: begin 
	   counter <= 16'd2000; //RRK was 1000
			if (Mux != previous_mux  || SampleRate != previous_SampleRate) begin 	// if Mux or sampleRate changes state then continue
					fifo_write_enable <= 0;  	// prevent writing to fifo input
					// wait for output side of fifo to empty
					if (phy_ready) begin 
						fifo_clear <= 1;		// clear both DDC0 and DDC1 fifos 
						fifo_clear1 <= 1;
						state <= 1;
					end 
			end
			else begin  
					fifo_write_enable <= 1; 		// enable writing to fifo input
					fifo_clear <= 0;
					fifo_clear1 <= 0;
			end 
		end
// wait until the fifo is empty & converter is in correct state. 		
	1: begin 
			if (counter == 16'd0) begin // leave fifo_clear active for long enough for other modules to see it. 
				fifo_clear1 <= 0;
				if (convert_state /* && fifo_empty */) begin 			// wait until 48 to 8 converter is in correct state. 
						fifo_write_enable <= 1; 
						fifo_clear <= 0;
						state <= 2;
				end 
			end
			
			else counter <= counter - 16'd1;
		end 	
// set previous states and back to sart 
	2: begin
			previous_mux <= Mux; 	// save current mux mode
			previous_SampleRate <= SampleRate;
			state <= 0;
		end 

	endcase
end

endmodule
