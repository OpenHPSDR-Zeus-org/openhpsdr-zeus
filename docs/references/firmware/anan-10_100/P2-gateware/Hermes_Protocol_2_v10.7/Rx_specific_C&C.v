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
	
	When the code detects that the frame is for the C&C port (1027) then the MSB
	of the sequence number is received.  We then read the next 3 bytes to complete the 
	sequence number.  Hence at the end of the sequence number the byte_number is 3.
	
	Note that udp_rx_active will drop once the UDP packet has been received. 
	
			Byte  Data
			
			0	Seq #	[31:24]
			1	Seq #	[23:16]
			2	Seq #	[15:8]
			3	Seq #	[7:0]
			4	Number of ADCs, max of 8 ADCs 
			5	Bits - Dither ADC0…7		[0] = ADC0, [1] = ADC1….[7] = ADC7
			6	Bits - Random ADC0..7	[0] = ADC0, [1] = ADC1….[7] = ADC7
			7	Rx Enable Rx0….Rx7		[0] = Rx0,  [1] = Rx1…….[7] = Rx7
			8	Rx Enable Rx8….Rx15	
			9	Rx Enable Rx16….Rx23	
			10	Rx Enable Rx24….Rx31	
			11	Rx Enable Rx32….Rx39	
			12	Rx Enable Rx40….Rx47	
			13	Rx Enable Rx48….Rx55	
			14	Rx Enable Rx56….Rx63	
			15	Rx Enable Rx64….Rx71	
			16	Rx Enable Rx72….Rx79		[0] = Rx72……..[7] = Rx79
			17	ADC Rx0/Mercury0			ADC(n)  that Rx0 is allocated to 
			18	Sampling Rate Rx0			[15:8]  48/96/192/384….
			19	Sampling Rate Rx			[7:0]
			20	CIC1 Rx0	For Future use 
			21	CIC2 Rx0	For Future use 
			22	Sample Size Rx0			Default 24 bits
			23	ADC Rx1/Mercury0			ADC(n)  that Rx1 is allocated to.
			24	Sampling Rate Rx1	
			25	Sampling Rate Rx	
			26	CIC1 Rx1	
			27	CIC2 Rx1	
			28	Sample Size Rx1			Default 24 bits
			29	ADC Rx2/Mercury0			ADC(n)  that Rx2 is allocated to.
			30	Sampling Rate Rx2	
			31	Sampling Rate Rx	
			32	CIC1 Rx2	
			33	CIC2 Rx2	
			34	Sample Size Rx2			Default 24 bits

			-------------------------------------------------------------			
			
			492	[7:0]SyncRx[7]			If bit set then Rx(n) is synched or muxed to Rx7 
			493	[7:0]SyncRx[6]			If bit set then Rx(n) is synched or muxed to Rx6 
			494	[7:0]SyncRx[5]			If bit set then Rx(n) is synched or muxed to Rx5
			495   [7:0]SyncRx[4]			If bit set then Rx(n) is synched or muxed to Rx4	
			496	[7:0]SyncRx[3]			If bit set then Rx(n) is synched or muxed to Rx3 
			497	[7:0]SyncRx[2]			If bit set then Rx(n) is synched or muxed to Rx2 
			498	[7:0]SyncRx[1]			If bit set then Rx(n) is synched or muxed to Rx1
			499   [7:0]SyncRx[0]			If bit set then Rx(n) is synched or muxed to Rx0
			500   [7:0]Mux					If bit set then Rx(n) is in Multiplexed mode
			
			
			
			1433	SyncRx9	[7:0] If bit set then Rx(n) is muxed to Rx9
			1434	SyncRx8	[7:0] If bit set then Rx(n) is  muxed to Rx8
			1435	SyncRx7	[7:0] If bit set then Rx(n) is synched or muxed to Rx7
			1436	SyncRx6	[7:0] If bit set then Rx(n) is synched or muxed to Rx6
			1437	SyncRx5	[7:0] If bit set then Rx(n) is synched or muxed to Rx5
			1438	SyncRx4	[7:0] If bit set then Rx(n) is synched or muxed to Rx4
			1439	SyncRx3	[7:0] If bit set then Rx(n) is synched or muxed to Rx3
			1440	SyncRx2	[7:0] If bit set then Rx(n) is synched or muxed to Rx2
			1441	SyncRx1	[7:0] If bit set then Rx(n) is synched or muxed to Rx1
			1442	SyncRx0	[7:0] If bit set then Rx(n) is synched or muxed to Rx0
			1443	Mux	[7:0] If bit set then Rx(n) is in Multiplexed mode
			
			
			
				
*/				

module Rx_specific_CC 
			( 	
				input              clock,
				input       [15:0] to_port,
				input              udp_rx_active,
				input        [7:0] udp_rx_data,
				output  reg  [7:0] dither,
				output  reg  [7:0] random,
				output  reg  [7:0] EnableRx0_7,
				output  reg [15:0] RxSampleRate[0:NR-1],
				output  reg  [7:0] RxADC[0:NR-1],
				output  reg  [7:0] SyncRx[0:NR-1],
				output  reg  [7:0] Mux,
				output  reg  Rx_data_ready,
				output    HW_reset
			);
			
parameter port = 16'd1025;	
parameter NR;
			
localparam 
				IDLE = 1'd0,
				PROCESS = 1'd1;
			
reg [31:0] CC_sequence_number;
reg [10:0] byte_number;
integer j;

reg state;

			
always @(posedge clock)
begin
  if (udp_rx_active && to_port == port)				// look for to_port = 1025
    case (state)
      IDLE:	
				begin
				Rx_data_ready <= 1'b0;
			//	HW_reset <= 1'b1;
				byte_number <= 11'd1;    // since byte 0 is received here 
				CC_sequence_number <= {CC_sequence_number[31-8:0], udp_rx_data};  //save MSB of sequence number
				state <= PROCESS;
				end 
			
		PROCESS:
			begin
				case (byte_number) 	//save balance of sequence number
					1,2,3: begin
						CC_sequence_number 		<= {CC_sequence_number[31-8:0], udp_rx_data};
					//	HW_reset <= 1'b1;
					end
					// 4:	number of ADCs
					5: dither 						<= udp_rx_data;
					6: random			 			<= udp_rx_data;
					7: EnableRx0_7					<= udp_rx_data; 
				 endcase
						
					for ( j = 0; j < NR ; j = j + 1)
					begin 						
						case (byte_number)
						 (j*6) + 17 : RxADC[j] <= udp_rx_data; 
						 (j*6) + 18 : RxSampleRate[j][15:8] <= udp_rx_data;
						 (j*6) + 19 : RxSampleRate[j][7:0]  <= udp_rx_data;
						 j + 1363: SyncRx[j] <= udp_rx_data; 
						endcase
					end

				case (byte_number)
					 1441: Rx_data_ready <= 1'b1;
					 1443: begin 
								Mux							<= udp_rx_data;
								Rx_data_ready <= 1'b0;
							//	HW_reset <= 1'b0;
							 end

									  
			   default: if (byte_number > 11'd1443) state <= IDLE;  
			   endcase  
		  
				byte_number <= byte_number + 11'd1;
			end
		default: state <= IDLE;
		endcase 
	else state <= IDLE;	

end	

// inhibit HW_reset if Ethernet data stops 
assign HW_reset = (byte_number > 11'd4  && udp_rx_active);	
			
endmodule			
