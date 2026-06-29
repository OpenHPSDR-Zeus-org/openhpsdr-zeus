// Created by Kirk Weedman KD7IRS - Feb 15, 2009
//
// This is a parameterized module
//
// clk_in is divided by CLK_DIV to create clk_out.

`timescale 1 ns/100 ps

module clk_div (reset, clk_in, clk_out);
input  wire reset;
input  wire clk_in;
output reg  clk_out;

parameter CLK_DIV   = 10;          // clk_out = clk_in/CLK_DIV
parameter TPD = 1;
localparam SZ = clogb2 ((CLK_DIV/2)-1); // 0 to ((CLK_DIV/2)-1)

reg  [SZ-1:0] cnt;

always @ (posedge clk_in)
begin
  if (reset)
    clk_out  <= #TPD 1'b0;
  else if (cnt == ((CLK_DIV/2)-1))
    clk_out  <= #TPD ~clk_out;

  if (reset)
    cnt <= #TPD 0;
  else if (cnt == ((CLK_DIV/2)-1))
    cnt <= #TPD 1'b0;
  else
    cnt <= #TPD cnt + 1'b1;
end

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule