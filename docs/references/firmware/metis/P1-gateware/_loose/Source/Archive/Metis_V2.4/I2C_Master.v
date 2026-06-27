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


//  IC2 Master - Copyright 2009, 2010, 2011  Phil Harman VK6APH




// I2C Master for Metis to setup Penelope TLV320 

/*
								Address 												Command Byte 								Data 1 - repeat format for  Data 2
	   ----+   +-----+-----+-----+-----+-----+-----+-----+			 +-----+-----+-----+-----+-----+-----+-----+-----+     +-----+-----+-----+-----+-----+-----+-----+-----+           +----
	SDA    | S |  A6 |  A5 |  A4 |  A3 |  A2 |  A1 |  A0 |  W    ACK |  D7 |  D6 |  D5 |  D4 |  D3 |  D2 |  D1 |  D0 | ACK |  D7 |  D6 |  D5 |  D4 |  D3 |  D2 |  D1 |  D0 | ACK    P  |
		   +---+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+-----+
 
        -------+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+----
    SCL        +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  +--+  


High states for both SDA and SCL are actually high impedance. The ACK states are set to high impedance 
and assumed to be pulled low by the receiver

NOTE: This code does not fully implement an I2C transmitter in that:

	1. It does not check that the ACK bit is set low by the receiver; and 
	2. It does not allow the receiver to hold the clock low following the ACK. 

 
Set up for Penelope TLV320
The I2C settings are as follows: 

Common settings
 0x1b    0x1E 0x00 - Reset chip
 0x1b    0x12 0x01 - set digital interface active
 0x1b    0x0C 0x00 - All chip power on
 0x1b    0x0E 0x02 - Slave, 16 bit, I2S
 0x1b    0x10 0x00 - 48k, Normal mode
 0x1b    0x0A 0x00 - turn D/A mute off
 0x1b    0x00 0x00 - set Line in gain to 0

For mic input and boost on/off
 0x1b    0x08 0x14/15 - D/A on, mic input, mic 20dB boost on/off

For line input                           
 0x1b    0x08 0x10 - D/A on, line input

 
 
 Updates:
		2011 March  2   - added mic or line in selection
		
		2012 Sept  9    - added line-in gain control (line_boost) - Joe K5SO



*/


module I2C_Master (clock, reset, SCL, SDA, mic_gain, line_in, line_boost);

input clock;							// 800kHz clock from PLL 
input reset;
input mic_gain;						// 0 for normal and 1 for 20dB boost
input line_in;							// 0 for mic and 1 for line-in
input reg [4:0] line_boost;		// line_in gain setting
output SCL;
output SDA;

reg SCL_I;
reg SDA_I;

wire [6:0]address = 7'h1b;				// Address of TLV320 on Penny, fixed.
reg [15:0]data[0:7];
reg [3:0] setup = 4'h0;					// selects which data word to send
reg previous_mic_gain;
reg previous_line_in;
reg [4:0]previous_line_boost;


initial begin 
SCL_I = 1'b1;
SDA_I = 1'b1;
// set common settings
data[0] = 16'h1E00;
data[1] = 16'h1201;
data[2] = 16'h0C00;
data[3] = 16'h0E02;
data[4] = 16'h1000;
data[5] = 16'h0A00;
data[6] = 16'h0000;						// set line in gain to min 
data[7] = 16'h0000;						
end 


// state machine to provide I2C Master

reg [4:0] shift;
reg [5:0] state = 0;

always @ (posedge clock)
begin 
	case (state)
	0: begin 
		if (reset) state <= 0;										// loop here until reset is released 
		else begin
			if (line_in) begin
				data[6] <= {11'b00000000000, line_boost};		// set line-in gain
				data[7] <= 16'h0810;									// select line input	
				end
			else 
				data[7] <= {15'b000010000001010, mic_gain};	// select mic input and set the mic gain
			SDA_I <= 0;
			shift <= 7;
			state <= state + 1'b1;
		end 
	   end 
	   
	1: begin
	    SCL_I <= 0;
		if (shift != 0) begin
			SDA_I <= address[shift - 1'b1];
			shift <= shift - 1'b1;
	        state <= state + 1'b1;
	    end 
	    else begin
			SDA_I <= 0; 				// Write command 
			state <= 3;
	    end
	   end
	   
	2: begin 
		SCL_I <= 1'b1;
		state <= 1;
	   end 
	   
	3: begin							
		SCL_I <= 1'b1;
		state <= state + 1'b1;
	   end
	   
	4: begin
		SDA_I <= 1'b1;						// ACK so set SDA high Z
		SCL_I <= 0;
		state <= state + 1'b1;
	   end
	   
	5: begin
		SCL_I <= 1'b1;				
		shift <= 16;
		state <= state + 1'b1;
	   end
	// now send first data byte
	6: begin
		SCL_I <= 0;
		if (shift != 8) begin
			SDA_I <= data[setup][shift - 1'b1];
			shift <= shift - 1'b1;
	        state <= state + 1'b1;
	    end 
	    else begin
			SDA_I <= 1'b1; 				// ACK so set SDA high Z
			state <= 8;
	    end
	   end 
	   
	7: begin
		SCL_I <= 1'b1;
		state <= 6;
	   end 
	   
	8: begin 
		SCL_I <= 1'b1;
		state <= state + 1'b1;
	   end 
	// now send second data byte  
	9: begin
		SCL_I <= 0;
		if (shift != 0) begin
			SDA_I <= data[setup][shift - 1'b1];
			shift <= shift - 1'b1;
	        state <= state + 1'b1;
	    end 
	    else begin
			SDA_I <= 1'b1; 				// ACK so set SDA high Z 
			state <= 11;
	    end
	   end
	   
   10: begin 
	   	SCL_I <= 1'b1;
		state <= 9;
	   end 
	   
   11: begin
		SCL_I <= 1'b1;
		state <= state + 1'b1;
	   end 
	   
   12: begin 
		SCL_I <= 0;
		state <= state + 1'b1;
	   end 
	   
   13: begin 
		SCL_I <= 1'b1;				// P 
		state <= state + 1'b1;
		setup <= setup + 1'b1;
		previous_mic_gain <= mic_gain;
		previous_line_in <= line_in;
		previous_line_boost <= line_boost;
	   end 
	 
   14: begin 
	    if (setup == 8)begin	// all I2C data has been sent to TLV320.
			if (mic_gain != previous_mic_gain || line_in != previous_line_in || line_boost != previous_line_boost) begin
				setup <= 6;		// mic gain or input has changed, send just new settings
				state <= 0;		// loop back to start
			end 
			else state <= 14;	// loop here waiting for mic gain or input to change
		end
		else state <= 0; 		// more data to send so send it		
	   end
	default: state <= 0; 				
   endcase
end

// set outputs high impedance when data high 
assign  SDA = (SDA_I == 1'b1) ? 1'bz : 1'b0;
assign  SCL = (SCL_I == 1'b1) ? 1'bz : 1'b0;

endmodule




