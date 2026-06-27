// i2c_interface.v
//
// Copyright (C) 2013 Joe Martin K5SO
//                    k5so@valornet.command
//
// This module sequences commands to the i2c MASTER module which in turn controls the i2c bus

`define INIT 	7'd0
`define WAIT	7'd1
`define POLL 	7'd13

module i2c_interface (Tx_clock_2, I2C_clock, reset_n, Penny_version, sda, scl, probe, mic_boost, line_in, 
								line_gain, Penny_ALC, FWD, REV, Merc1_ver, Merc1_overload, Merc2_ver, Merc2_overload,
								Merc3_ver, Merc3_overload, Merc4_ver, Merc4_overload);

input wire Tx_clock_2;				// 12.5 MHz state machine clock
input wire I2C_clock;				// 800 KHz clock from which to derive the 200 KHz i2c bus clock
input reg reset_n;					// reset
output reg [7:0] Penny_version;
inout wire  sda;
output wire  scl;
output reg probe;
input reg mic_boost;
input reg line_in;
input [4:0] line_gain;
output reg [11:0] Penny_ALC;
output reg [11:0] FWD;
output reg [11:0] REV;
output reg [7:0] Merc1_ver;
output reg 		  Merc1_overload;
output reg [7:0] Merc2_ver;
output reg 		  Merc2_overload;
output reg [7:0] Merc3_ver;
output reg 		  Merc3_overload;
output reg [7:0] Merc4_ver;
output reg 		  Merc4_overload;

reg req_ena;
reg [6:0] slave_addr;
reg rw;
reg busy;
reg prev_busy;
reg [5:0] busy_cnt;
reg [15:0] data_wr;
reg [15:0] data_rd;
reg [7:0] state;
reg [7:0] return_to_state;
reg [4:0] init_cnt;
reg [15:0] TLV320_init[0:5];

parameter Merc1_addr				= 7'h10;
parameter Merc2_addr				= 7'h11;
parameter Merc3_addr				= 7'h12;
parameter Merc4_addr				= 7'h13;

parameter Penny_ver_addr 		= 7'h15;
parameter Penny_ALC_addr		= 7'h16;
parameter Penny_FWD_addr		= 7'h17;
parameter Penny_REV_addr		= 7'h18;
parameter Penny_TLV320_addr	= 7'h1b;

//setup parameters for the Penny TLV320
initial 
begin
TLV320_init[0] = 16'h1e00;		// chip reset
TLV320_init[1] = 16'h1201;		// set digitial interface active
TLV320_init[2] = 16'h0c00;		// all chip power ON
TLV320_init[3] = 16'h0e02;		// slave, 16 bit, I2C
TLV320_init[4] = 16'h1000;		// 48k, normal mode
TLV320_init[5] = 16'h0a00;		// turn D/A mute OFF
slave_addr = Penny_TLV320_addr;
init_cnt <= 8;
end


// state machine should run with a fast clock relative to the i2c clock to avoid conflicts
// when issuing sequencing commands to the i2c master controller module
//
always @ (posedge Tx_clock_2)
begin 
	case (state)												// state machine as an "i2c-master command sequencer"

		`INIT: begin											// initialize the TLV320 on Penny(Lane)
				rw <= 1'b0;										// i2c WRITE
				prev_busy <= busy;
				slave_addr <= Penny_TLV320_addr;
				data_wr <= TLV320_init[6 - init_cnt];
				init_cnt <= init_cnt - 1;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 2; 
				state <= `WAIT;
		end // 0:
		
		`WAIT: begin
				if (busy == !prev_busy) begin 			// if equal, command is not yet being executed by the i2c master
					req_ena <= 0;								// remove request
					state <= return_to_state;
					end
		end //1:
			
		2: begin
			if (!busy) begin									// when not busy, send next TLV320 command 
				if (init_cnt > 0) state <= state - 2;	// done sending that command, go send next TLV320 command
				else begin
					prev_busy <= 1'b0;						// reset for next command
					state <= state + 1;						// go send a new command
				end
			end																
		end //2:
		
