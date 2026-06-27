//-----------------------------------------------------------------------------
//                          High_Priority_CC.v
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
	
	When the code detects that the frame is for the High_Priority_CC port (1027) then the MSB
	of the sequence number is received.  We then read the next 3 bytes to complete the 
	sequence number.  Tt the end of the sequence number the byte_number is 3.
	
	Note that udp_rx_active will drop once the UDP packet has been received. In which case there is 
	no need to detect the end of packet. 
	
	
		 Byte Data
			0	Seq #	[31:24]
			1	Seq #	[23:16]
			2	Seq #	[15:8]
			3	Seq #	[7:0]
			4	Bits  [0] = run, [1] = PTT0…[4] = PTT3
			5	CWX0	[0] = CWX, [1] = Dot, [2] = Dash
			6	CWX1	[0] = CWX, [1] = Dot, [2] = Dash
			7	CWX2	[0] = CWX, [1] = Dot, [2] = Dash
			8	CWX3	[0] = CWX, [1] = Dot, [2] = Dash
			9	RX0 (Hz)/Mercury0 - Rx0	[31:24]
			10									[23:16]
			11									[15:8]
			12									[7:0]
			13	RX1/Mercury0 - Rx1		[31:24]
			14									[23:16]
			15									[15:8]
			16									[7:0]

        345	Tx0 Drive Level			0-255
	
	    1432	Alex 0						[31:24]
		 1433	Alex 0						[23:16]
		 1434	Alex 0						[15:8]
		 1435	Alex 0						[7:0]
		 
       1442	Step Attenuator 1 		(0 - 31dB)
       1443	Step Attenuator 0 		(0 - 31dB)
	
	

*/


module High_Priority_CC 
			( 	
				input clock,
				input [15:0] to_port,
				input udp_rx_active,
				input [7:0] udp_rx_data,
				input HW_timeout,
			   output reg run,
				output reg PC_PTT,
				output reg CWX,
				output reg Dot,
				output reg Dash,
				output reg [31:0]Rx_frequency[0:NR-1] /* ramstyle = "logic" */,		
				output reg [31:0]Tx0_frequency,
				output reg [31:0]Alex_data,
				output reg  [7:0]drive_level,
				output [4:0]Attenuator0,
				output [4:0]Attenuator1,
				output reg  [7:0]Open_Collector,
				output reg  [7:0]User_Outputs,
				output reg  [7:0]Mercury_Attenuator,				
				output reg Alex_data_ready,
				output  HW_reset
			);
			
parameter port = 16'd1027;	
parameter NR;

localparam 
				IDLE = 1'd0,
				PROCESS = 1'd1;
			
//reg [31:0] CC_sequence_number;
reg [10:0] byte_number;
reg state;


reg [31:0]temp_Rx_frequency[0:NR-1];
reg [31:0]temp_Alex_data;

// per NR number
genvar i;
generate
for (i=0; i<NR; i=i+1) begin : rxloop
	always @(posedge clock)
	begin
		if (udp_rx_active && to_port == port)	// look for to_port = 1027
			case (state)
				PROCESS:
				case (byte_number)
						i*4 + 9:	temp_Rx_frequency [i][31:24] <= udp_rx_data; 
						i*4 + 10:	temp_Rx_frequency [i][23:16] <= udp_rx_data;
						i*4 + 11:	temp_Rx_frequency [i][15:8]  <= udp_rx_data;
						i*4 + 12:	temp_Rx_frequency [i][7:0]   <= udp_rx_data;
						350:		Rx_frequency[i] <= temp_Rx_frequency[i];
		    		endcase
	    		endcase
	end
end
endgenerate

always @(posedge clock)
begin
  if(HW_timeout) run <= 1'b0; 							// reset run if HW timeout 
  
  if (udp_rx_active && to_port == port)				// look for to_port = 1027
    case (state)
		IDLE:
			begin
				byte_number <= 11'd1;
				Alex_data_ready <= 1'b0;
				//CC_sequence_number <= {CC_sequence_number[31-8:0], udp_rx_data};  //save MSB of sequence number
				state <= PROCESS;
			end 

		PROCESS:
			begin
				case (byte_number) 	//save balance of sequence number
				  1,2,3: begin
							Alex_data_ready <= 1'b0;
						//	HW_reset <= 1'b1;						
						//	CC_sequence_number <= {CC_sequence_number[31-8:0], udp_rx_data};
							end
						4: begin 
							run <= udp_rx_data[0];
							PC_PTT <= udp_rx_data[1];
						   end
						5: begin
							CWX  <= udp_rx_data[0];
							Dot  <= udp_rx_data[1]; 
							Dash <= udp_rx_data[2]; 
						   end

						 329:	Tx0_frequency [31:24]  <= udp_rx_data;
						 330:	Tx0_frequency [23:16]  <= udp_rx_data;					
						 331:	Tx0_frequency [15:8]   <= udp_rx_data;
						 332:	Tx0_frequency [7:0]    <= udp_rx_data;	

						 345: drive_level <= udp_rx_data;

						1401:	Open_Collector 		  <=  udp_rx_data;
						1402:	User_Outputs 			  <=  udp_rx_data;
						1403:	Mercury_Attenuator     <=  udp_rx_data;	
						
						1432:	temp_Alex_data [31:24]      <= udp_rx_data;
						1433:	temp_Alex_data [23:16]      <= udp_rx_data;					
						1434:	temp_Alex_data [15:8]       <= udp_rx_data;
						1435:	temp_Alex_data [7:0]        <= udp_rx_data;	
		
						1437:	Alex_data <= temp_Alex_data;
		
						1442: begin 
									Attenuator1 <= udp_rx_data[4:0];
									Alex_data_ready <= 1'b1;
								end 
						1443: begin 
									Attenuator0 <= udp_rx_data[4:0]; 
									Alex_data_ready <= 1'b0;
									//HW_reset <= 1'b0;
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
assign HW_reset = (byte_number > 4  && udp_rx_active);	
	
endmodule			
