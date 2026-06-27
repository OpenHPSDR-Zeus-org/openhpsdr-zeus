//
//  i2c_master.v
//
// Copyright (C) 2013 Joe Martin K5SO
// 						 k5so@valornet.com
//

// state machine definitions
`define READY  		7'd0
`define START  		7'd1
`define COMMAND 		7'd2
`define HIGH_BYTE 	7'd3
`define LOW_BYTE 		7'd4
`define PRE_STOP		7'd5
`define STOP			7'd6


module i2c_master (I2C_clock, reset_n, ena, addr, rw, data_wr, busy, data_rd, ack_error, sda, scl, probe); 

input wire	I2C_clock;		// 800 kHz clock
input reg	reset_n;			// Tx_reset
input reg	ena;				// enable, control bit
input reg	[6:0] addr;			// I2C address of target board to send/receive to/from
input reg	rw;				// WRITE (rw = 0) or READ (rw = 1)
input reg	[15:0] data_wr;			// data to write out to target board
output reg	busy;				// module status 0=available, 1=busy
output reg	[15:0] data_rd;	// latched in data from target to transfer to interface module
output reg	ack_error;			// flag if no ack from target
inout wire sda;						// i2c data line
output wire scl;						// i2c clock line
output wire probe;

// internal variables
reg [6:0] state;				// for state machine	
reg 	data_clk;				// clock edges for sda
reg scl_clk;					// constantly running scl
reg scl_ena;					// enables interal scl to output
reg sda_int;					// internal sda
reg sda_ena_n;					// enables internal sda to output
reg [7:0] addr_rw;			// latched in address and rw
reg [15:0] data_tx;			// latched in data to write to target
reg [15:0] data_rx;			// data received from target
reg [4:0] bit_cnt;			// bit counter
reg stretch;					// identifies if target is stretching scl

reg read;

//reg dg_sda;
//reg dg_scl;

initial 
begin
scl_ena = 1'b0;  
sda_int = 1'b1;
bit_cnt = 5'd7;
stretch = 1'b0;
count = 0;
state = `READY;
end

///////////////////////////////////////////////////////////////////////////
//
//  i2c clock generator: i2c bus clock speed is 1/4 of I2C_clock
//
reg [1:0] count;						// counter, range: 0 to 3

always @ (posedge I2C_clock) begin
  if (reset_n) begin			
	stretch <= 1'b0;					// high means clock stretching is requested from target
	count <= 0;
  end
  else begin
	//if (!stretch) count <= count + 2'b01;	// clock stretching request not detected, continue with clock generation
	count <= count + 1;

	case (count)
		0:	begin							// first 1/4 cycle of clocking
			scl_clk <= 1'bz;
			data_clk <= 1'b0;
		end 

		1: begin							// second 1/4 cycle of clocking
			scl_clk <= 1'b0;
			data_clk <= 1'b1;
		end
		
		2: begin							// third 1/4 cycle of clocking
			scl_clk <= 1'b0; //1'bz;			// release scl
			data_clk <= 1'b1;
		end

		3: begin							// last 1/4 cycle of clocking
			scl_clk <= 1'bz;			// release scl
			//if (dg_scl == 1'b0) stretch <= 1'b1; // detect if slave is reqquesting a stretched clock
			//else stretch <= 1'b0;
			data_clk <= 1'b0;
		end
				
	endcase
	end
end

