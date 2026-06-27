// Copyright 2009  Kirk Weedman KD7IRS 

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
// 15 Feb 2009 - first version


`timescale 1 ns/100 ps

//------------------------------------------------------------------------------
//            Code Copyright (c) 2009 Kirk Weedman, KD7IRS
//------------------------------------------------------------------------------


module kordic (clock, phase_step, Xin, Yin, Xout, Yout);

parameter IN_WIDTH   = 16; // ADC bitwidth
parameter EXTRA_BITS = 6;  // spur reduction 6 dB per bit

localparam WI  = IN_WIDTH;
localparam WXY = IN_WIDTH + EXTRA_BITS; // 22-bit data regs
localparam STG = WXY;

input                   clock;
input  signed    [31:0] phase_step; // ratio of f0/fs.  Thus 0 to 2*PI will be represented as a 32 bit number from 0 to {32{1'b1}}
input  signed  [WI-1:0] Xin;
input  signed  [WI-1:0] Yin;
output signed [WXY-1:0] Xout;
output signed [WXY-1:0] Yout;



//------------------------------------------------------------------------------
//                             arctan table
//------------------------------------------------------------------------------

wire signed [31:0] atan_table [0:30];

//                      32'b01000000000000000000000000000000; // upper 2 bits = 2'b01 = 90 degrees
assign atan_table[00] = 32'b00100000000000000000000000000000; // 45.000 degrees -> atan(2^0)
assign atan_table[01] = 32'b00010010111001000000010100011101; // 26.565 degrees -> atan(2^-1)
assign atan_table[02] = 32'b00001001111110110011100001011011; // 14.036 degrees -> atan(2^-2)
assign atan_table[03] = 32'b00000101000100010001000111010100; // atan(2^-n)
assign atan_table[04] = 32'b00000010100010110000110101000011;
assign atan_table[05] = 32'b00000001010001011101011111100001;
assign atan_table[06] = 32'b00000000101000101111011000011110;
assign atan_table[07] = 32'b00000000010100010111110001010101;
assign atan_table[08] = 32'b00000000001010001011111001010011;
assign atan_table[09] = 32'b00000000000101000101111100101110;
assign atan_table[10] = 32'b00000000000010100010111110011000;
assign atan_table[11] = 32'b00000000000001010001011111001100;
assign atan_table[12] = 32'b00000000000000101000101111100110;
assign atan_table[13] = 32'b00000000000000010100010111110011;
assign atan_table[14] = 32'b00000000000000001010001011111001;
assign atan_table[15] = 32'b00000000000000000101000101111101;
assign atan_table[16] = 32'b00000000000000000010100010111110;
assign atan_table[17] = 32'b00000000000000000001010001011111;
assign atan_table[18] = 32'b00000000000000000000101000101111;
assign atan_table[19] = 32'b00000000000000000000010100011000;
assign atan_table[20] = 32'b00000000000000000000001010001100;
assign atan_table[21] = 32'b00000000000000000000000101000110;
assign atan_table[22] = 32'b00000000000000000000000010100011;
assign atan_table[23] = 32'b00000000000000000000000001010001;
assign atan_table[24] = 32'b00000000000000000000000000101000;
assign atan_table[25] = 32'b00000000000000000000000000010100;
assign atan_table[26] = 32'b00000000000000000000000000001010;
assign atan_table[27] = 32'b00000000000000000000000000000101;
assign atan_table[28] = 32'b00000000000000000000000000000010;
assign atan_table[29] = 32'b00000000000000000000000000000001;
assign atan_table[30] = 32'b00000000000000000000000000000000;


//------------------------------------------------------------------------------
//                              registers
//------------------------------------------------------------------------------

//stage outputs
reg signed [WXY-1:0] X [0:STG-1];
reg signed [WXY-1:0] Y [0:STG-1];
reg signed    [31:0] Z [0:STG-1]; // 32bit
// NCO
reg           [31:0] phase_acc;


//------------------------------------------------------------------------------
//                               stage 0
//------------------------------------------------------------------------------
wire [1:0] quadrant = phase_acc[31:30];
wire  signed  [WI-1:0] NXin;
wire  signed  [WI-1:0] NYin;

assign NXin = -Xin;
assign NYin = -Yin;

always @(posedge clock)
begin // make sure the rotation angle is in the -pi/2 to pi/2 range.  If not then pre-rotate
  case (quadrant)
  2'b00,
  2'b11: // no pre-rotation needed for these quadrants
  begin
    X[0] <= {Xin[WI-1], Xin} << (EXTRA_BITS-1); // since An = 1.647, divide input by 2 and then multiply by 2^EXTRA_BITS
    Y[0] <= {Yin[WI-1], Yin} << (EXTRA_BITS-1);
    Z[0] <= phase_acc;
  end

  2'b01:
  begin
    X[0] <= {NYin[WI-1], NYin} << (EXTRA_BITS-1);
    Y[0] <= { Xin[WI-1],  Xin} << (EXTRA_BITS-1);
    Z[0] <= {2'b00,phase_acc[29:0]}; // subtract pi/2 from phase_acc for this quadrant
  end

  2'b10:
  begin
    X[0] <= { Yin[WI-1],  Yin} << (EXTRA_BITS-1);
    Y[0] <= {NXin[WI-1], NXin} << (EXTRA_BITS-1);
    Z[0] <= {2'b11,phase_acc[29:0]}; // add pi/2 to phase_acc for this quadrant
  end
  endcase

  //advance NCO
  phase_acc <= phase_acc + phase_step;
end


//------------------------------------------------------------------------------
//                           stages 1 to STG-1
//------------------------------------------------------------------------------
genvar i;

generate
  for (i=0; i < (STG-1); i=i+1)
  begin: XYZ
    wire Z_sign;
    wire signed [WXY-1:0] X_shr, Y_shr; 

    assign X_shr = X[i] >>> i; // signed shift right
    assign Y_shr = Y[i] >>> i;

    //the sign of the current rotation angle
    assign Z_sign = Z[i][31]; // Z_sign = 1 if Z[i] < 0

    always @(posedge clock)
    begin
      // add/subtract shifted data
      X[i+1] <= Z_sign ? X[i] + Y_shr         : X[i] - Y_shr;
      Y[i+1] <= Z_sign ? Y[i] - X_shr         : Y[i] + X_shr;
      Z[i+1] <= Z_sign ? Z[i] + atan_table[i] : Z[i] - atan_table[i];
    end
  end
endgenerate


//------------------------------------------------------------------------------
//                                 output
//------------------------------------------------------------------------------
assign Xout = X[STG-1];
assign Yout = Y[STG-1];

endmodule
