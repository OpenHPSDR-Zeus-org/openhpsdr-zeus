// Apollo  SPI interface - 
// (C) 2011 Phil Harman VK6APH, Dave McQuate WA8YWQ
// (C) 2013 Espen S. Johnsen LA7DJA

//
//  HPSDR - High Performance Software Defined Radio
//
//  Hermes code. 
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


// This module provides the communication between Hermes and Apollo
// Dave McQuate  WA8YWQ
// January 4, 2011

// 4 July 2012 - sync byte changed from 0x80 to 0x88 by Espen to match previous USB version
// 6 Jan  2013 - modified PTT timer code. Removed ATU commands since not yet implemented in Apollo uP code. Phil Harman VK6APH
//             - removed read of Apollo status since not yet implemented in Apollo uP code. VK6APH 
// 16 May 2013 - complete rewrite. LA7DJA


module Apollo (enable, reset, clock, frequency, PTT, tune, 
							 FilterEnabled, TunerEnabled, 
 							 SS, SCK, MISO, MOSI, STATUS, RESET);
	 input enable;			         // Apollo selected when high
	 input reset;
	 input clock;					       // 30 kHz
	 input [31:0] frequency;
	 input 				PTT;					 // enable PA bias
	 input 				tune;				   // start tuning cycle when asserted,
															 // cancel when deasserted
	 input 				FilterEnabled; // enable filter bank
	 input 				TunerEnabled;	 // enable antenna tuner

	 /* Apollo IO lines */
	 output 			SS;     // Apollo SPI bus enabled when high
	 input 				MISO;		// receive serial data from Apollo
	 output 			MOSI;		// send serial data, MOSI,  to Apollo
	 output 			SCK;		// serial data clock, SCLK, nominally 15 kHz
	 input 				STATUS;	// Apollo sets this line high when ATU status
												// has changed. Deasserted when a command to
												// read status is received from Hermes
	 output 			RESET;	// reset Apollo uC (active low)
	 reg 					RESET;
	 
	 parameter ClockFrequency = 30000;
	 parameter [23:0] ptt_timeout = 500; // ms

	 localparam [15:0] ptt_ticks = (ClockFrequency * ptt_timeout / 1000);

	 reg [31:0]  last_freq_msg; // Last frequency update
	 reg 				 last_tune;
	 reg  [4:0]  state;
	 reg  [4:0]  next_state;
	 reg [31:0]  last_status; // How do we route this to client programs? 
	 
	 reg  [31:0] message;
	 reg 				 send_msg;
	 wire [31:0] response;
	 wire 			 got_response;

 	 reg [15:0]  ptt_counter;
	 
 					 

	 /* Apollo message opcodes */
	 localparam
		 MSG_SET_FREQ     = 8'd1, // and and also filter/tuner enable/disable
		 MSG_ENABLE_PTT   = 8'd2,
		 MSG_DISABLE_PTT  = 8'd3,
		 MSG_START_TUNING = 8'd4,
		 MSG_ABORT_TUNING = 8'd5,
		 MSG_GET_STATUS   = 8'd6, // get ATU status
		 MSG_GET_VERSION  = 8'd7;	 
		 
	 localparam
		 STATE_RESET          = 5'd0,
		 STATE_INIT           = 5'd1,
		 STATE_IDLE           = 5'd2,
		 STATE_SEND_MSG       = 5'd3,
		 STATE_WAIT_MSG       = 5'd4,
		 STATE_FREQ_SET       = 5'd5,
		 STATE_PTT_DISABLED   = 5'd6,
		 STATE_PTT_ENABLED    = 5'd7,
		 STATE_TUNING_STARTED = 5'd8,
		 STATE_TUNING_ABORTED = 5'd9,
		 STATE_GET_STATUS     = 5'd10;

 
