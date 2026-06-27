// V1.0 17th July 2007
//
// Copyright 2007 Phil Harman VK6APH
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  ADC module for driving ADC78H90 
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
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA// SCI inteface to ADC78H90 

//  Penelope uses inputs AIN1 to AIN5
//  SCLK can run at 8MHz max and is = clock/4 
//  Note: AINx data can be latched using nCS

//  for now just use AIN5 the Forward Power sensor value

module ADC(clock, SCLK, nCS, MISO, MOSI, AIN5);

input  wire       clock;
output reg        SCLK;
output reg        nCS;
input  wire       MISO;
output reg        MOSI;
output reg [11:0] AIN5; // reg holds VFWD volts

wire [15:0] data;
reg   [2:0] ADC_state;
reg   [3:0] bit_cnt;
reg  [11:0] temp_AIN5;	// 

assign data = 16'b0010_0000_0000_0000; // address of AIN5

// NOTE: this code generates the SCLK clock for the ADC
always @ (posedge clock)
begin
  case (ADC_state)
  0:
	begin
    nCS       <= 1'b1;          // set nCS high
    bit_cnt   <= 4'd15;         // set bit counter
    ADC_state <= 1;
	end
	
  1:
	begin
    nCS       <= 0;             // select ADC
    MOSI      <= data[bit_cnt];	// shift data out to MOSI
    ADC_state <= 2;
	end
	
  2:
	begin
    SCLK      <= 1'b1;          // SCLK high
    ADC_state <= 3;
	end
	
  3:
	begin
    SCLK      <= 1'b0;          // SCLK low
    ADC_state <= 4;
	end

  4:
	begin
    if (bit_cnt == 0)           // restart ? - will change address here later on
      ADC_state <= 0;
    else
    begin
      bit_cnt   <= bit_cnt - 1'b1; // do all again
      ADC_state <= 1;
    end
	end 
	
  default:
    ADC_state <= 0;
  endcase
end 

always @ (posedge clock)
begin
  if (ADC_state == 0)
    AIN5 <= temp_AIN5;			    // latch data when not shifting
 
  if (SCLK && (bit_cnt <= 11))  // start capturing data at bit counter = 11
    temp_AIN5[bit_cnt] <= MISO; // capture incoming data
end 

	
	
endmodule 

