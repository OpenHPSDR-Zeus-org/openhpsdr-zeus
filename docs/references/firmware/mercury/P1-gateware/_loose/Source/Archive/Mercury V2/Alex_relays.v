//////////////////////////////////////////////////////////////
//
//		Alex Antenna relay selection
//
//		Antenna relays decode as follows
//
//		TX_relay[1:0]	Antenna selected
//			00			Tx 1
//			01			Tx 2
//			10			Tx 3
//
//		RX_relay[1:0]	Antenna selected
//			00			None
//			01			Rx 1
//			10			Rx 2
//			11			Transverter
//
//		Rout			Rx_1_out
//			0			Not selected
//			1			Selected
//
//////////////////////////////////////////////////////////////

module Alex_relays(TX_relay, RX_relay, Rout, ANT1, ANT2, ANT3, Rx_1_out, Rx_1_in, Rx_2_in, Transverter);

input [1:0]TX_relay;
input [1:0]RX_relay;
input  Rout;
output ANT1;
output ANT2;
output ANT3;
output Rx_1_out;
output Rx_1_in;
output Rx_2_in;
output Transverter;


assign Rx_1_out = Rout;

assign ANT1 = (TX_relay == 2'b00) ? 1'b1 : 1'b0;  		// select Tx antenna 1
assign ANT2 = (TX_relay == 2'b01) ? 1'b1 : 1'b0;		// select Tx antenna 2
assign ANT3 = (TX_relay == 2'b10) ? 1'b1 : 1'b0;		// select Tx antenna 3

assign Rx_1_in = (RX_relay == 2'b01) ? 1'b1 : 1'b0;		// select Rx antenna 1
assign Rx_2_in = (RX_relay == 2'b10) ? 1'b1 : 1'b0;		// select Rx antenna 2
assign Transverter = (RX_relay == 2'b11) ? 1'b1 : 1'b0;	// select Transverter input 

endmodule
