/*
 Simple NWire data transfers

 Kirk Weedman

 Sept. 17, 2009

 Concept:
   Transfer data across N wires between FPGAs using a sync or data ready signal to start transfers

 Method:
    DLY_TIME: time that NWire_xmit holds signal low after reset occurs
    OSL: Number of "logic 0" or "logic 1" bits occuring after DLY_TIME
    TB: Amount of time it takes to transmit a "logic 0" or logic 1"

    After reset, the DOUT signal is held low for DLY_TIME, followed by OSL data bits and then a low for 3*TB.
    The low for 3*TB is used by the receiver to syncnronize itself to the dataa stream

                +---+           +
    DATA_BIT ---+   +---+---+---+  "logic 0"
                |<----- TB ---->|

                +---+---+---+   +
             ---+           +---+  "logic 1"

                           +-----------------------+                +---
  DIN  --------------------+-----------------------+----------------+---
       |<---- DLY_TIME---->|<------- OSL*TB ------>|<---- 3*TB ---->|
                           |<----------- 1/SEND_FREQ -------------->|



        ---------------+-----------------------------------------------------------
xrcv_data -------------+-----------------------------------------------------------

                       +--------------------------+
xrcv_rdy --------------+                          +--------------------------------

                                               +------+
xrcv_ack --------------------------------------+      +----------------------------

Note:  The receiver has new data ready when rcv_rdy goes high
       xmit_ack should be asserted while rcv_rdy = 1
       xmit_ack should be deasserted once rcv_rdy goes low or immediately after getting new data
*/