`define MSG_SET_FREQ { MSG_SET_FREQ, FilterEnabled, TunerEnabled, frequency[25:4] }
	

	 
			
// Nominal clock frequency is 30 kHz.  PTT timeout is in milliseconds.
// As long as PTT continues to be high, we need to send additional 
// enable PTT message before Apollo's PTT timeout expires.
// These enable PTT message should be sent before the timeout expires,
// but not too frequently. 			

	 ApolloSPI ApolloSPI_inst (.enable(enable), .reset(reset), .clk(clock), 
														 .data_out(message), .data_out_flag(send_msg), 
														 .data_in(response), .data_in_flag(got_response),
														 .MISO(MISO), .MOSI(MOSI), .SCK(SCK), .SS(SS));

	 always @ (posedge clock) begin
			if (reset || !enable)
				begin
					 RESET <= 1'b0;
					 last_freq_msg <= 0;
					 ptt_counter <= 0;
					 last_tune <= 0;					 
					 send_msg <= 0;					 
					 state <= STATE_RESET;
				end			
			else
				begin
				 if (ptt_counter != 0) ptt_counter <= ptt_counter - 1;
				 case (state)
					 STATE_RESET: begin
			 				RESET <= 1'b0;
							// Reset pulse will be at least 33us which is more than  the required 400ns 
							state <= STATE_INIT;
					 end
					 STATE_INIT: begin
							RESET <= 1'b1;
							/* Wait for Apollo to become ready */
							if (STATUS == 1'b1) state <= STATE_IDLE;
							// TODO: Try new reset if Apollo doesn't respond within
							// reasonable time
					 end
					 STATE_IDLE: begin
							if (STATUS == 1'b1)
								begin
									 message <= { MSG_GET_STATUS, 24'd0 };									 
									 next_state <= STATE_GET_STATUS;
									 state <= STATE_SEND_MSG;
								end
							else if (!PTT && ptt_counter != 0)
								begin
									 message <= { MSG_DISABLE_PTT, 24'd0 };
									 next_state <= STATE_PTT_DISABLED;							 
									 state <= STATE_SEND_MSG;
								end
							else if (`MSG_SET_FREQ != last_freq_msg)
								begin
									 message <= `MSG_SET_FREQ;
									 next_state <= STATE_FREQ_SET;
									 state <= STATE_SEND_MSG;
								end
							else if (PTT && ptt_counter < ptt_ticks >> 1)
								begin
									 message <= { MSG_ENABLE_PTT, ptt_timeout };
									 next_state <= STATE_PTT_ENABLED;
									 state <= STATE_SEND_MSG;									 
								end
							else if (tune && tune != last_tune)
								begin
									 message <= { MSG_START_TUNING, 24'd0 };
									 next_state <= STATE_TUNING_STARTED;
									 state <= STATE_SEND_MSG;									 									 
								end
							else if (!tune && tune != last_tune)
								begin
									 message <= { MSG_ABORT_TUNING, 24'd0 };
									 next_state <= STATE_TUNING_ABORTED;
									 state <= STATE_SEND_MSG;									 									 
								end							
					 end					 
					 STATE_SEND_MSG: begin
							send_msg <= 1'b1;
							state <= STATE_WAIT_MSG;
					 end
					 STATE_WAIT_MSG: begin
							send_msg <= 1'b0;
							if (got_response == 1'b1) state <= next_state;
					 end
					 STATE_FREQ_SET: begin
							last_freq_msg <= response;
							state <= STATE_IDLE;
					 end					 
					 STATE_PTT_ENABLED: begin
							if (response == message) ptt_counter <= ptt_ticks;							
							state <= STATE_IDLE;
					 end
					 STATE_PTT_DISABLED: begin
							if (response == message) ptt_counter <= 0;
							state <= STATE_IDLE;
					 end
					 STATE_TUNING_STARTED: begin
							if (response == message) last_tune <= 1;
							state <= STATE_IDLE;
					 end					 
					 STATE_TUNING_ABORTED: begin
							if (response == message) last_tune <= 0;
							state <= STATE_IDLE;
					 end
					 STATE_GET_STATUS: begin
							last_status <= message;							
							state <= STATE_IDLE;							
					 end
				 endcase
			end
	 end
endmodule


/* This module can also be used for ISP programming if a parameter is
   added to enable/disable the SS toggle between each byte) */
module ApolloSPI (enable, reset, clk, data_out, data_out_flag, 
									data_in, data_in_flag,  MISO, MOSI, SCK, SS);
	 input        enable;
	 input        reset;
	 input 	      clk;	// 30 kHz
	 input [31:0] data_out;
	 input 				data_out_flag; // load data into shift register and
															 // start spi transfer
	 output [31:0] data_in;
	 output 			 data_in_flag;	 
	 
	 input 				MISO;
	 output 			MOSI;
	 output 			SCK;
	 output 			SS; 			

	 reg [31:0] 	data_in;
	 reg 					data_in_flag;
	 
	 reg [3:0] 		state;
	 reg [6:0] 		count;
	 reg [4:0] 		delay;	 
	 reg [31:0] 	shift;
	 reg 					MOSI;
	 reg 					SCK;
	 reg 					SS; 		
	 reg 					RESET;

	 localparam	
		 STATE_RESET  = 3'd0,
		 STATE_IDLE   = 3'd1,
		 STATE_SET    = 3'd2,
		 STATE_SAMPLE = 3'd3,		
		 STATE_END    = 3'd4,
		 STATE_DELAY  = 3'd5;
	 

	 	 
	 always @ (posedge clk) begin
			if (reset || !enable) 
				state <= STATE_RESET;
			else
				case (state)
					STATE_RESET: begin
						 SCK <= 1'b0;
						 SS <= 1'b1;
						 MOSI <= 1'b1;						 
						 state <= STATE_IDLE;
					end						
					STATE_IDLE: begin
						 data_in_flag <= 1'b0;
						 if (data_out_flag) begin
								shift <= data_out;							
								count <= 6'd40;
								delay <= 0; 
								state <= STATE_DELAY;
						 end
					end
					STATE_SET: begin
						 SCK <= 1'b0;						 
						 MOSI <= shift[31];
						 SS <= 1'b0;
						 count <= count - 1;
						 state <= STATE_SAMPLE;
					end
					STATE_SAMPLE:	begin
						 SCK <= 1'b1;					 
						 shift <= {shift[30:0], ~MISO};
						 if (count == 0)
							 state <= STATE_END;
						 else if (count[2:0] == 0)
							 begin
									state <= STATE_DELAY;
							 end
						 else
							 state <= STATE_SET;
					end
					STATE_END: begin
						 data_in <= shift;
						 data_in_flag <= 1'b1;						 
						 SCK <= 1'b0;						 
						 SS <= 1'b1;
						 state <= STATE_IDLE;
					end
					STATE_DELAY: begin
						 // The AVR SPI bus has a peculiar and not clearly
						 // documented behavior in slave mode. One have to write
						 // the next outgoing byte to the shift regiser, before
						 // reading the last received.  This makes it impossible
						 // to read, modify and then immediately write back
						 // data. To overcome this problem, we have to toggle SS
						 // after each octet 
						 SCK <= 1'b0;						 
						 SS <= 1'b1;

						 // Delay to give Apollo some time to process received bytes
						 delay <= delay + 1;
						 if (delay == 5'd31) state <= STATE_SET;
					end				 
				endcase
	 end
endmodule