//////////////////////////////////////////////////////////
// 
// i2c MASTER bus controller state machine
//	
always @ (posedge data_clk)
begin
	if (reset_n) begin										// reset asserted
		state <= `READY;										// return to initial state
		busy <= 1'b1;											// indicate "not available"
		scl_ena <= 1'b0;										// sets scl high impedance
		sda_int <= 1'b1; 										// sets sda high impedance
	end
	else begin
	
	case (state)
		`READY: begin											// idle state
				if (ena) begin									// transaction requested
					busy <= 1'b1;								// flag busy 
					addr_rw <= {addr, rw};					// collect slave adr and command
					data_tx <= data_wr;						// collect requested data to write
					bit_cnt <= 7;
					state <= `START;							// go to START
				end
				else begin										// remain idle
					busy <= 1'b0;								// unflag busy
				end
			end
			
		`START: begin											// start bit for transaction
				scl_ena <= 1'b1;								// enable scl output
				sda_int <= addr_rw[bit_cnt];				// send first address bit to bus ...i2c clock #1
				state <= `COMMAND;							// go to COMMAND
			end
			
		`COMMAND: begin										// address and command byte of transaction
				if (bit_cnt == 0) begin						// I2C clock #9 ... slave acknowledge bit
					sda_int <= 1'b1;							// release sda for slave acknowledge
					bit_cnt <= 16;								// set bit counter for two-byte transfers
					state <= `HIGH_BYTE; 					// go send/receive high byte bits
				end
				else begin
					bit_cnt <= bit_cnt - 1; 				// keep track of transaction bits
					sda_int <= addr_rw[bit_cnt - 1]; 	// send address/command bit to bus ...i2c clocks #2-#8
				end
			end			
							
		`HIGH_BYTE: begin										// read/write high byte
			if (bit_cnt == 8) begin							// i2c clock #18, ack position in stream
				if (rw) sda_int <= 1'b0;					// read, send MASTER ack
				else sda_int <= 1'b1;						// write, release sda for SLAVE ack
				state <= `LOW_BYTE;							// go send/receive low byte bits
			end
			else begin											// bits 15 - 8, clocks #9 - #17
				if (!rw) sda_int <= data_tx[bit_cnt - 1];	// write
				bit_cnt <= bit_cnt - 1;						// keep track of bits
			end
		end
				
		`LOW_BYTE: begin										// read/write low byte
			if (bit_cnt == 0) begin							// i2c clock #27, ack position in stream
				sda_int <= 1'b1;								// release data line for ack
				if (rw) begin 
					sda_int <= 1'b0;							// READ ack
				end
				else sda_int <= 1'b1;						// WRITE ack
				state <= `PRE_STOP;								
			end
			else begin											// bits 7 - 0, clocks #19 - #26
				if (rw) begin 
					sda_int = 1'b1;							// blocking assignment, release line for target to send
				end
				else sda_int <= data_tx[bit_cnt - 1];	// write
				bit_cnt <= bit_cnt - 1;						// keep track of bits
			end
		end
						
		`PRE_STOP: begin
			sda_int <= 1'b1;									// send i2c STOP
			state <= `STOP;
		end
		
		`STOP: begin
			busy <= 1'b0;										// unflag busy
			data_rd <= data_rx;								// send rcv'd data to interface module 
			scl_ena <= 1'b0;									// turn off data_clk control of the i2c clock line
			state <= `READY;									// go to ready state
		end
		
	endcase
  end // else
end // always


reg ack_flag; 


// when i2c READ, receive data bytes from target and put them into data_rx
reg [7:0] clk_count;

always @ (posedge scl)
begin
		if (scl_ena) begin	
			if (rw) begin
				if (clk_count == 27) begin
				clk_count <= 0;
				end
				else begin
					if (scl_ena) begin 
						if (clk_count >= 9 & clk_count <= 16) data_rx[24 - clk_count] <= sda; // high byte
						if (clk_count >= 18 & clk_count <= 25) data_rx[25 - clk_count] <= sda; // low byte
						clk_count <= clk_count + 1;
					end
				end //else
			end // if (rw)
		end // if (scl_ena)
end // always
	

/*
// read from sda when scl is high
always @ (negedge data_clk)
begin
	if (reset_n) ack_error <= 1'b0;						// reset asserted
	else begin
//		if(sda & (((state == `COMMAND | state == `WR2) & bit_cnt == 0) | (state == `WR1 & bit_cnt == 8))) ack_error <= 1'b1;
//		else if (!sda & (((state == `COMMAND | state == `WR2) & bit_cnt == 0) | (state == `WR1 & bit_cnt == 8))) ack_error <= 1'b0;
		case (state)
			`START: ack_error <= 1'b0;						// starting new transaction
//			`RD1: begin
//				if (bit_cnt == 7) ack_flag <= 1'b1;
//				else ack_flag <= 1'b0;
//				end
			//`SLV_ACK1: ack_error <= (sda | ack_error);// set error output if ack error
			//`RD1: data_rx[bit_cnt] <= sda;					// receive current slave data bit
			//`SLV_ACK2: ack_error <= (sda | ack_error);// set error output if ack error
			//`SLV_ACK3: ack_error <= 1'b0; //...jam
			//`RD2: data_rx[bit_cnt] <= sda;
		endcase
		
  end  // else
end // always
*/
		
// set sda output
always @ (posedge I2C_clock)
begin
			if (state == `START) sda_ena_n <= data_clk; // generate i2c bus START
			if (state == `STOP)  sda_ena_n <= !data_clk; // generate i2c bus STOP
			if (state != `START & state != `STOP) sda_ena_n <= sda_int; // set to internal sda signal
end
			
//   set scl and sda outputs
assign scl = (scl_ena) ? scl_clk : 1'bz;
assign sda = (!sda_ena_n) ? 1'b0 : 1'bz; 

endmodule

			



