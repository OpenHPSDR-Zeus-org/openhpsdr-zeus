//-----------------------------------------------------------------------------
//                          byte_to_32bits
//-----------------------------------------------------------------------------

//
//  HPSDR - High Performance Software Defined Radio
//
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


//  Copyright 2014  Phil Harman VK6PH

/*
	The maximum payload length in a UDP frame is 1444 bytes (without fragmentation).
	We use 4 bytes to hold the sequence number so 1440 bytes are avaialble for data.
	
	When the code detects that the frame is for the specified port then the MSB
	of the sequence number is received.  We then read the next 3 bytes to complete the 
	sequence number.  Hence at the end of the sequence number the byte_number is 2.
	
	We then accumulate 4 bytes to form a 32 bit value i.e. 16 bit + 16 bit sample.
	At the end of the accumulation the byte_number will be 6 in which case we need to save the sample 
	in the FIFO by setting fifo_wrreq true.  Only write to the fifo if not full.
	
*/

module byte_to_32bits
			( 	input clock,
				input run,
				input [15:0]to_port,
				input udp_rx_active,
				input [7:0] udp_rx_data,
				input full,
			   output reg fifo_wrreq,
				output reg [31:0] data_out,
				output reg sequence_error
			);

parameter [15:0] port; 	

localparam IDLE = 1'd0,
			  PROCESS = 1'd1;
			
reg [31:0] temp_Audio_sequence_number = 0;
reg [10:0] byte_number = 0;
reg [10:0] byte_counter = 0;
reg main = 0;


always @(posedge clock)
begin
case (main)
	IDLE:
	begin 
		if (udp_rx_active && run && to_port == port) begin
			temp_Audio_sequence_number[31:24] <= udp_rx_data;
			byte_counter <= 0;
			byte_number <= 0;
			main <= PROCESS;
		end
	end 
			
	PROCESS: 
	begin
	case (byte_number)
	  0: begin 
				temp_Audio_sequence_number[23:16] <= udp_rx_data;
				byte_number <= 1;
		  end
	  1: begin 
				temp_Audio_sequence_number[15:8] <= udp_rx_data;
				byte_number <= 2;
		  end
	  2: begin 
				temp_Audio_sequence_number[7:0] <= udp_rx_data;
				byte_number <= 3;
		  end	
	  3: begin
				fifo_wrreq <= 0;							// have sequence number so now save the I&Q data
				data_out[31:24] <= udp_rx_data;
				byte_number <= 4;
		  end	
	  4: begin 
				data_out[23:16] <= udp_rx_data;
				byte_number <= 5;
		  end
	  5: begin 
				data_out[15:8] <= udp_rx_data;
				byte_number <= 6;
		  end
	  6: begin 
				data_out[7:0] <= udp_rx_data;
				if(byte_counter == 64) begin	// MUST get 1440 bytes = 360 x 32 bit I&Q samples.  // ** was 360
					byte_counter <= 0;
					byte_number <= 0;
					if (!udp_rx_active)  		// only return to IDLE state when udp_rx_active has dropped
							main <= IDLE;
				end
				else begin
					if(!full) fifo_wrreq <= 1'b1;			// only write to the fifo if not full.
					byte_number <= 3;
					byte_counter <= byte_counter + 11'd1;
				end 
		  end
					  
	default:  byte_number <= 0;			
	endcase // byte_number	
	end 
	
endcase  // main  
  
 // if (byte_number == 11'd4) Audio_sequence_number <= temp_Audio_sequence_number;
 sequence_error <= 0;
  
end 

endmodule			