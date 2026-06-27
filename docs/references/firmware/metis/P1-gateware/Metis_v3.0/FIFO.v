//
//  HPSDR - High Performance Software Defined Radio
//
//  Metis code. 
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



// FIFO - copyright 2010, 2011 Kirk Weedman KD7IRS

/*
	change log:


*/



module FIFO (rst, clk, full, usedw, empty, wrreq, data, rdreq, q);

parameter SZ = 2048;
parameter WD = 16;

localparam DP= clogb2(SZ-1);

input  wire          rst;
input  wire          clk;
output wire          full;
output reg  [DP-1:0] usedw;
output wire          empty;
input  wire          wrreq;
input  wire [WD-1:0] data;
input  wire          rdreq;
output reg  [WD-1:0] q;

reg [WD-1:0] mem [0:SZ-1];
reg [DP-1:0] inptr, outptr;

assign full = (usedw == {DP{1'b1}});
assign empty = (usedw == 1'b0);

always @(posedge clk)
begin
  if (rst)
    inptr <= 1'b0;
  else if (wrreq)
    inptr <= inptr + 1'b1;

  if (rst)
    outptr <= 1'b0;
  else if (rdreq)
    outptr <= outptr + 1'b1;

  if (rst)
    usedw <= 1'b0;
  else if (rdreq & !wrreq)
    usedw <= usedw - 1'b1;
  else if (wrreq & !rdreq)
    usedw <= usedw + 1'b1;

  if (wrreq)
    mem[inptr] <= data;

  if (rdreq)
    q <= mem[outptr];
end

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule