// V2.0 13th September 2014
//
// Copyright 2014 Phil Harman VK6PH
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



//---------------------------------------------------
//		Alex SPI interface
//---------------------------------------------------


/*
	data to send to Alex Rx filters is in the folowing format:
	
		Bit 	Function 		I.C. Output
	------ 	------------ 	-----------
	Bit 00 - YELLOW LED 		U2 - D0 		All are active "High"
	Bit 01 - 13 MHz HPF 		U2 - D1
	Bit 02 - 20 MHz HPF 		U2 - D2
	Bit 03 - 6M Preamp 		U2	- D3
	Bit 04 - 9.5 MHz HPF 	U2 - D4
	Bit 05 - 6.5 MHz HPF 	U2 - D5
	Bit 06 - 1.5 MHz HPF 	U2 - D6	
	Bit 07 - N.C. 				U2 - D7
	Bit 08 - XVTR RX In 		U3 - D0
	Bit 09 - RX 2 In 			U3 - D1
	Bit 10 - RX 1 In 			U3 - D2
	Bit 11 - RX 1 Out 		U3 - D3 		Low = Default Receive Path
	Bit 12 - Bypass 			U3 - D4
	Bit 13 - 20 dB Atten. 	U3 - D5
	Bit 14 - 10 dB Atten. 	U3 - D6
	Bit 15 - RED LED 			U3 - D7		
	
	
	data to send to Alex Tx filters is in the following format:

		Bit 	Function 		I.C. Output
	------ 	------------ 	-----------
	Bit 16 - N.C. 				U2 - D0 		
	Bit 17 - N.C. 				U2 - D1
	Bit 18 - N.C. 				U2 - D2
	Bit 19 - YELLOW LED 		U2 - D3
	Bit 20 - 30/20 Meters 	U2 - D4
	Bit 21 - 60/40 Meters 	U2 - D5
	Bit 22 - 80 Meters 		U2 - D6
	Bit 24 - 160 Meters 		U2 - D7
	Bit 24 - ANT #1 			U4 - D0
	Bit 25 - ANT #2 			U4 - D1
	Bit 26 - ANT #3 			U4 - D2
	Bit 27 - T/R Relay 		U4 - D3 		Transmit is high, Rec Low
	Bit 28 - RED LED 			U4 - D4
	Bit 29 - 6 Mtrs(Bypass) U4 - D5
	Bit 30 - 12/10 Meters 	U4 - D6
	Bit 31 - 17/15 Meters 	U4 - D7	
	
	Bit number refers to Alex_data[x]
	
	SPI data is sent to Alex whenever data changes.
	On reset all outputs are set off. 

*/

module SPI(
				input reset,
				input  spi_clock,
				input enable,
				input [31:0]Alex_data,
				output reg SPI_data,
				output reg SPI_clock,
				output reg Rx_load_strobe,
				output reg Tx_load_strobe
			);

reg [3:0] spi_state;
reg [4:0] data_count;
reg [31:0] previous_Alex_data; 
reg loop_count;					// used to send the Alex data word twice when the data word changes
 
always @ (posedge spi_clock)
begin
case (spi_state)
0:	begin
		if (reset | ( enable & (Alex_data != previous_Alex_data))) begin
			data_count <= 31;				// set starting bit count to 31
			spi_state <= 1;
		end
	end		
1:	begin
	if (reset) 
		SPI_data <= 1'b0;
	else 
		SPI_data <= Alex_data[data_count];	// set up data to send
	spi_state <= 2;
	end
2:	begin
	SPI_clock <= 1'b1;					// set clock high
	spi_state <= 3;
	end
3:	begin
	SPI_clock <= 1'b0;					// set clock low
	spi_state <= 4;
	end
4:	begin
		if (data_count == 16)begin		// transfer complete
			Tx_load_strobe <= 1'b1; 	// strobe Tx data
			spi_state <= 5;
		end
		else if(data_count == 0) begin
			Rx_load_strobe <= 1'b1;
			spi_state <= 6;
		end 
		else spi_state  <= 1;  			// go round again
	data_count <= data_count - 1'b1;
	end
5:	begin
	Tx_load_strobe <= 1'b0;				// reset Tx strobe
	spi_state <= 1;						// now do Rx data
	end
6:	begin
	Rx_load_strobe <= 1'b0;				// reset Rx strobe
	loop_count <= loop_count + 1'b1; // loop_count increments each time the SPI data word is sent after a word change is detected
	if (loop_count == 1'b1) begin			
				spi_state <= 1;			// send data word twice
		end
		else begin
			previous_Alex_data <= Alex_data; // save current data
			spi_state <= 0;						// reset for next run
		end
	end
	
endcase
end

endmodule