//
// get firmware version numbers
//
//  retrieve Penny firmware version number
		3: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Penny_ver_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  
				state <= `WAIT;
		end // 3:
					
		4: begin
			if (!busy) begin									// when not busy, send next command 
				Penny_version <= data_rd[7:0];			// get version number from low byte 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end //4:

		//  get firmware version from Mercury 1		
		5: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc1_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		6: begin
			if (!busy) begin									// when not busy, send next command 
				Merc1_ver <= data_rd[7:0];					// get firmware version number
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  get firmware version from Mercury 2		
		7: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc2_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		8: begin
			if (!busy) begin									// when not busy, send next command 
				Merc2_overload <= !data_rd[8];			// get overload status (invert)
				Merc2_ver <= data_rd[7:0];					// get firmware version number
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  get firmware version from Mercury 3		
		9: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc3_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		10: begin
			if (!busy) begin									// when not busy, send next command 
				Merc3_ver <= data_rd[7:0];					// get firmware version number
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  get firmware version from Mercury 4		
		11: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc4_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		12: begin
			if (!busy) begin									// when not busy, send next command 
				Merc4_ver <= data_rd[7:0];					// get firmware version number
				prev_busy <= 1'b0;							// reset for next command
				state <= `POLL;								// go send a new command
			end																
		end 
//
// begin continuous polling loop	to update line-in/mic selection, line-in gain, mic gain, Penny power out, FWD, REV, and ADC overload
// status from four Mercury receiver boards
	`POLL: begin
			if (line_in) begin					// if line-in selected, send "set line-in & line-in gain" commands to TLV320
				rw <= 1'b0;							// i2c WRITE
				prev_busy <= busy;
				slave_addr <= Penny_TLV320_addr;
				data_wr <= 16'h0810;				// select line input
				req_ena <= 1'b1;					// request to "enable the i2c master" 
				return_to_state <= state + 1;
				state <= `WAIT; 
			end
			else state <= state + 4;			// line in not selected, go set mic input and mic gain instead
		end 

			
		14: begin
			if (!busy) begin									// when not busy, send next command 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end

//
// set line in gain
		15: begin
				prev_busy <= busy;
				data_wr <= {11'b0, line_gain};			// set line-in gain
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1; 
				state <= `WAIT;
		end 
			
		16: begin
			if (!busy) begin									// when not busy, send next command 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 3;							// go send a new command
			end																
		end 


// set mic as input and set mic gain
		17: begin											
				rw <= 1'b0;										// i2c WRITE
				prev_busy <= busy;
				slave_addr <= Penny_TLV320_addr;
				data_wr <= {15'b000010000001010, mic_boost};	// select mic input and set the mic gain
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1; 
				state <= `WAIT;
		end
					
		18: begin
			if (!busy) begin									// when not busy, send next command 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end

//  update Penny_ALC value
		19: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Penny_ALC_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;
				state <= `WAIT;
		end
					
		20: begin
			if (!busy) begin									// when not busy, send next command 
				Penny_ALC <= data_rd[11:0];				// get ALC vlaue 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;								// go send a new command
			end																
		end
		
		//  update FWD value from Penny
		21: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Penny_FWD_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;
				state <= `WAIT;
		end
					
		22: begin
			if (!busy) begin									// when not busy, send next command 
				FWD <= data_rd[11:0];						// get FWD vlaue 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end

		//  update REV value from Penny
		23: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Penny_REV_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end
					
		24: begin
			if (!busy) begin									// when not busy, send next command 
				REV <= data_rd[11:0];						// get REV vlaue 
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end

		//  update ADC_overload status from Mercury 1		
		25: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc1_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		26: begin
			if (!busy) begin									// when not busy, send next command 
				Merc1_overload <= !data_rd[8];			// get overload status (invert)
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  update ADC_overload status from Mercury 2		
		27: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc2_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		28: begin
			if (!busy) begin									// when not busy, send next command 
				Merc2_overload <= !data_rd[8];			// get overload status (invert)
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  update ADC_overload status from Mercury 3		
		29: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc3_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		30: begin
			if (!busy) begin									// when not busy, send next command 
				Merc3_overload <= !data_rd[8];			// get overload status (invert)
				prev_busy <= 1'b0;							// reset for next command
				state <= state + 1;							// go send a new command
			end																
		end 

		//  update ADC_overload status from Mercury 4		
		31: begin											
				rw <= 1'b1;										// i2c READ
				prev_busy <= busy;
				slave_addr <= Merc4_addr;
				req_ena <= 1'b1;								// request to "enable the i2c master" 
				return_to_state <= state + 1;  			
				state <= `WAIT;
		end 
					
		32: begin
			if (!busy) begin									// when not busy, send next command 
				Merc4_overload <= !data_rd[8];			// get overload status (invert)
				prev_busy <= 1'b0;							// reset for next command
				state <= `POLL;								// go send a new command
			end																
		end 
		
	endcase
end


i2c_master master_inst(.I2C_clock(I2C_clock), .reset_n(reset_n), .ena(req_ena), .addr(slave_addr), .rw(rw), .data_wr(data_wr),
									.busy(busy), .data_rd(data_rd), .ack_error(), .sda(sda), .scl(scl), .probe(probe));

									
endmodule
