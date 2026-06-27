// Created by Kirk Weedman KD7IRS - Feb 15, 2009
//
// This is a parameterized module
//
// BCLK and LRCLK are all generated synchronously to CLK_IN.

`timescale 1 ns/100 ps

module clk_lrclk_gen (reset, CLK_IN, BCLK, Brise, Bfall, LRCLK, LRrise, LRfall, Speed);
parameter BCLK_00   = 32;          // desired number of BCLKs per half LRCLK cycle at Speed = 2'b00
parameter BCLK_01   = 32;          // desired number of BCLKs per half LRCLK cycle at Speed = 2'b01
parameter BCLK_10   = 32;          // desired number of BCLKs per half LRCLK cycle at Speed = 2'b10
parameter CLK_FREQ  = 122880000;   // frequency of incoming clock

input   wire          reset;       // reset
input   wire          CLK_IN;
output  reg           BCLK;
output  reg           Brise;
output  reg           Bfall;
output  reg           LRCLK;
output  reg           LRrise;
output  reg           LRfall;
input   wire    [1:0] Speed;       // 2'b00 = LRCLK_00, 2'b01 = LRCLK_01, 2'b10 = LRCLK_10

localparam LS = clogb2 (BCLK_00-1); // 0 to (BCLK_10-1)

// internal signals
//reg  [BS-1:0] BCLK_cnt;
reg    [15:0] BCLK_cnt;
reg    [15:0] BCLK_DIV;
reg  [LS-1:0] LRCLK_cnt;

localparam TPD = 1;

always @*
begin
  case (Speed)
  2'b00:  // 48kHz
    BCLK_DIV = (CLK_FREQ/48000/(BCLK_00*2));
  2'b01:  // 96kHz
    BCLK_DIV = (CLK_FREQ/96000/(BCLK_01*2));
  2'b10:  // 192kHz
    BCLK_DIV = (CLK_FREQ/192000/(BCLK_10*2));
  2'b11:  // oops
    BCLK_DIV = (CLK_FREQ/48000/(BCLK_00*2));
  endcase
end

// CLK_IN gets divided down to create BCLK
always @ (posedge CLK_IN)
begin
  if (reset)
    BCLK_cnt <= #TPD 0;
  else if (BCLK_cnt == (BCLK_DIV-1))
    BCLK_cnt <= #TPD 0;
  else
    BCLK_cnt <= #TPD BCLK_cnt + 1'b1; // 0, 1, ...(BCLK_DIV-1), 0, ...

  if (reset)
    Brise <= 1'b0;
  else
    Brise <= (BCLK_cnt == (BCLK_DIV/2));

  if (reset)
    Bfall <= 1'b0;
  else
    Bfall <= (BCLK_cnt == 1'b0); // may not be a 50/50 duty cycle

  if (Brise)
    BCLK  <= #TPD 1'b1;
  else if (Bfall)
    BCLK  <= #TPD 1'b0;

  if (reset)
    LRCLK_cnt <= #TPD 0;
  else 
  begin
    if ((LRCLK_cnt == 0) && Bfall)
    begin
      case (Speed)
      2'b00:  // 48kHz
        LRCLK_cnt <= #TPD BCLK_00-1;
      2'b01:  // 96kHz
        LRCLK_cnt <= #TPD BCLK_01-1;
      2'b10:  // 192kHz
        LRCLK_cnt <= #TPD BCLK_10-1;
      2'b11:  // oops
        LRCLK_cnt <= #TPD BCLK_00-1;
      endcase
    end
    else if (Bfall)
      LRCLK_cnt <= #TPD LRCLK_cnt - 1'b1;
  end

  if (reset)
    LRCLK  <= #TPD 1'b1;
  else if ((LRCLK_cnt == 0) && Bfall)
    LRCLK  <= #TPD ~LRCLK;  // toggles according to speed {DFS1, DFS0}

  if (reset)
    LRrise <= 1'b0;
  else
    LRrise <= (LRCLK_cnt == 0) && Bfall && !LRCLK;

  if (reset)
    LRfall <= 1'b0;
  else
    LRfall <= (LRCLK_cnt == 0) && Bfall && LRCLK; // may not be a 50/50 duty cycle
end

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction
endmodule