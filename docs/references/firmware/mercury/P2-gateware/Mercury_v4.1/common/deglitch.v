/////////////////////////////////////////////////////////////////////
////                                                             ////
//// Copyright (C) 2006 Phil Harman VK6APH                     	 ////
////                    pvharman@arach.net.au                 	 ////
//
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published
//  by the Free Software Foundation; version 1 
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
//  This design is LGPL software written for the HPSDR project
//  by Phil Harman, VK6APH.  The author assumes no libability on how the
//  code might be used or warranty that the code is fit for any 
//  particular application. 
//
//  The purpose of the code is to remove any glitches on an input signal. 
// 
//  Input has to be stable for 4 consecutive clock samples before a transition is considered valid
//

module  deglitch(clock, in, out);

input wire clock;
input wire in;
output reg out;

reg in_n1, in_n2, in_n3;

//
// de-glitch input using clock
// input has to be stable for 4 consecutive clock  samples before a transition is considered valid
//

always @ (posedge clock)
begin
	if (in & in_n1 & in_n2 & in_n3) out <= 1; 			// all have to be 1 before valid 
	else if (~in & ~in_n1 & ~in_n2 & ~in_n3) out <=0;	// all have to be 0 before valid
	else out <= out;								    			// else do nothing
	in_n1 <= in;										 			// shift previous samples along 
	in_n2 <= in_n1;
	in_n3 <= in_n2;	
end 

endmodule