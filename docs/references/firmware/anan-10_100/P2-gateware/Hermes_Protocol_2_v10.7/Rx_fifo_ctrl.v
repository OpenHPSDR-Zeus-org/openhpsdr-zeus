//-----------------------------------------------------------------------------
//                          Rx_fifo_ctrl.v
//-----------------------------------------------------------------------------

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


//  copyright 2010, 2011, 2012, 2013, 2014, 2015 Phil Harman VK6PH


/* Convert 48 bits to 8 for new Ethernet protocol
   NOTE:  At power on the FIFO will fill since no data is being requested by the PC.
   In which case need to check that the fifo is full and if so clear it. 

	The module works as follows:  When spd_rdy (Rx FIFO has data) then the I&Q data from 
	the receiver specified by Rx_number is sent to the PHY.
	
	The module then checks to see if data from another receiver(s) is required for synchronus or
	multiplex requirements.  This is done by checking the bits in Sync.  A set bit indicates 
	that the I&Q data relating to the position of the bit needs to be sent e.g.
	
		bit[0] = 1 sends Rx0 data
		bit[1] = 1 sends Rx1 data
		bit[2] = 1 sends Rx2 data etc
		
	If no bits are set then the code loops to the start.



*/

			

module Rx_fifo_ctrl(
	input clock,
	input reset,
	input [23:0] Sync_data_in_Q,
	input [23:0] Sync_data_in_I,			// Synchronus Rx data from selected receiver 
	input spd_rdy,
	input fifo_full,
	input [7:0]Sync,							// set if Sync active
	
	output reg wrenable,
	output reg [7:0] data_out,
	output reg fifo_clear
	);
	
parameter NR;
	
reg [3:0]state;
	
always @ (posedge clock)
begin 


if (reset) wrenable <= 0;	

else begin 
	case(state)
	
	0: begin
		fifo_clear <= 1'b0;
		state <= 1;
		end 
	
	1:	begin
			if (fifo_full) state <= 10; 		// clear fifo, will need to do this if code has been idle
			else if(spd_rdy) begin 													
				wrenable <= 1'b1;
				data_out <= Sync_data_in_I[23:16];
				state <= 2;
			end
		end 
		
	2:	begin
		data_out <= Sync_data_in_I[15:8];
		state <= 3;
		end		
		
	3:	begin
		data_out <= Sync_data_in_I[7:0];
		state <= 4;
		end
		
	4:	begin
		data_out <= Sync_data_in_Q[23:16];
		state <= 5;
		end

	5:	begin
		data_out <= Sync_data_in_Q[15:8];
		state <= 6;
		end	
		
	6:	begin
		data_out <= Sync_data_in_Q[7:0];
		state <= 7;
		end	
		
	// base receiver 	data sent so stop sending to FIFO until we see if sync or mux data required.
	7: begin 
		wrenable <= 0;
		state <= 9;  						// no other Receiver data to send so return
		end 

		
	9:	if (!spd_rdy) state <= 0;	// wait for spd_rdy to drop then continue
 
		
  10: begin
		fifo_clear <= 1'b1;
		state <= 0;
		end

	default: state <= 0;
	endcase
	end	
end

	
endmodule

