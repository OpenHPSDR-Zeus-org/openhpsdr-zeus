// Copyright Phil Harman VK6APH and Bill Tracey  KD5TFD 2006, 2007 
// 
// Implements I2S Encoder, 24 bits, two channels.
// 
// 
// In I2S mode the MSB of the left sample is available on the second rising edge of BCLK
// following a falling  edge on LRCLK.  Simiarily the MSB of the right sample will be available on 
// the second rising edge of BCLK following the rising edge of LRCLK.

// NOTE: the data to send is latched on the positive edge of LRCLK

module I2SEncode(LRCLK, BCLK, left_sample, right_sample, outbit); 

input LRCLK;             		// left/right clock 
input BCLK;              		// bit clock 
// left and right samples are clocked into the module @ rising edge or LRCLK 
input [23:0] left_sample;     	// data to be clocked out 
input [23:0] right_sample;    	// right input to be clocked out 
output outbit;           		// output bit 

reg outbit;       				// current bit being clocked out 
reg [23:0] data; 				// data to be  clocked out 
reg [23:0] local_right_sample; 	// local copy of right sample 
reg [4:0] bit_count;     		// how many bits clocked, must preset to MSB 
reg [2:0]encode_state;

always @(posedge BCLK)
begin
case(encode_state)
0:	begin
	if (!LRCLK)encode_state <= 0;					// loop until LRCLK is high
	else begin
		encode_state <= 1;
		data <= left_sample;						// get local copy of data since
		local_right_sample <=  right_sample;		// it will change part way through otherwise
		end
	end
1:	if (LRCLK)encode_state <= 1;						// loop until LRCLK is low
	else begin
	bit_count <= 23;
	encode_state <= 2;
	end
2:	begin
		if (bit_count == 0)	encode_state <= 3; 
		else begin
		bit_count <= bit_count - 1'b1;
		encode_state <= 2;  
		end
	end
3:	if (!LRCLK)encode_state <= 3; 					// loop until LRCLK is high
	else begin
	bit_count <= 23;
	data <= local_right_sample;
	encode_state <= 4;
	end
4:	begin
		if (bit_count == 0)	encode_state <= 0; 		// done so start again
		else begin
		bit_count <= bit_count - 1'b1;
		encode_state <= 4;
		end					
	end 
default: encode_state <= 0;
endcase
end

// the I2S data must be available on the 2nd postive edge of bclk after the clrclk transition

always @ (negedge BCLK)
begin
	outbit <= data[bit_count];
end 


endmodule 