// Copyright 2009  Kirk Weedman KD7IRS 
//
//  HPSDR - High Performance Software Defined Radio
//
//  Mercury to Atlas bus interface.
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
//
// Change log:
//
// 22 Feb 2009 - first version

/*
 Simple OneWire data transfers


 Concept:
   Transfer data across 1 wire between FPGAs

 Method:
    DLY_TIME: time signal held low after reset occurs
    SYNC_BITS: 5 "logic 0's" transmitted once every FRAME_FREQ DATA_BITS
    DATA_BITS: Number of "logic 0" or "logic 1" bits occuring after SYNC_BITS:
    FRAME_FREQ: Number of times DATA_BITS are repeatedly sent before sending SYNC_BITS again, etc..
    FRAME: SYNC_BITS, low on DOUT for more than 4*TB, followed by DATA_BITS repeated FRAME_FREQ times
    TB: Amount of time it takes to transmit a "logic 0" or logic 1"

    After reset, the DOUT signal is held low for DLY_TIME, followed by SYNC_BITS, then followed by
    a low for greater than the length of 4 TB time periods (signaling an auto resync), then
    followed by DATA_BITS which repeat FRAME_FREQ times or until another reset occurs.

                +---+           +
    SYNC_BIT ---+   +---+---+---+  "logic 0"
                |<----- TB ---->|

                +---+---+---+   +
             ---+           +---+  "logic 1"

    To determine of a bit is a logic 0 or 1:
       1. Use a sampling clock at least 8 times faster than 1/TB.
       2. Calculate the number of sampling clocks (N) for one SYNC_BIT by averaging
          the time for all the SYNC_BITS
       3. Divide N by 2 = M
       4. At M sampling clocks after the beginning of a DATA_BIT, sample the signal
          and determine if its a logic 1 or 0

                           +-+     +-+     +-+     +-+     +-+
  DIN  --------------------+ +-+-+-+ +-+-+-+ +-+-+-+ +-+-+-+ +-+-+-+-----------------------------------
       |<---- DLY_TIME---->|<----- 5 "logic 0's" ----------------->|<---low greater than 2 bits of time

       followd by DATA_BITS made of "logic 1's" and/or "logic 0's" which are repeated FRAME_FREQ times

*/
`timescale 1ns/100ps

module onewire_rcv (rst, clk, rcv_data, rcv_rdy, din);

parameter SYNC_BITS  = 5;        // Number of "logic 0" sync bits used by receiver to determine TB
parameter DATA_BITS  = 64;       // Number of data bits (logic 0 or 1) to transmit
parameter TPD        = 1.3;

localparam XS    = clogb2(DATA_BITS-1);

input  wire                 rst;        // reset signal
input  wire                 clk;        // high speed sampling clock
output reg  [DATA_BITS-1:0] rcv_data;
output reg                  rcv_rdy;
input  wire                 din;

// local registers
reg          [15:0] tb_cnt;
reg          [15:0] tb_width;
wire         [15:0] Q12;

reg [DATA_BITS-1:0] rdata;
reg        [XS-1:0] data_cnt;

reg                 d2, d1, d0;
reg                 DBrise;
reg           [1:0] TB_state;
reg           [1:0] TB_state_next;
reg          [15:0] S_TB [0:3]; // SYNC_BIT TB time recording
wire                resync;
wire         [15:0] timeout;
reg                 tb_flag;

localparam TB_IDLE   = 0,
           TB_BIT    = 1,
           TB_NEXT   = 2;

assign timeout = (tb_width<<1); // din low for more than 2 * tb_width is a resync
// resync occurs if low more than 2*TB once we've gotten the initial SYNC_BITS
assign resync  = (tb_cnt == timeout) && tb_flag;

assign Q12 = (tb_width >> 1); // TB/2

always @(posedge clk)
begin
  if (rst)
    tb_flag <= #TPD 1'b0;
  else if (data_cnt == (SYNC_BITS-1))
    tb_flag <= #TPD 1'b1; // stays high once 1st set of SYNC_BITS has finished

  if (rst)
    {d2, d1, d0} <= #TPD 3'b0;
  else
    {d2, d1, d0} <= #TPD {d1, d0, din};

  if (rst)
    DBrise <= #TPD 1'b0;
  else
    DBrise <= #TPD d1 & !d2; // rising edge of DATA_BIT or SYNC_BIT

  if (rst)
    tb_cnt <= #TPD 1'b0;
  else if ((TB_state == TB_NEXT) || resync)
    tb_cnt <= #TPD 1'b0;
  else if (TB_state == TB_BIT)
    tb_cnt <= #TPD tb_cnt + 1'b1;

  if (rst)
    tb_width <= #TPD 0;
  else if (!tb_flag && (data_cnt == (SYNC_BITS-1)))
    tb_width <= #TPD (S_TB[0]+S_TB[1]+S_TB[2]+S_TB[3]) >> 2; // new average of SYNC_BIT timings
  else if (tb_flag && resync) // once every second
    tb_width <= #TPD (S_TB[0]+S_TB[1]+S_TB[2]+S_TB[3]) >> 2; // new average of SYNC_BIT timings

  if (rst)
    {S_TB[3], S_TB[2], S_TB[1], S_TB[0]} <= #TPD {16'b0,16'b0,16'b0,16'b0};
  else if (TB_state == TB_NEXT)
    {S_TB[3], S_TB[2], S_TB[1], S_TB[0]} <= #TPD {S_TB[2], S_TB[1], S_TB[0], tb_cnt};

  if (rst)
    TB_state <= #TPD TB_IDLE;
  else
    TB_state <= #TPD TB_state_next;

  if (rst)
    rdata <= #TPD 1'b0;
  else if ((TB_state == TB_BIT) && (tb_cnt == Q12))
    rdata <= #TPD {din,rdata[DATA_BITS-1:1]};

  if ((TB_state == TB_NEXT) && (data_cnt == (DATA_BITS-1)))
    rcv_data <= #TPD rdata;

  if (rst)
    rcv_rdy <= #TPD 1'b0;
  else
    rcv_rdy <= #TPD ((TB_state == TB_NEXT) && (data_cnt == (DATA_BITS-1)));

  if (rst)
    data_cnt <= #TPD 1'b0;
  else if (resync)
    data_cnt <= #TPD 1'b0;
  else if ((TB_state == TB_NEXT) && (data_cnt == (DATA_BITS-1)))
    data_cnt <= #TPD 1'b0;
  else if (TB_state == TB_NEXT)
    data_cnt <= #TPD data_cnt + 1'b1;
end

always @*
begin
   case(TB_state)
     TB_IDLE:
     begin
       if (DBrise)
         TB_state_next = TB_BIT;  // beginning of a DATA_BIT or SYNC_BIT
       else
         TB_state_next = TB_IDLE;  // Wait for 1st DATA_BIT or SYNC_BIT
     end

     TB_BIT:
     begin
       if (resync)
         TB_state_next = TB_IDLE;
       else if (DBrise)
         TB_state_next = TB_NEXT;  // beginning of nexy DATA_BIT or SYNC_BIT
       else
         TB_state_next = TB_BIT;
     end

     TB_NEXT:
     begin
       TB_state_next = TB_BIT;
     end

     default:
     begin
       TB_state_next = TB_IDLE;
     end
   endcase
end


function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule