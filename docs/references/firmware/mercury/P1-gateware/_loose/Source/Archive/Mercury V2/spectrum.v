// Sends the spectrum data as an 16 bit value in I2S format to Ozy over Atlas A12.
//
// The data to send is held in a 4096 x 16 bit FIFO that is filled using the 122.88MHz clock
// The data from the FIFO is read using the LRCLK and BCLK.  The read does not take place if 
// a full flag from Ozy is set. The flag is set if EP4 on the FX2 is not available.
//
// Format is as follows: 
//
//  
//                 ---+   +---+   +---+   +---+   +---+   +---+   +---+
// BCKLK              +---+   +---+   +---+   +---+   +---+   +---+   +---+
//  
//          ----------+       
// LRCLK              +--------------------------------------------------------------
// 
//                            +-------+-------+-------+-------+------+
// spectrum -------------------   D15  +  D14  +  D13  +  D12  + D11  +  etc...  
//                            +-------+-------+-------+-------+------+
//
//                            <-------------Spectrum data ---------------


module spectrum(BCLK, LRCLK, full,rdreq, q, spectrum);

inout  BCLK;
input  LRCLK;
input  full;
output rdreq;
input  signed [15:0]q;
output spectrum;


reg [1:0]spec_send;
reg [3:0]spec_data_count;
reg rdreq;
reg spectrum;
reg [15:0]data;
//reg [15:0] test_counter;

always @ (posedge BCLK)
if (full) begin
	spec_send <= 0;   	// don't send data if Ozy spectrum FIFO is full
	//test_counter <= 0;
	end 
else

begin
case(spec_send)
0:  if (LRCLK)	 spec_send <= 1;		// loop until LRCLK is high
	else spec_send <= 0;
	
1:	if (LRCLK) spec_send <= 1;			// wait until it goes low - this is first BCLK after negedge of LRCLK
	else begin
		spec_data_count <= 15; 			// need to have data available for Ozy on next BCLK
		rdreq <= 1'b1;					// get data from FIFO output
		spec_send <= 2;
		data <= q;						// need to latch data here !!!!!
		//test_counter <= test_counter + 511;
	end
2:	begin
	rdreq <= 1'b0; 						// stop reading, we have a 16 bit sample, now serialise it
	if (spec_data_count == 0)begin

		spec_send <= 0;
	end
	else begin
		spec_data_count <= spec_data_count - 1'b1;
		spec_send <= 2;
	end
	end 
default: spec_send <= 0;
endcase
end 


// I2S data must be available on the 2nd positive edge of BCLK after the LRCLK transition
always @ (negedge BCLK)
begin
	spectrum <= data[spec_data_count];		// shift data out to Atlas bus MSB first
	//spectrum <= test_counter[spec_data_count];		// shift data out to Atlas bus MSB first

end

endmodule