`timescale 1ns/100ps

module NWire_rcv (irst, iclk, xrst, xclk, xrcv_data, xrcv_rdy, xrcv_ack, din);

parameter OSL          = 32; // Output Stream Length
parameter OSW          = 1;  // Output Stream Width
parameter SLOWEST_FREQ = 10000;
parameter ICLK_FREQ    = 144000000;
parameter XCLK_FREQ    = 48000000;
parameter TPD          = 1;

localparam DATA_BITS   = OSL * OSW;            // Total number of data bits (logic 0 or 1) to receive: must be > 5?
localparam DC          = clogb2(OSL-1);
localparam DB          = clogb2(OSL);
localparam DLY_TIME    = ICLK_FREQ/SLOWEST_FREQ;
localparam DS          = clogb2(DLY_TIME-1);

input  wire                 irst;       // reset signal
input  wire                 iclk;       // high speed sampling clock
input  wire                 xrst;       // reset signal
input  wire                 xclk;       // high speed sampling clock
output wire [DATA_BITS-1:0] xrcv_data;  // recieve data
output wire                 xrcv_rdy;   // receive data is ready
input  wire                 xrcv_ack;   // used to clear xrcv_rdy
input  wire       [OSW-1:0] din;        // Nwire interface serial data input

// local registers
reg          [DS:0] tb_cnt;
reg          [DS:0] tb_width;
wire         [DS:0] Q12;
reg        [DB-1:0] DBrise_cnt;

reg       [OSL-1:0] rdata[0:OSW-1];
reg        [DC-1:0] data_cnt;

reg       [OSW-1:0] d3, d2, d1, d0;
reg                 DBrise;
reg           [2:0] TB_state;
reg           [2:0] TB_state_next;
reg          [DS:0] DB_LEN [0:3]; // SYNC_BIT TB time recording
reg                 rcv_flag;
reg                 resync;
reg           [3:0] pass;
wire         [DS:0] tb_min, tb_max;
reg [DATA_BITS-1:0] idata;  // recieve data
reg                 irdy;
wire                iack;

localparam TB_IDLE   = 0,
           TB_DB     = 1,
           TB_CALC   = 2,
           TB_SYNC   = 3,
           TB_NEXT   = 4,
           TB_BIT    = 5;

assign Q12 = (tb_width >> 1); // TB/2
assign tb_min = tb_width - 3'd2;
assign tb_max = tb_width + 3'd2;

generate
if (ICLK_FREQ == XCLK_FREQ)
begin: SAME_CLK
  assign xrcv_data = idata;
  assign xrcv_rdy  = irdy;
  assign iack      = xrcv_ack;
end
else // ICLK_FREQ != XCLK_FREQ
begin: DIFF_CLK
  reg [DATA_BITS-1:0] xd1, xd0;
  reg xr2, xr1, xr0;
  reg ia1, ia0;

  assign xrcv_data = xd1;
  assign xrcv_rdy  = xr2;
  assign iack      = ia1;

  always @(posedge xclk) 
  begin
    {xd1, xd0} <= #TPD {xd0, idata};
    {xr2, xr1, xr0} <= #TPD {xr1, xr0, irdy};
  end

  always @(posedge iclk) 
  begin
    {ia1, ia0} <= #TPD {ia0, xrcv_ack};
  end
end
endgenerate

generate
  genvar i;
  for (i = 0; i < OSW; i = i + 1)
  begin: STREAM_DATA
    always @(posedge iclk)
    begin
      if (rcv_flag)
        idata[i*OSL +: OSL] <= #TPD rdata[i];
 
      if (irst)
        rdata[i] <= #TPD 1'b0;
      else if ((TB_state == TB_BIT) && (tb_cnt == Q12))
        rdata[i] <= #TPD {d3[i],rdata[i] [OSL-1:1]}; // shift in all streams into [OSL-1:0] rdata [0:OSW-1]
    end
  end
endgenerate

integer k;
always @(posedge iclk)
begin
  if (irst)
    DBrise_cnt <= #TPD 1'b0;
  else if (TB_state == TB_IDLE)
    DBrise_cnt <= #TPD 1'b0;
  else if (DBrise && (DBrise_cnt != 5))
    DBrise_cnt <= #TPD DBrise_cnt + 1'b1; // count number of DBrise during TB_SYNC state
 
  if (irst)
    {d3, d2, d1, d0} <= #TPD 4'b0000;
  else
    {d3, d2, d1, d0} <= #TPD {d2, d1, d0, din}; // just use the LSB since all streams have sync info

  if (irst)
    DBrise <= #TPD 1'b0;
  else
    DBrise <= #TPD d1[0] & !d2[0]; // rising edge of DATA_BIT or SYNC_BIT

  if (irst)
    tb_cnt <= #TPD 1'b0;
  else if (DBrise)
    tb_cnt <= #TPD 1'b0;
  else if (tb_cnt != -1)
    tb_cnt <= #TPD tb_cnt + 1'b1;

  if (irst)
    tb_width <= #TPD 0;
  else if (TB_state == TB_CALC)
    tb_width <= #TPD (DB_LEN[0]+DB_LEN[1]+DB_LEN[2]+DB_LEN[3]) >> 2; // new average of last 4 DATA_BIT timings
// Note: when using a highspeed clock, these 4 adds may take more than 1 clock period
//       resulting in timing analysis failure.  However, in this case, all the DB_LEN[] values
//       have been stable for TB time (many clocks) before getting this TB_CALC state.
//       Therefore the way to cure the problem is to declare the DB_LEN[] to tb_width path
//       as a Multicycle path in the Assignment editor.  4 clocks should be plenty

  for (k = 0; k < 4; k = k + 1)
  begin
    if (irst)
      pass[k] <= #TPD 1'b0;
    else
      pass[k] <= (tb_min <= DB_LEN[k]) && (DB_LEN[k] <= tb_max);
  end

  if (irst)
    {DB_LEN[3], DB_LEN[2], DB_LEN[1], DB_LEN[0]} <= #TPD {{DS{1'b0}}, {DS{1'b0}}, {DS{1'b0}}, {DS{1'b0}}};
  else if ((TB_state == TB_DB) && DBrise)
    {DB_LEN[3], DB_LEN[2], DB_LEN[1], DB_LEN[0]} <= #TPD {DB_LEN[2], DB_LEN[1], DB_LEN[0], tb_cnt};

  if (irst)
    TB_state <= #TPD TB_IDLE;
  else
    TB_state <= #TPD TB_state_next;

  rcv_flag <= #TPD ((TB_state == TB_BIT) && (data_cnt == (OSL-1)) && (tb_cnt == Q12));

  if (irst)
    irdy <= #TPD 1'b0;
  else if (rcv_flag)
    irdy <= #TPD 1'b1;
  else if (iack)
    irdy <= #TPD 1'b0;

  if (irst)
    data_cnt <= #TPD 1'b0;
  else if (TB_state == TB_NEXT)
    data_cnt <= #TPD 1'b0;
  else if (DBrise)
    data_cnt <= #TPD data_cnt + 1'b1;

  if (irst)
    resync <= #TPD 1'b0;
  else if (tb_cnt == (tb_width + Q12))
    resync <= #TPD 1'b1; // bit width is too big for this bit. Maybe transmitter got reset or turned off
  else
    resync <= #TPD 1'b0;
end

always @*
begin
   case(TB_state)
     TB_IDLE:
     begin
       if (DBrise)
         TB_state_next = TB_DB;
       else
         TB_state_next = TB_IDLE;  // wait for a DBrise to occur
     end

     TB_DB:  // now wait for 4 more DBrise signals
     begin
       if (DBrise_cnt != 5)
         TB_state_next = TB_DB;
       else
         TB_state_next = TB_CALC;  // wait for 4 more DBrise's to occur so DB_LEN has filled with data
     end

     TB_CALC:
     begin
       if (!DBrise)
         TB_state_next = TB_CALC; // wait till next DBrise before checking "pass"
       else if (&pass)
         TB_state_next = TB_SYNC; // we have a valid TB timing, now go find end of serial stream
       else
         TB_state_next = TB_IDLE; // try getting 5 more DBrise signals and recalculate
     end

     TB_SYNC:  // now wait for resync (should be at least a period of 3*TB where no DBrise occurs)
     begin
       if (resync)
         TB_state_next = TB_NEXT; 
       else
         TB_state_next = TB_SYNC;  // continue getting SYNC_BITs
     end

     TB_NEXT:
     begin
       if (DBrise)
         TB_state_next = TB_BIT;
      else
         TB_state_next = TB_NEXT;   // stay here until 1st DBrise of data input stream
     end

     TB_BIT:
     begin
       if (resync)
         TB_state_next = TB_IDLE; // oops.  bit width got too big.  Maybe transmitter got turned off or reset
       else if (rcv_flag) // normally we just wait for rcv_flag to end this state.
         TB_state_next = TB_NEXT;  // this stream of DATA_BITs has finished
       else
         TB_state_next = TB_BIT;
     end

     default:
     begin
       TB_state_next = TB_SYNC;
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