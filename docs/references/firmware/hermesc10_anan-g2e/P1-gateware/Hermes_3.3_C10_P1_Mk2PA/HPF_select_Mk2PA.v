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
BPF design frequency ranges:

BPF1: 1.5 MHz through 2.5 MHz for 160M
BPF2: 2 MHz through 6 MHz for 80/60M
BPF3: 5 MHz tthrough 10 MHz for 40/30M
BPF4: 12 MHz through 24 MHz for 20/17M
BPF5: 20 MHz through 35 MHz for 15/12M
LNA:  21 MHz through 54 MHz for 10M/6M

2017 Mar 21 - changed BPF1 from 1.5 MHz to 1.8 MHz to allow more MW coverage

	
*/

module HPF_select(clock,frequency,HPF);
input  wire        clock;
input  wire [31:0] frequency;
output reg   [6:0] HPF;

always @(posedge clock)
begin
if 		(frequency <  1800000) 	HPF <= 7'b0100000;	// bypass
else if		(frequency <  2100000) 	HPF <= 7'b0010000;	// RX BPF1 160M	
else if 	(frequency <  5500000)	HPF <= 7'b0001000;	// RX BPF2 80M/60M
else if 	(frequency < 11000000)	HPF <= 7'b0000100;	// RX BPF3 40/30M
else if 	(frequency < 22000000)	HPF <= 7'b0000001;	// RX BPF4 20M/17M
else if 	(frequency < 35000000) 	HPF <= 7'b0000010; 	// RX BPF5 15/12M
else					HPF <= 7'b1000000;	// LNA, active above 35MHz

end
endmodule
