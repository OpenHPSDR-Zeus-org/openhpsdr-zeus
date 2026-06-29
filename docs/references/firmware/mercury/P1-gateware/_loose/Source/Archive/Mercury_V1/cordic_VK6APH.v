/*
 * Spectrum and Network Analyzer
 * Copyright 2005-2007 Darrell Harmon
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
 */

// Modified by Phil Harman, VK6APH, to take complex input. 


/*
 *	  Basic CORDIC function
 *
 *    if phase < target 
 *
 *     P(n) = P(n-1) + arctan(1/2^n)
 *	
 *	  (sin)   I(n) = I(n-1) - Q(n-1)/2^n
 *    (cos)   Q(n) = Q(n-1) + I(n-1)/2^n
 *
 *    if phase > target 
 *
 *     P(n) = P(n-1) - arctan(1/2^n)
 *
 *	  (sin)   I(n) = I(n-1) + Q(n-1)/2^n
 *    (cos)   Q(n) = Q(n-1) - I(n-1)/2^n
 *    
 */

/*
 * CORDIC Rotator
 * 16 bit complex input, 18 bit I/Q output, gain = 3.2934
 * 20 bit phase input, scale: pi radians = 2^19
 * SFDR appears to be > 105 dB
 * Can be used for sin/cos or vector rotation
 * Fully pipelined, latency of 17 cycles
 *
 */

