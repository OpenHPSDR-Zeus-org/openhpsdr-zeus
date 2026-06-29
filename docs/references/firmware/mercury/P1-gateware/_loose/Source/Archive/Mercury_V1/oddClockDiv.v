//
// Copyright 2007 Bill Tracey, bill@ewjt.com, KD5TFD 
//	
//  HPSDR - High Performance Software Defined Radio
//
//  Clock dividecr 
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
// adapted from http://www.edn.com/archives/1997/081597/17di_01.htm#Listing%201
// 
//

// module oddClockDivider(clk_i, clk_o, q1, q2); 
module oddClockDivider(clk_i, clk_o); 
input clk_i; 
output clk_o; 
// output q1;
// output q2;

wire clk_i; 
wire clk_o; 
// wire q1; 
// wire q2; 


parameter DIVIDE_RATE = 125; 
parameter COUNTER_WIDTH=7; 

reg [(COUNTER_WIDTH-1):0] count; 

always @ (posedge clk_i) begin

	case ( count )
		DIVIDE_RATE-1: count <= 0; 
		default: count <= count + 1; 
	endcase 				
end 


wire en_tff1; 
wire en_tff2; 
reg div1;
reg div2; 

assign en_tff1 = ( count == 0 ? 1 : 0 ) ; 
assign en_tff2 = ( count ==  (((DIVIDE_RATE-1)/2)+1) ? 1 : 0 ); 


// 
always @ (posedge clk_i) begin
	if ( en_tff1 == 1 ) div1 <= ~div1; 
end 

always @ (negedge clk_i) begin 
	if ( en_tff2 == 1 ) div2 <= ~div2; 
end 

assign clk_o = div1 ^ div2;
// assign q1 = div1; 
// assign q2 = div2; 

endmodule 