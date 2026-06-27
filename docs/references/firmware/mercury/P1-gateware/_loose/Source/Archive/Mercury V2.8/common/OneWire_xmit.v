/*
 Simple OneWire data transfers

 Kirk Weedman

 Feb. 29, 2009

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
  DOUT --------------------+ +-+-+-+ +-+-+-+ +-+-+-+ +-+-+-+ +-+-+-+-----------------------------------
       |<---- DLY_TIME---->|<----- 5 "logic 0's" ----------------->|<---low greater than 2 bits of time

       followd by DATA_BITS made of "logic 1's" and/or "logic 0's" which are repeated FRAME_FREQ times

*/
`timescale 1ns/100ps

module onewire_xmit (rst, clk, xmit_data, dout);

parameter CLK_FREQ   = 48000000; // Frequency in Hz of clk
parameter DLY_TIME   = 200;      // dealy time after reset (in milliseconds)
parameter SYNC_BITS  = 5;        // Number of "logic 0" sync bits used by receiver to determine TB
parameter DATA_BITS  = 64;       // Number of data bits (logic 0 or 1) to transmit
parameter FRAME_FREQ = 1000;     // frequency in Hz of each complete DATA_BITs transmission
parameter TPD        = 1.3;

parameter NUM_DLY_CLKS = CLK_FREQ/1000 * DLY_TIME;
//localparam NUM_DLY_CLKS = CLK_FREQ/1000 * DLY_TIME;
localparam XS    = clogb2(DATA_BITS-1);
localparam FS    = clogb2(FRAME_FREQ-1);
localparam DS    = clogb2(NUM_DLY_CLKS-1);
localparam Q1    = CLK_FREQ/FRAME_FREQ/DATA_BITS/4;
localparam Q123  = 3*Q1;
localparam Q1234 = 4*Q1;
localparam LOW_TIME = 4*Q1234;
localparam QS    = clogb2(LOW_TIME-1);

input  wire                 rst;        // reset signal
input  wire                 clk;        // high speed sampling clock
input  wire [DATA_BITS-1:0] xmit_data;
output reg                  dout;

// local registers
reg [4:0] OW_state;
reg [4:0] OW_state_next;

reg        [DS-1:0] dly_cnt;
reg        [QS-1:0] bcnt;
reg [DATA_BITS-1:0] xd;
reg        [XS-1:0] data_cnt;
reg        [FS-1:0] frame_cnt;

localparam OW_IDLE     = 0,
           OW_DLY      = 1,
           OW_FRAME    = 2,
           OW_SYNC_H   = 3,
           OW_SYNC_L   = 4,
           OW_LOW      = 5,
           OW_DATA_Q1  = 6,
           OW_DATA_Q23 = 7,
           OW_DATA_Q4  = 8;

always @(posedge clk)
begin
  if (OW_state != OW_DLY)
    dly_cnt <= #TPD 1'b0;
  else if (dly_cnt != NUM_DLY_CLKS)
    dly_cnt <= #TPD dly_cnt + 1'b1;

  if (OW_state == OW_FRAME)
    bcnt <= #TPD 1'b0;
  else if ((OW_state == OW_SYNC_L) && (bcnt == Q1234))
    bcnt <= #TPD 1'b0;
  else if ((OW_state == OW_LOW) && (bcnt == LOW_TIME))
    bcnt <= #TPD 1'b0;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234))
    bcnt <= #TPD 1'b0;
  else
    bcnt <= #TPD bcnt + 1'b1;

  if (OW_state == OW_FRAME)
    xd <= #TPD xmit_data;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234) && (data_cnt == (DATA_BITS-1)))
    xd <= #TPD xmit_data;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234))
    xd <= #TPD xd >> 1;

  if ((OW_state == OW_FRAME) || (OW_state == OW_LOW))
    data_cnt <= #TPD 1'b0;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234) && (data_cnt == (DATA_BITS-1)))
    data_cnt <= #TPD 1'b0;
  else if ((OW_state == OW_SYNC_L) && (bcnt == Q1234))
    data_cnt <= #TPD data_cnt + 1'b1;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234))
    data_cnt <= #TPD data_cnt + 1'b1;

  if (OW_state == OW_LOW)
    frame_cnt <= #TPD 1'b0;
  else if ((OW_state == OW_DATA_Q4) && (bcnt == Q1234) && (data_cnt == (DATA_BITS-1)))
    frame_cnt <= #TPD frame_cnt + 1'b1;

  if (rst)
    OW_state <= #TPD OW_IDLE;
  else
    OW_state <= #TPD OW_state_next;
end


// OneWire state machine
always @*
begin
   case(OW_state)
     OW_IDLE:
     begin
       dout = 1'b0;
       OW_state_next = OW_DLY;  // Wait DLY_TIME before sending FRAMEs
     end

     OW_DLY:
     begin
       dout = 1'b0;
       if (dly_cnt != NUM_DLY_CLKS)
         OW_state_next = OW_DLY;
       else
         OW_state_next = OW_FRAME;  // send a FRAME now
     end

     OW_FRAME:
     begin
       dout = 1'b0;
       OW_state_next = OW_SYNC_H;
     end

     OW_SYNC_H: // high for 1st 1/4 of TB
     begin
       dout = 1'b1;
       if (bcnt != Q1) // 1st quarter of sync bit
         OW_state_next = OW_SYNC_H;
       else
         OW_state_next = OW_SYNC_L;
     end

     OW_SYNC_L: // low for last 3/4 of TB = "logic 0" = our sync signal
     begin
       dout = 1'b0;
        if (bcnt != Q1234) // last 3/4s of sync bit
         OW_state_next = OW_SYNC_L;
       else if (data_cnt != (SYNC_BITS-1))
         OW_state_next = OW_SYNC_H;
       else
         OW_state_next = OW_LOW;
     end

     OW_LOW: // low for 4*TB which signals an auto resync
     begin
       dout = 1'b0;
       if (bcnt != LOW_TIME)
         OW_state_next = OW_LOW;
       else
         OW_state_next = OW_DATA_Q1;
     end

     OW_DATA_Q1: // during 1st quarter of "logic n", dout = 1
     begin
       dout = 1'b1;
        if (bcnt != Q1) // 1st 1/4 of data bit
         OW_state_next = OW_DATA_Q1;
       else
         OW_state_next = OW_DATA_Q23;
     end

     OW_DATA_Q23: // during 2nd & 3rd quarter of "logic n", dout = n
     begin
       dout = xd[0];
       if (bcnt != Q123) // 2nd and 3rd quarter of data bit
         OW_state_next = OW_DATA_Q23;
       else
         OW_state_next = OW_DATA_Q4;
     end

     OW_DATA_Q4: // during last quarter of "logic n", dout = 0
     begin
       dout = 1'b0;
       if (bcnt != Q1234)
         OW_state_next = OW_DATA_Q4;   // haven't finished sending this data bit
       else if (data_cnt != (DATA_BITS-1))
         OW_state_next = OW_DATA_Q1;   // this data bit has finished, but not all the DATA_BITS have been sent
       else if (frame_cnt != (FRAME_FREQ-1))
         OW_state_next = OW_DATA_Q1;   // all the DATA_BITS have been sent, now repeat them FRAME_FREQ times
       else
         OW_state_next = OW_FRAME;     // all the DATA_BITS have been repeated for the frame, now repeat the frame
     end

     default:
     begin
       dout = 1'b0;
       OW_state_next = OW_IDLE;
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