/*

Format changed to match Protocol Discussion paper 5 Dec 2014


 This is priority C&C data from the hardware. It is sent whenever data changes and every **mS	

			The format is as follows:
		 RAM Byte Data
				0	Seq #							[31:24]
				1	Seq #							[23:16]
				2	Seq #							[15:8]
				3	Seq #							[7:0]
			0	4	Bits - PTT, Dot, Dash	[0] = PTT, [1] = Dot, [2] = Dash, [3] = new_frequency, [4] = locked_10MHz
			1	5	Bits - ADC Overload		[0] = ADC0…[7] = ADC7
			2	6	Exciter Power 0			[15:8]
			3	7	Exciter Power 0			[7:0]
			4	8	Exciter Power 1			[15:8]
			5	9	Exciter Power 1			[7:0]
			6	10	Exciter Power 2			[15:8]
			7	11	Exciter Power 2			[7:0]
			8	12	Exciter Power 3			[15:8]
			9	13	Exciter Power 3			[7:0]
			10	14	Forward Power - Alex 0 	[15:8]  (Set to zero if Alex not selected)
			11	15	Forward Power - Alex 0 	[7:0]   (Set to zero if Alex not selected)
			12	16	Forward Power - Alex 1	[15:8]
			13	17	Forward Power - Alex 1	[7:0]
			14	18	Forward Power - Alex 2	[15:8]
			15	19	Forward Power - Alex 2	[7:0]
			16	20	Forward Power - Alex 3	[15:8]
			17	21	Forward Power - Alex 3	[7:0]
			18	22	Reverse Power - Alex 0 	[15:8]  (Set to zero if Alex not selected)
			19	23	Reverse Power - Alex 0 	[7:0]   (Set to zero if Alex not selected)
			20	24	Reverse Power - Alex 1	[15:8]
			21	25	Reverse Power - Alex 1	[7:0]
			22	26	Reverse Power - Alex 2	[15:8]
			23	27	Reverse Power - Alex 2	[7:0]
			24	28	Reverse Power - Alex 3	[15:8]
			25	29	Reverse Power - Alex 3	[7:0]
			
			RAM 26 to 44 and Bytes 30 to 48 currently not used.

			45	49	Supply Volts				[15:8]
			46	50	Supply Volts				[7:0]
			47	51	User ADC3					[15:8]
			48	52	User ADC3					[7:0]
			49	53	User ADC2					[15:8]
			50	54	User ADC2					[7:0]
			51	55	User ADC1					[15:8]
			52	56	User ADC1					[7:0]
			53	57	User ADC0					[15:8]
			54	58	User ADC0					[7:0]
			55	59	Bits - User logic in		[0] = IO0….[7] = IO7


2018  Mar 28 - Added FPGA_PTT input, if set then update rate = 1mS so the FW & REV data is 
				   received by Thetis fast enough to calculate SWR. 
							
*/

module CC_encoder (
							input clock,					// tx_clock  125MHz
							input ACK,
							input PTT,
							input Dot,
							input Dash,
							input frequency_change[0:NR-1], 
							input locked_10MHz,
							input ADC0_overload,
							input ADC1_overload,
							input [15:0]Exciter_power,
							input [15:0]FWD_power,
							input [15:0]REV_power,
							input [15:0]Supply_volts,
							input [15:0]User_ADC1,
							input [15:0]User_ADC2,
							input [7:0] User_IO,
							input pk_detect_ack,		// from Orion_ADC
							input FPGA_PTT,
							input [15:0] Debug_data,
						
							output reg [7:0] CC_data[0:55],
							output reg ready,
							output reg pk_detect_reset // to Orion_ADC 
							
							);
							
parameter update_rate = 200; 					// number of mS between updates if no change in data	
parameter NR;

// move all C&C data to tx_clock domain

reg [7:0] memory[0:56];    // 57 by 8 bit ram
reg [7:0] temp[0:56];
reg [7:0] previous[0:2];
reg 		 new_frequency;
reg [$clog2(NR)-1:0] x;

