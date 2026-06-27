// Sends the software serial number as an 8 bit value in I2S format to Ozy over Atlas C19.
//
// C19 is normally high Z so that other Atlas bus cards can share the same bus.
// Each card sends is serial number at a specific count in the I2S sequence. 
// Mercury sends its serial number in bits 7 to 0 of 15, MSB first
// Penny uses bits 15 to 8.
//
// Format is as follows: 

//  
//                 ---+   +---+   +---+   +---+   +---+   +---+   +---+
// BCKLK              +---+   +---+   +---+   +---+   +---+   +---+   +---+
//  
//          ----------+       
// LRCLK              +--------------------------------------------------------------
// 
//                            +-------+-------+-------+-------+------+
// serno   -------------------   D15  +  D14  +  D13  +  D12  + D11  +  etc...  
//                            +-------+-------+-------+-------+------+
//
//                            <-------------Penny serial number ---------------


module software_serial(BCLK,LRCLK,SERIAL,serno);

input  BCLK;
input  LRCLK;
input  [7:0]SERIAL;
output reg serno;

// done to here ********

reg [1:0]sno_send;
reg [3:0]sno_data_count;

always @ (posedge BCLK)
begin	
case(sno_send)
0:  if (LRCLK)	 sno_send <= 1;		// loop until LRCLK is high
	else sno_send <= 0;
1:	if (LRCLK) sno_send <= 1;		// wait until it goes low - this is first BCLK after negedge of LRCLK
	else begin
		sno_data_count <= 15; 		// need to have data available for Ozy on next BCLK
		sno_send <= 2;
	end
2:	begin
	if (sno_data_count == 0)begin
		sno_send <= 0;
	end
	else begin
		sno_data_count <= sno_data_count - 1'b1;
		sno_send <= 2;
	end
	end 
default: sno_send <= 0;
endcase
end 

reg serial_number;
// I2S data must be available on the 2nd positive edge of CBCLK after the CLRCLK transition
always @ (negedge BCLK)
begin
	if (sno_send == 2 && sno_data_count < 8)
		serno <= SERIAL[sno_data_count];			// shift data out to Atlas bus MSB first
	else serno <= 1'bz;								// serial # bus is high Z
end

//assign serno = (sno_send == 2 && sno_data_count < 8) ? serial_number : 1'bz;  // serial # bus is high Z

endmodule

