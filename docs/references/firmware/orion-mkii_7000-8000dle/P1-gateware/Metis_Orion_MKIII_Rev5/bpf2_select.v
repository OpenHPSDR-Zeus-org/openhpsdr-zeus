// V1.0 19th November 2007
//
// Copyright 2006,2007 Phil Harman VK6APH
//
//  HPSDR - High Performance Software Defined Radio
//
//  Alex SPI interface.
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

//////////////////////////////////////////////////////////////
//
//		Alex Band Decoder & HPF selection
//
//////////////////////////////////////////////////////////////
/*
BPF design ranges:

BPF1: 1.5 MHz to 2.5 MHz for 160M
BPF2: 2 MHz to 6 MHz for 80/60M
BPF3: 5 MHz to 10 MHz for 40/30M
BPF4: 12 MHz to 24 MHz for 20/17M/15M
BPF5: 20 MHz to 35 MHz for 12/10M
LNA:  21 MHz to 54 MHz 
	
*/

module BPF2_select(clock,frequency,BPF2);
input  wire        clock;
input  wire [31:0] frequency;
output reg   [6:0] BPF2;

always @(posedge clock)
begin
if 		(frequency <  1500000) 	BPF2 <= 7'b0100000;	// bypass
else if		(frequency <  2100000) 	BPF2 <= 7'b0010000;	// RX BPF1 160M	
else if 	(frequency <  5500000)	BPF2 <= 7'b0001000;	// RX BPF2 80M/60M
else if 	(frequency < 11000000)	BPF2 <= 7'b0000100;	// RX BPF3 40/30M
else if 	(frequency < 21000000)	BPF2 <= 7'b0000001;	// RX BPF4 20M/17M
else if 	(frequency < 35000000) 	BPF2 <= 7'b0000010; 	// RX BPF5 15/12M
else  					BPF2 <= 7'b1000000;	// LNA, active above 35MHz

end
endmodule
