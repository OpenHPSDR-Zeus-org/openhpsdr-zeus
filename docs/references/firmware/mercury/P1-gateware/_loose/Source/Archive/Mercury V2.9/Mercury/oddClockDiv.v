//
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

module oddClockDivider(clk_i, clk_o); 
input  clk_i; 
output clk_o; 

wire clk_i; 
reg  clk_o; 


parameter DIVIDE_RATE   = 125; 
parameter COUNTER_WIDTH = 7; 

reg [(COUNTER_WIDTH-1):0] count; 

always @ (posedge clk_i)
begin

  if ( count == (DIVIDE_RATE-1))
    count <= 0; 
  else
    count <= count + 1'b1; 
    
  clk_o <= (count < 63) ? 1'b0 : 1'b1;
end 

endmodule