module cordic_VK6APH(i_in, q_in, iout, qout, ain, clk);
   input [19:0]    ain; // Angle 
   input [15:0]    i_in, q_in; // I&Q inputs to CORDIC rotator
   output [17:0]   iout, qout; // I/Q output
   reg [17:0]      iout, qout;
   input           clk;
   reg [18:0]     a0;
   reg [17:0]     a1;
   reg [17:0]     a2;
   reg [16:0]     a3;
   reg [15:0]     a4;
   reg [14:0]     a5;
   reg [13:0]     a6;
   reg [12:0]     a7;
   reg [11:0]     a8;
   reg [10:0]     a9;
   reg [9:0]      a10;
   reg [8:0]      a11;
   reg [7:0]      a12;
   reg [6:0]      a13;
   reg [5:0]      a14;
   reg [4:0]      a15;
   reg [3:0]      a16;
   reg [19:0]     i0,i1, i2, i3, i4, i5, i6, i7, i8, i9;
   reg [19:0]     i10, i11, i12, i13, i14, i15, i16, i17;
   reg [19:0]     q0, q1, q2, q3, q4, q5, q6, q7, q8, q9;
   reg [19:0]     q10, q11, q12, q13, q14, q15, q16, q17;

   /*
    * This is the cordic pipeline
    * The constants are calcuated as follows:  Using base 2^17 then 
    * arctan 1 = 45 degrees = 2^17 = d131072
    * arctan 1/2 = 26.565 degrees = 2^17 * 26.565/45 = d77376
    * arctan 1/4 = 14.036 degrees = 2^17 * 14.036/45 = d40884
    * arctan 1/8 =  7.125 degrees = 2^17 *  7.125/45 = d20753 etc
   */

   always @ (posedge clk)
     begin
		a0 <= ain[18:0]; 
		// Top two bits give quadrant	
		case (ain[19:18])
		2'b00,2'b11: begin
					i0 <=  {i_in[15],i_in,3'd0};
					q0 <= -{q_in[15],q_in,3'd0};
					end
		2'b01,2'b10: begin
					i0 <= -{i_in[15],i_in,3'd0};
					q0 <=  {q_in[15],q_in,3'd0};
					end
		endcase

        // 45 degrees
		a1 <= a0[18] ? a0[17:0] + 18'd131072 : a0[17:0] - 18'd131072;
		i1 <= a0[18] ? i0 + q0 : i0 - q0;
		q1 <= a0[18] ? q0 - i0 : q0 + i0;

        /* 26.565 degrees */
        a2 <= a1[17] ? a1 + 18'd77376 : a1 - 18'd77376;
        i2 <= a1[17] ? i1 + {q1[19], q1[19:1]}: i1 - {q1[19], q1[19:1]}; 
        q2 <= a1[17] ? q1 - {i1[19], i1[19:1]}: q1 + {i1[19], i1[19:1]};

        /* 14.036 degrees */
        a3 <= a2[17] ? a2[16:0] + 17'd40884 : a2[16:0] - 17'd40884;
        i3 <= a2[17] ? i2 + {{2{q2[19]}}, q2[19:2]}: i2 - {{2{q2[19]}}, q2[19:2]};
        q3 <= a2[17] ? q2 - {{2{i2[19]}}, i2[19:2]}: q2 + {{2{i2[19]}}, i2[19:2]};

        /* 7.125 degrees */
        a4 <= a3[16] ? a3[15:0] + 16'd20753 : a3[15:0] - 16'd20753;
        i4 <= a3[16] ? i3 + {{3{q3[19]}}, q3[19:3]}: i3 - {{3{q3[19]}}, q3[19:3]};
        q4 <= a3[16] ? q3 - {{3{i3[19]}}, i3[19:3]}: q3 + {{3{i3[19]}}, i3[19:3]};

        /* 3.576 degrees */
        a5 <= a4[15] ? a4[14:0] + 15'd10417 : a4[14:0] - 15'd10417;
        i5 <= a4[15] ? i4 + {{4{q4[19]}}, q4[19:4]}: i4 - {{4{q4[19]}}, q4[19:4]};
        q5 <= a4[15] ? q4 - {{4{i4[19]}}, i4[19:4]}: q4 + {{4{i4[19]}}, i4[19:4]};
 
        /* 1.790 degrees */
        a6 <= a5[14] ? a5[13:0] + 14'd5213 : a5[13:0] - 14'd5213;
        i6 <= a5[14] ? i5 + {{5{q5[19]}}, q5[19:5]}: i5 - {{5{q5[19]}}, q5[19:5]};
        q6 <= a5[14] ? q5 - {{5{i5[19]}}, i5[19:5]}: q5 + {{5{i5[19]}}, i5[19:5]};

        /* 0.895 degrees */
        a7 <= a6[13] ? a6[12:0] + 13'd2607 : a6[12:0] - 13'd2607;
        i7 <= a6[13] ? i6 + {{6{q6[19]}}, q6[19:6]}: i6 - {{6{q6[19]}}, q6[19:6]};
        q7 <= a6[13] ? q6 - {{6{i6[19]}}, i6[19:6]}: q6 + {{6{i6[19]}}, i6[19:6]};

        /* 0.448 degrees */
        a8 <= a7[12] ? a7[11:0] + 12'd1304 : a7[11:0] - 12'd1304;
        i8 <= a7[12] ? i7 + {{7{q7[19]}}, q7[19:7]}: i7 - {{7{q7[19]}}, q7[19:7]};
        q8 <= a7[12] ? q7 - {{7{i7[19]}}, i7[19:7]}: q7 + {{7{i7[19]}}, i7[19:7]};

        /* 0.224 degrees */
        a9 <= a8[11] ? a8[10:0] + 11'd652 : a8[10:0] - 11'd652;
        i9 <= a8[11] ? i8 + {{8{q8[19]}}, q8[19:8]}: i8 - {{8{q8[19]}}, q8[19:8]};
        q9 <= a8[11] ? q8 - {{8{i8[19]}}, i8[19:8]}: q8 + {{8{i8[19]}}, i8[19:8]};

        /* 0.1119 degrees */
        a10 <= a9[10] ? a9[9:0] + 10'd326 : a9[9:0] - 10'd326;
        i10 <= a9[10] ? i9 + {{9{q9[19]}}, q9[19:9]}: i9 - {{9{q9[19]}}, q9[19:9]};
        q10 <= a9[10] ? q9 - {{9{i9[19]}}, i9[19:9]}: q9 + {{9{i9[19]}}, i9[19:9]};

        /* 0.05595 degrees */
        a11 <= a10[9] ? a10[8:0] + 9'd163 : a10[8:0] - 9'd163;
        i11 <= a10[9] ? i10 + {{10{q10[19]}}, q10[19:10]}: i10 - {{10{q10[19]}}, q10[19:10]};
        q11 <= a10[9] ? q10 - {{10{i10[19]}}, i10[19:10]}: q10 + {{10{i10[19]}}, i10[19:10]};

        /* 0.02798 degrees */
        a12 <= a11[8] ? a11[7:0] + 8'd81 : a11[7:0] - 8'd81;
        i12 <= a11[8] ? i11 + {{11{q11[19]}}, q11[19:11]}: i11 - {{11{q11[19]}}, q11[19:11]};
        q12 <= a11[8] ? q11 - {{11{i11[19]}}, i11[19:11]}: q11 + {{11{i11[19]}}, i11[19:11]};

        /* 0.01399 degrees */
        a13 <= a12[7] ? a12[6:0] + 7'd41 : a12[6:0] - 7'd41;
        i13 <= a12[7] ? i12 + {{12{q12[19]}}, q12[19:12]}: i12 - {{12{q12[19]}}, q12[19:12]};
        q13 <= a12[7] ? q12 - {{12{i12[19]}}, i12[19:12]}: q12 + {{12{i12[19]}}, i12[19:12]};

        /* 0.00699 degrees */
        a14 <= a13[6] ? a13[5:0] + 6'd20 : a13[5:0] - 6'd20;
        i14 <= a13[6] ? i13 + {{13{q13[19]}}, q13[19:13]}: i13 - {{13{q13[19]}}, q13[19:13]};
        q14 <= a13[6] ? q13 - {{13{i13[19]}}, i13[19:13]}: q13 + {{13{i13[19]}}, i13[19:13]};

        /* 0.00350 degrees */
        a15 <= a14[5] ? a14[4:0] + 5'd10 : a14[4:0] - 5'd10;
        i15 <= a14[5] ? i14 + {{14{q14[19]}}, q14[19:14]}: i14 - {{14{q14[19]}}, q14[19:14]};
        q15 <= a14[5] ? q14 - {{14{i14[19]}}, i14[19:14]}: q14 + {{14{i14[19]}}, i14[19:14]};

        /* 0.00175 degrees */
        a16 <= a15[4] ? (a15[3:0] + 4'd5) : (a15[3:0] - 4'd5);
		i16 <= a15[4] ? i15 + {{15{q15[19]}}, q15[19:15]}: i15 - {{15{q15[19]}}, q15[19:15]};
        q16 <= a15[4] ? q15 - {{15{i15[19]}}, i15[19:15]}: q15 + {{15{i15[19]}}, i15[19:15]};

        /* 0.00087 degrees */
		i17 <= a16[3] ? i16 + {{16{q16[19]}}, q16[19:16]}: i16 - {{16{q16[19]}}, q16[19:16]};
        q17 <= a16[3] ? q16 - {{16{i16[19]}}, i16[19:16]}: q16 + {{16{i16[19]}}, i16[19:16]};

        /* Output */
        iout[17:0] <= (i17[1:0] > 2) ? i17[19:2] + 18'd1 : i17[19:2];
        qout[17:0] <= (q17[1:0] > 2) ? q17[19:2] + 18'd1 : q17[19:2];  // was (i17[1:0] !!



     end // always @ (posedge clk)
endmodule // cordic_16


