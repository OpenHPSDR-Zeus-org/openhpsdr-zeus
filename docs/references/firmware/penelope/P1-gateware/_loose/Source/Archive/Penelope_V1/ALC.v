// V1.0 17th July 2007
//
// Copyright 2008 Kirk Weedman KD7IRS
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  ALC Module
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



// Fixed point signed multiplier. Multipier is assumed positive
// Result is a 16 bit integer. 
module ALC (out, sample, multiply);

output wire [15:0] out;
input       [15:0] sample;
input       [15:0] multiply;

wire [47:0]result;
wire [15:0] pos_sample;

assign pos_sample = sample[15] ? (~sample + 1'b1) : sample;  // convert to positive if negative 

assign result = pos_sample * multiply ; // B0 * B16 scalar  (single 16 by 16 bit multiply)

assign out = sample[15] ? ~result[31:16]+16'b1 : result[31:16];  // or result >> 16
 
endmodule