// initial clear of all RAM
reg [6:0] t;
initial
for (t = 0; t < 57; t++)
begin 
	memory[t] = 8'd0;
	temp[t]   = 8'd0;
end
	

always @ (posedge clock)
begin 

	begin 
			if (frequency_change[0] || frequency_change[1] ) 		// this is from Rx clock domain!!
				new_frequency <= 1'b1;
			else new_frequency <= 0;	
	end

	{memory[0],    temp[0]}  <=  {temp[0],  3'b0, locked_10MHz, new_frequency, Dash, Dot, PTT};  // sent in real time
	{memory[1],    temp[1]}  <=  {temp[1],  6'b0, ADC1_overload, ADC0_overload};
	{memory[2],    temp[2]}  <=  {temp[2],  Exciter_power[15:8]};
	{memory[3],    temp[3]}	 <=  {temp[3],  Exciter_power[7:0]};		
	{memory[10],  temp[10]}  <= {temp[10],  FWD_power[15:8]};
	{memory[11],  temp[11]}  <= {temp[11],  FWD_power[7:0]};
	{memory[18],  temp[18]}  <= {temp[18],  REV_power[15:8]};
	{memory[19],  temp[19]}  <= {temp[19],  REV_power[7:0]};	
	
	// RAM 20 - 44 not presently used - intialised to zero
	{memory[26],  temp[26]}  <= {temp[26],  Debug_data[15:8]};
	{memory[27],  temp[27]}  <= {temp[27],  Debug_data[7:0]};
	
	{memory[45],  temp[45]}  <= {temp[45],  Supply_volts[15:8]}; 								
	{memory[46],  temp[46]}  <= {temp[46],  Supply_volts[7:0]}; 
	
	// RAM 47 - 50 not presently used - intialised to zero
	{memory[51],  temp[51]}  <= {temp[51], User_ADC2[15:8]};
	{memory[52],  temp[52]}  <= {temp[52], User_ADC2[7:0]};
	{memory[53],  temp[53]}  <= {temp[53], User_ADC1[15:8]};		// User_ADC1 input 
	{memory[54],  temp[54]}  <= {temp[54], User_ADC1[7:0]};							
	{memory[55],  temp[55]}  <= {temp[55],  User_IO};				// sent in real time
				   memory[56]   <= 8'd0;  									// spare
end	
	
// set ready flag when C&C data changes. Clear flag when ACK received


reg [2:0]  state = 0;
reg [5:0]  count = 0;
reg [24:0] counter = 0;
reg [7:0]  rate;

assign rate = FPGA_PTT ? 8'd1 : update_rate;   // if Txing then use 1mS update rate

always @ (posedge clock)	
begin
	if (counter >= (rate * 125000)) begin	
		counter <= 25'd0;
		if (state == 0) state <= 1;	  // no need to send data if its already in progress	
	end
	else counter <= counter + 25'd1;

	case(state)	
	0: begin 
			// check for inputs that we report in real time
			if ((memory[0] != previous[0]) || (memory[55] != previous[1])) begin 
				CC_data[0]  <= memory[0];
				CC_data[55] <= memory[55];
				previous[0] <= memory[0];
				previous[1] <= memory[55];
				counter <= 0;							// no need to send a periodic update							
				state <= 1;
			end 				
			else  	
				for (count = 0; count < 6'd56; count = count + 6'd1) // update all status (56 bytes)
				CC_data[count] <= memory[count];
		end 
		
	1: begin 
			ready <= 1'b1;
			if (ACK) begin 
				counter <= 25'd0;
				count <= 6'b0;
				ready <= 1'b0;
				pk_detect_reset <= 1'b1;		// signal Orion_ADC to begin new pk detect interval
				state <= 0;
			end
			if (pk_detect_ack) pk_detect_reset <= 1'b0;  // clear pk detect reset once Orion_ADC receives interval reset signal
		end 

default: state <= 0;
endcase 

end



endmodule
												
