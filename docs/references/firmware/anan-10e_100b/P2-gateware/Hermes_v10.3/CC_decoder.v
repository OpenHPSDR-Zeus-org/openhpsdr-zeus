//-----------------------------------------------------------------------------
//                          CC_decoder.v
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


//  Copyright 2010, 2011, 2012, 2013, 2014  Phil Harman VK6(A)PH

// ***** need to check sequence error 

/*
	The maximum payload length in a UDP frame is 1444 bytes (without fragmentation).
	We use 4 bytes to hold the sequency number so 1440 bytes are avaialble for data.
	In which case we count the frame from 0 to 1443.
	
	When the code detects that the frame is for the C&C port (1033) then the MSB
	of the sequence number is received.  We then read the next 3 bytes to complete the 
	sequence number.  Hence at the end of the sequence number the byte_number is 2.
	
	Note that udp_rx_active will drop once the UDP packet has been received. In which case there is 
	no need to detect the end of packet. 

	****  Temp test code for C&C decoder to port 1034 *****
	
			The format is as follows:

			Byte 0 - 	Bit [0] 	 - run 1 = true, 0 = false
							Bit [1] 	 - wideband 1 = on, 0 = off
							Bit [2] 	 - PC_PTT  1 = active, 0 = inactive
							Bit [4:3] - ADC0 Sample rate 00 = 48k, 01 = 96k, 10 = 192k, 11 = 384k
							bit [7]   - PureSignal 1= on, 0 = off

			Byte  1 -   Bit [1:0] - ADC1 sample rate 00 = 48k, 01 = 96k, 10 = 192k, 11 = 384k
							Bit [5]   - Attenuator 0 = 0dB, 1 = 20dB attenuation
							Bit [6]   - Mic Boost 20db  0 = off, 1 = on
							Bit [7]   - Sync Rx1 and Rx2 if set. Uses Rx1 frequency and sample rate							
							
			Bytes 2-5	Rx1_Frequency 
			Bytes 6-9   Rx2_Frequency 
			Bytes 9-12  Tx1_Frequency 
			Bytes 13-16 Bits [31:0] - Alex data
			Byte  17    Drive 
	

*/


module CC_decoder 
			( 	input clock,
				input [15:0] to_port,
				input udp_rx_active,
				input [7:0] udp_rx_data,
			   output reg run,
				output reg wideband,
				output reg PC_PTT,
				output reg [1:0] sample_rate1,
				output reg [1:0] sample_rate2,
				output reg Attenuator,
				output reg [31:0]Rx1_frequency,
				output reg [31:0]Rx2_frequency,
				output reg [31:0]Tx1_frequency,
				output reg [31:0]Alex_data,
				output reg Mic_boost,
				output reg Sync,
				output reg RX_RAND,
				output reg RX_DITHER,
				output reg [7:0]drive_level,
				output reg PureSignal,
				output reg Alex_data_ready
			);
			
localparam 
				IDLE = 1'd0,
				PROCESS = 1'd1;
			
reg [31:0] CC_sequence_number;
reg [10:0] byte_number;
reg [31:0] temp_Rx1_frequency;
reg [31:0] temp_Rx2_frequency;
reg [31:0] temp_Tx1_frequency;

reg state;

			
always @(posedge clock)
begin
  if (udp_rx_active && to_port == 16'd1034)				// look for to_port = 1034
    case (state)
      IDLE:	
				begin
				byte_number <= 11'd0;
				Alex_data_ready <= 1'b0;
				CC_sequence_number <= {CC_sequence_number[31-8:0], udp_rx_data};  //save MSB of sequence number
				state <= PROCESS;
				end 
			
		PROCESS:
			begin
				case (byte_number) 	//save balance of sequence number
				  0,1,2: CC_sequence_number <= {CC_sequence_number[31-8:0], udp_rx_data};
				      3: begin 
							run <= udp_rx_data[0];
							wideband <= udp_rx_data[1];
							PC_PTT <= udp_rx_data[2];
							sample_rate1 <= udp_rx_data[4:3];      // 0 = 48k, 1 = 96k, 2 = 192k, 3 = 384k
							PureSignal <= udp_rx_data[7];	
							end
						4: begin 
							sample_rate2 <= udp_rx_data[1:0];      // 0 = 48k, 1 = 96k, 2 = 192k, 3 = 384k
							Attenuator <= udp_rx_data[5]; 
							Mic_boost <= udp_rx_data[6];
							Sync <= udp_rx_data[7];
							end 
				5,6,7,8:	temp_Rx1_frequency <= {temp_Rx1_frequency[31-8:0], udp_rx_data}; 
			9,10,11,12:	temp_Rx2_frequency <= {temp_Rx2_frequency[31-8:0], udp_rx_data}; 
	     13,14,15,16:	temp_Tx1_frequency <= {temp_Tx1_frequency[31-8:0], udp_rx_data};
		  17,18,19,20: Alex_data <= {Alex_data[31-8:0], udp_rx_data};
		           21: drive_level <= udp_rx_data;
					  22: begin 
							Rx1_frequency <= temp_Rx1_frequency;		// latch frequency when ready so no transients
							Rx2_frequency <= temp_Rx2_frequency;							
							Tx1_frequency <= temp_Tx1_frequency;
							end 
								
					  30: Alex_data_ready <= 1'b1;
									  
			   default: if (byte_number > 11'd1442) state <= IDLE;  // don't need since will auto stop when active drops ?
			   endcase  
		  
				byte_number <= byte_number + 11'd1;
			end
		default: state <= IDLE;
		endcase 
	else state <= IDLE;	

end		
			
endmodule			