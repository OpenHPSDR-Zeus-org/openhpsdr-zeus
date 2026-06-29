/////////////////////////////////////////////////////////////////////
////                                                             ////
//// Copyright (C) 2006 Steven Wilson                            ////
////                    ka6s@yahoo.com                           ////
//// Copyright (C) 2013 Joe Martin K5SO								  ////
//// 							k5so@valornet.com								  ////
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published
//  by the Free Software Foundation; version 1 
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//
// This design is LGPL software written for the HPSDR project
// by Steve Wilson, KA6S.  The author assumes no libability on how the
// code might be used or warranty that the code is fit for any 
// particular application. Further, I2C is a patented technology 
// of Philips corporation and requires a license. If you are going
// to use this in a product - get the license!
//
// The purpose of the code is to act as an    
// I2C slave. Note that there is NO provision for dealing with
// the 50ns glitch requirements in I2C provided within this code.
// Consequently - use a CLEAN line or implement external filters, as done
// in the i2c.v module that interfaces with this module.
//

`define SM_IDLE   3'b000
`define SM_ADDR   3'b001
`define SM_WRITE  3'b010
`define SM_READ   3'b011
`define SM_WAIT   3'b100

module i2c_slave (
	          		rst_n,
                  slave_addr,
						address,
	          		sda_i, 
						sda_out, 
						sda_oe, 
						scl_in,
                  int_reg0,
						int_reg1,
						rcv_reg0,
						rcv_reg1,
                  probe 
);

input             rst_n;
output wire [6:0] slave_addr;
input 		[6:0] address;
input             sda_i;     // Serial Data In
output            sda_out;   // Serial Data out
output            sda_oe;    // Serial Data Out Enable
input             scl_in;    // Serial Clock In
output 		[7:0] int_reg0;  // First write cycle
output 		[7:0] int_reg1;  // Second Write cycl
input  		[7:0] rcv_reg0;  // First Read cycle
input  		[7:0] rcv_reg1;  // Second Read cycle
output reg			probe;

reg 					start;
reg 					stop;
reg 			[9:0] rcv_reg;
reg 			[3:0] rcv_cntr;
reg 			[2:0] i2c_sm;
reg 			[2:0] nxt_i2c_sm;
reg 					cyc_cnt;
reg      			target;
wire     			ack;
wire     			nack;
wire    				 int_sda;
reg  					sda_in;
wire     			read;
wire     			write;
wire 					addr_dec;

always @(sda_i)
  #1 sda_in = sda_i;

//
// Capture Start flag - SDA falls while SCL is high
wire start_clr = ~rst_n | (rcv_cntr == 4'h2);

always @(negedge sda_in or posedge start_clr)
  if(start_clr)
    start <= 1'b0;
  else
  if(scl_in == 1)
    start <= 1'b1;
//
// Capture Stop Flag - SDA rises while SCL is high
wire stop_clr = ~rst_n | ack | start;

always @(posedge sda_in or posedge stop_clr)
  if(stop_clr)
    stop <= 1'b0;
  else	    
  if(scl_in == 1)
    stop <= 1'b1;

// 
// RCV_CNTR - counts from 0 - 8 for each 8 bit packet + ACK/NACK
//
wire cnt8 = (rcv_cntr == 4'h8);
wire cnt7 = (rcv_cntr == 4'h7);
wire cntr_rst = ~rst_n;

always @(negedge scl_in or posedge cntr_rst)
  if(cntr_rst)
    rcv_cntr <= 4'b0;
  else
  if(cnt8 | start & ((i2c_sm == `SM_WRITE) | (i2c_sm == `SM_READ)) )  
    rcv_cntr <= 4'b0;
  else
  if(i2c_sm != `SM_IDLE)
    rcv_cntr <= rcv_cntr + 4'h1;	  

	 
// LAST BIT Counter - detects the last scl posedge in the 2-byte transmission from Mets/Ozy 
// and ensures a release of the data line so that an i2c STOP can occur

wire last_bit = (read & end_counter >= 18);
reg [5:0] end_counter; 

always @ (posedge scl_in or posedge cntr_rst) begin
	if (cntr_rst) begin 
		end_counter <= 0;
	end
	else begin
		if (target) begin
			end_counter <= end_counter + 1;
		end
		else end_counter <= 6'd0;
	end
end

			
//
// Receive shift register
//

