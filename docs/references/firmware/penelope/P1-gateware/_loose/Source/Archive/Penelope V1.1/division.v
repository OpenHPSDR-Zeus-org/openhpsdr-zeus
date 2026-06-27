// V1.0 17th July 2007
//
// Copyright 2007 Phil Harman VK6APH
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  64 bit division  
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

//  divide program using successive subtraction, slow but fast enough for our needs. 


module division(quotient,ready,dividend,divider,clk);
   
   input [31:0]  dividend; // frequency 
   input [31:0]  divider;  // 125MHz
   input         clk;
   output [31:0] quotient;
   output        ready;

   reg [95:0]    qr;
   reg [33:0]    diff;
   reg [31:0]    quotient;


   always @(negedge clk)
   begin
	if (ready)					// get result when ready 
		quotient <= qr[31:0];
   	else 
		quotient <= quotient;
   end

   reg [7:0]     bits; 
   wire          ready = !bits;
   
   always @(posedge clk) 
     if(ready) begin
        bits = 64;
        qr = {32'd0,dividend, 32'd0}; // multiply freq by 2^32
     end else begin
        diff = qr[95:63] - {1'b0,divider};
        if(diff[32])
          qr = {qr[94:0],1'd0};      // negative so use qr shifted left, result = 0
        else
          qr = {diff[31:0],qr[62:0],1'd1}; // positive so use diff shifted left, result = 1
        bits = bits - 1'b1;
    end

endmodule

