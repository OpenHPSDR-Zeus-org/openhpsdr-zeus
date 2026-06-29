/*
 Simple NWire data transfers

 Kirk Weedman

 Mar. 5, 2009

 Concept:
   Transfer data across N wires between FPGAs using a sync or data ready signal to start transfers

 Method:
    DLY_TIME: time signal held low after reset occurs
    OSL: Number of "logic 0" or "logic 1" bits occuring after DLY_TIME
    TB: Amount of time it takes to transmit a "logic 0" or logic 1"

    After reset, the DOUT signal is held low for DLY_TIME, followed by OSL data bits and
    then a low for 3*TB.  The low for 3*TB is used by the receiver to syncnronize itself
    to the dataa stream

                +---+           +
    data bit ---+   +---+---+---+  "logic 0"
                |<----- TB ---->|

                +---+---+---+   +
             ---+           +---+  "logic 1"

                           +-----------------------+                +---
  DOUT --------------------+-----------------------+----------------+---
       |<---- DLY_TIME---->|<------- OSL*TB ------>|<-- LOW_BITS -->|
                           |<----------- 1/SEND_FREQ -------------->|



                                                  +--------+                          +---
     ---------------------------------------------+        +--------+--------+--------+
NW_state ------------- WAIT --------------------->|<- Q1 ->|<------ Q23 ---->|<- Q4 ->|

         ------------------------------------------+
xrdy                                               +--------------------------------------

                                              +---+---------------------------+
xreq     -------------------------------------+   +---------------------------+------

                                                +----------------------------+
xack     ---------------------------------------+                            +----------------

Note:  The transmitter is ready for more xdata ONLY when xrdy = 1
       xrdy goes high right after the LOW_BITS time.
       xreq should only be asserted while xrdy = 1
       xreq should only be deasserted once xack = 1
       xreq MUST be removed before the xdata stream is completed (best to be low soon after xack goes low)
       xack will go low approximately 3/4 of the way through the first DATA_BIT

	   Spet 6, 2009: Added true NWire ability - Now the output data stream can be OSL bits long and OSW bits wide.
*/

`timescale 1ns/100ps

module NWire_xmit (irst, iclk, xrst, xclk, xdata, xreq, xrdy, xack, dout);

parameter OSW          = 1'b1;      // Output Stream Width
parameter OSL          = 6'd32;     // Output Stream Length
parameter XCLK_FREQ    = 48000000;  // Frequency in Hz of xclk
parameter ICLK_FREQ    = 144000000; // Frequency in Hz of iclk
parameter DLY_TIME     = 200;       // delay time after reset (in milliseconds)
parameter SEND_FREQ    = 48000;     // frequency in Hz of each complete OSL+LOW_BITS transmission
parameter LOW_BITS     = 3;         // number of bits to remain low after OSL biits are sent
parameter TPD          = 1;
parameter NUM_DLY_CLKS = ICLK_FREQ/1000 * DLY_TIME;
localparam QT          = ICLK_FREQ/SEND_FREQ/(OSL+LOW_BITS); // total clocks needed for each TB time
parameter LOW_TIME     = LOW_BITS*QT;

localparam DATA_BITS = OSL * OSW; // total data bits entered into xdata as {OSL_N-1, OSL_N-2...., OSL_0} where N = OSW
// example: OSL = 8, OSW = 3 so xdata is {OSL_2, OSL_1, OSL_0} which is 24 bits, where {OSL_2[0], OSL_1[0], OSL_0[0}} are the first
//          data bits transmitted in parallel
localparam XS    = clogb2(OSL-1);
localparam DS    = clogb2(NUM_DLY_CLKS-1);
localparam Q1    = QT/4; // rounds down
localparam Q123  = QT - Q1;
localparam Q1234 = QT;
localparam QS    = clogb2(LOW_TIME-1);

input  wire                 irst;  // reset signal
input  wire                 iclk;  // internal data sampling/state machine clock
input  wire                 xrst;  // reset signal
input  wire                 xclk;  // external interface clock
input  wire [DATA_BITS-1:0] xdata;
input  wire                 xreq;  // someone wants to send new xdata
output wire                 xrdy;  // transmitter is ready for more xdata
output wire                 xack;  // acknowledge that xreq has occured
output reg        [OSW-1:0] dout;  // 1 to N bits output stream width

// local registers
reg [2:0] NW_state;
reg [2:0] NW_state_next;

//reg         [DS-1:0] dly_cnt;
reg           [DS:0] dly_cnt;
reg         [QS-1:0] bcnt;
reg         [XS-1:0] data_cnt;
reg                  irdy;
reg                  iack;
wire                 ireq;
reg        [OSL-1:0] id [0:OSW-1];
wire [DATA_BITS-1:0] idata;
wire       [OSW-1:0] OUT;

localparam NW_IDLE     = 0,
           NW_DLY      = 1,
           NW_WAIT     = 2,
           NW_DATA_Q1  = 3,
           NW_DATA_Q23 = 4,
           NW_DATA_Q4  = 5,
           NW_LOW      = 6;

generate
if (ICLK_FREQ == XCLK_FREQ)
begin: SAME_CLK
  assign xrdy  = irdy;
  assign xack  = iack;

  assign idata = xdata;
  assign ireq  = xreq;
end
else
begin: DIFF_CLK
  reg xr1, xr0;
  reg xa1, xa0;
  reg [DATA_BITS-1:0] id1, id0;
  reg iq2, iq1, iq0;

  always @(posedge xclk) 
  begin
    {xr1, xr0} <= #TPD {xr0, irdy};
    {xa1, xa0} <= #TPD {xa0, iack};
  end

  always @(posedge iclk) 
  begin
    {id1, id0}  <= #TPD {id0, xdata};
    {iq2, iq1, iq0} <= #TPD {iq1, iq0, xreq}; // if xdata and xreq are at the same time make sure ireq will not occur before idata
  end

  assign xrdy = xr1;
  assign xack = xa1;

  assign idata = id1;
  assign ireq  = iq2;
end
endgenerate

always @(posedge iclk)
begin
  if (irst)
    irdy <= #TPD 1'b0;
  else if (NW_state == NW_WAIT)
    irdy <= #TPD 1'b1;
  else
    irdy <= #TPD 1'b0;

  if (irst)
    iack <= #TPD 1'b0;
  else if (irdy && ireq) // ignore requests until irdy is true
    iack <= #TPD 1'b1;
  else if (NW_state == NW_DATA_Q4) // wait till first DATA_BIT mostly done before clearing xack
    iack <= #TPD 1'b0;

  if (irst)
    dly_cnt <= #TPD 1'b0;
//  else if (dly_cnt != NUM_DLY_CLKS) // Slow because of the size and causing timing analysis problems
  else if (!dly_cnt[DS]) // a little longer but the compare is much simpler for a large counter and FASTER
    dly_cnt <= #TPD dly_cnt + 1'b1;

  if (irst)
    bcnt <= #TPD 1'b0;
  else if (NW_state == NW_WAIT)
    bcnt <= #TPD 1'b0;
  else if ((NW_state == NW_DATA_Q4) && (bcnt == Q1234))
    bcnt <= #TPD 1'b0;
  else if ((NW_state == NW_LOW) && (bcnt == LOW_TIME))
    bcnt <= #TPD 1'b0;
  else
    bcnt <= #TPD bcnt + 1'b1;

  if (irst || (NW_state == NW_WAIT))
    data_cnt <= #TPD 1'b0;
  else if ((NW_state == NW_DATA_Q4) && (bcnt == Q1234))
    data_cnt <= #TPD data_cnt + 1'b1;

  if (irst)
    NW_state <= #TPD NW_IDLE;
  else
    NW_state <= #TPD NW_state_next;
end

generate
  genvar i;
  for (i = 0; i < OSW; i = i + 1)
  begin: STREAM_DATA
    always @(posedge iclk)
    begin
      if (NW_state == NW_WAIT)
        id[i] <= #TPD idata[i*OSL +: OSL];
      else if ((NW_state == NW_DATA_Q4) && (bcnt == Q1234))
        id[i] <= #TPD id[i] >> 1;
    end
	assign OUT[i] = id[i][0];
  end
endgenerate

// NWire state machine
always @*
begin
   case(NW_state)
     NW_IDLE:
     begin
       dout = {OSW{1'b0}};
//       if (dly_cnt != NUM_DLY_CLKS)
       if (!dly_cnt[DS])
         NW_state_next = NW_IDLE;   // Wait at least DLY_TIME before sending OSL data bits
       else
         NW_state_next = NW_WAIT;  // send OSL data bits now
     end

     NW_WAIT:
     begin
       dout = {OSW{1'b0}};
       if (iack)
         NW_state_next = NW_DATA_Q1;  // we have permission to transmit a stream of OSL data bits
       else
         NW_state_next = NW_WAIT;
     end

     NW_DATA_Q1: // during 1st quarter of "logic n", dout = 1
     begin
       dout = {OSW{1'b1}};
       if (bcnt != Q1) // 1st 1/4 of data bit
         NW_state_next = NW_DATA_Q1;
       else
         NW_state_next = NW_DATA_Q23;
     end

     NW_DATA_Q23: // during 2nd & 3rd quarter of "logic n", dout = n
     begin
       dout = OUT;  // LSB of each id[]
       if (bcnt != Q123) // 2nd and 3rd quarter of data bit
         NW_state_next = NW_DATA_Q23;
       else
         NW_state_next = NW_DATA_Q4;
     end

     NW_DATA_Q4: // during last quarter of "logic n", dout = 0
     begin
       dout = {OSW{1'b0}};
       if (bcnt != Q1234)
         NW_state_next = NW_DATA_Q4;   // haven't finished sending this data bit
       else if (data_cnt != (OSL-1))
         NW_state_next = NW_DATA_Q1;   // this data bit has finished, but not all have been sent
       else
         NW_state_next = NW_LOW;       // data bits have finished
     end

     NW_LOW: // send out a low signal during this time
     begin
       dout = {OSW{1'b0}};
       if (bcnt != LOW_TIME)
         NW_state_next = NW_LOW;
       else
         NW_state_next = NW_WAIT;      // LOW_BITS have finished
     end

     default:
     begin
       dout = {OSW{1'b0}};
       NW_state_next = NW_IDLE;
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