always @(posedge scl_in or negedge rst_n)
begin 
	if(~rst_n) begin
		rcv_reg <= 10'b0;
	end
	else begin
	if(rcv_cntr <= 4'h7)
		rcv_reg <= {rcv_reg[8:0],sda_in};
	end
end
// Decode ACK/NACK
assign ack  = cnt8 & ~sda_in;
assign nack = cnt8 & sda_in;

// rcv_reg decode
assign slave_addr = (i2c_sm == `SM_ADDR) ? rcv_reg[7:1] : 7'b0;
wire       i2c_write = ~rcv_reg[0] & (i2c_sm == `SM_ADDR);
assign addr_dec  = (slave_addr == address) & (i2c_sm == `SM_ADDR);
assign     read = (i2c_sm == `SM_READ);
assign     write = (i2c_sm == `SM_WRITE);


//
// i2c State Machine
//  
// The state machine waits for a START sequence, then waits 
// to become the target of the transaction. If it isn't the 
// target, then it goes back to IDLE.  If it is the target,
// then it decides whether it's a READ or WRITE sequence. 
// When we're in a READ sequence - and we get to the end, 
// we move to WAIT until an ACK occurs. 
//

always @(start or cnt8 or addr_dec or
         ack or i2c_write or nack or i2c_sm)
  case(i2c_sm)
     `SM_IDLE:  if(start) begin
	          nxt_i2c_sm = `SM_ADDR;
				 end
	        else
	          nxt_i2c_sm = `SM_IDLE;
     `SM_ADDR:  if(cnt8 & ~addr_dec)
	          nxt_i2c_sm = `SM_IDLE;
	        else
	        if(cnt8 & addr_dec & ack & i2c_write)
	          nxt_i2c_sm = `SM_WRITE;
	        else
                if(cnt8 & addr_dec & ack & ~i2c_write)
                  nxt_i2c_sm = `SM_READ;
	        else
                if(cnt8 & addr_dec & nack)
                  nxt_i2c_sm = `SM_IDLE;
	        else
                  nxt_i2c_sm = `SM_ADDR;
     `SM_WRITE: if(start)
                  nxt_i2c_sm = `SM_ADDR;	
                else
                if(cnt8 & ack)
	          nxt_i2c_sm = `SM_WRITE;
	        else
                  nxt_i2c_sm = `SM_WRITE;
     `SM_READ:  if(start)
                  nxt_i2c_sm = `SM_ADDR;
                else
                if(cnt8 & nack)			 
	          nxt_i2c_sm = `SM_WAIT;
	        else
	          nxt_i2c_sm = `SM_READ;
     `SM_WAIT:  nxt_i2c_sm = `SM_WAIT;
      default:  nxt_i2c_sm = `SM_IDLE;
   endcase

wire sm_rst = stop | ~rst_n;   
always @(posedge scl_in or posedge sm_rst)
  if(sm_rst)
    i2c_sm <= `SM_IDLE;
  else
    i2c_sm <= nxt_i2c_sm;


//
// This counter is used to drive successive 8 bit registers in a write burst or
// Read burst. The cntr increments on the ack.
//
wire cyc_rst = ~rst_n | stop | start;
always @(posedge scl_in or posedge cyc_rst)
   if(cyc_rst == 1'b1)
      cyc_cnt <= 3'b0;
   else	   
   if(target & cnt8)
      cyc_cnt <= cyc_cnt + 3'b1;

//
// Determine when to drive SDA to indicate an ACK
//
// You drive SDA to 0 when it's an ACK and we're the receiving
// party.  We receive SDA at cnt8 when we're the transmitter  looking
// for an ACK.
//

reg sda_oe;
always @(negedge scl_in or negedge rst_n)
  if(~rst_n)
    sda_oe <= 1'b0;
  else sda_oe <= (target & read & ~cnt7 & ~last_bit) |
              (i2c_sm == `SM_ADDR) & cnt7 |
              write & cnt7;

assign  sda_out = (target & read & ~cnt8) ? int_sda :		// Read case
						((addr_dec | target) ? 1'b0 : 1'b1);	// ACK case

//
// Set Target when my_addr has been sent on the first
// transaction in the I2C sequence to us.  This means
// we're the target of the transaction.
//

always @(posedge scl_in or posedge cyc_rst)
  if(cyc_rst == 1'b1)
    target <= 1'b0;
  else
  if(ack & (i2c_sm == `SM_ADDR))
    target <= addr_dec;

//
// Int_reg is the internal register that we are writing to.
//

reg [7:0] int_reg0;
reg [7:0] int_reg1;
wire load_int_reg = ack & target & write;

always @(posedge scl_in or negedge rst_n)
  if(~rst_n)
    begin
     int_reg0 <=  8'b0;
     int_reg1 <= 8'b0;
    end
  else
  if(load_int_reg)
    case(cyc_cnt)
       3'h0: int_reg0 <= rcv_reg[7:0];
       3'h1: int_reg1 <= rcv_reg[7:0];
    endcase

//
// Select the rcv_reg# register that corresponds to the current
// transfer count for the appropriate high-byte/low-byte selection to send
//
reg  [7:0] main_out;
always @(cyc_cnt or rcv_reg0 or rcv_reg1)
  case(cyc_cnt)
     3'h0: main_out = rcv_reg0;
     3'h1: main_out = rcv_reg1;
  endcase

assign int_sda = main_out[7-rcv_cntr[2:0]]; 

endmodule 
