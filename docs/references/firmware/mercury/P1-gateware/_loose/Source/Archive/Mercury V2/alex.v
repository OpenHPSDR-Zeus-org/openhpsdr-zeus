// Determines which filters on the Alex board to select based on frequency.
// Also selects antenna inputs, TR relay and 6m preamp.


module alex(frequency_HZ, ATTEN, SPI_data, PTT_out, Tx_load_strobe, Rx_load_strobe, SPI_clock, clock);
                 
input  [31:0] frequency_HZ;
input  [1:0] ATTEN;
input  PTT_out;
output SPI_data;
output Tx_load_strobe;
output Rx_load_strobe;
output SPI_clock;
input  clock;               


wire [6:0]LPF;
wire [5:0]select_HPF;
wire [31:0]frequency_plus_IF; // add PowerSDR IF frequecy (9kHz) to current frequency

//	The frequency sent by PowerSDR is the indicated frequency
//  less the 9kHz IF. In order to select filters at the correct
//  frequency we need to add the IF offset to the current frequency.

assign frequency_plus_IF = frequency_HZ + 32'd9000; // add 9kHz IF offset 

// select the appropriate HPF and LPF based on frequency

LPF_select Alex_LPF_inst(
	.frequency(frequency_plus_IF), 
	.LPF(LPF)
	);
HPF_select Alex_HPF_inst(
	.frequency(frequency_plus_IF), 
	.HPF(select_HPF)
	);

//		Alex Antenna relay selection

wire ANT1;			
wire ANT2;
wire ANT3;
wire Rx_1_out;
wire Transverter;
wire Rx_2_in;
wire Rx_1_in;
wire [1:0]TX_relay;
wire [1:0]RX_relay;
wire Rout;

Alex_relays Alex_relays_inst(
	.TX_relay(TX_relay), 
	.RX_relay(RX_relay), 
	.Rout(Rout), 
	.ANT1(ANT1), 
	.ANT2(ANT2), 
	.ANT3(ANT3), 
	.Rx_1_out(Rx_1_out), 
	.Rx_1_in(Rx_1_in), 
	.Rx_2_in(Rx_2_in), 
	.Transverter(Transverter)
	);


//		Alex SPI interface

wire _6m_preamp;
wire Tx_yellow_led = 1'b1; 	// indicate we have some SPI data
wire Rx_yellow_led = 1'b1;	// ditto
wire Tx_red_led;
wire Rx_red_led;
wire TR_relay;
wire [15:0]Alex_Tx_data;
wire [15:0]Alex_Rx_data;

// assign attenuators
wire _10dB_atten = ATTEN[0];
wire _20dB_atten = ATTEN[1];

// define and concatinate the Tx data to send to Alex via SPI
assign Tx_red_led = PTT_out; // turn red led on when we Tx
assign TR_relay   = PTT_out; // turn on TR relay when PTT active

assign Alex_Tx_data = {LPF[6:4],Tx_red_led,TR_relay,ANT3,ANT2,ANT1,LPF[3:0],Tx_yellow_led,3'b000};

// define and concatinate the Rx data to send to Alex via SPI
assign Rx_red_led = PTT_out;	// turn red led on when we Tx

// turn 6m preamp on if frequency > 50MHz 
assign _6m_preamp = (frequency_plus_IF > 50000000) ? 1'b1 : 1'b0;

// if 6m preamp selected disconnect all filters 
wire [5:0] HPF;
assign HPF = _6m_preamp ? 6'd0 : select_HPF; 

// V3 Alex hardware
assign Alex_Rx_data = {Rx_red_led,_10dB_atten ,_20dB_atten, HPF[5], Rx_1_out,Rx_1_in,Rx_2_in,
                       Transverter, 1'b0, HPF[4:2],_6m_preamp,HPF[1:0],Rx_yellow_led};
					   
// concatinate Tx and Rx data and send to SPI interface. SPI interface only sends on a change of Alex_data.
// All data is sent in about 120uS.
wire [31:0] Alex_data;

assign Alex_data = {Alex_Tx_data[15:0],Alex_Rx_data[15:0]};

SPI Alex_SPI_Tx (
				.Alex_data(Alex_data), 
				.SPI_data(SPI_data),
                .SPI_clock(SPI_clock), 
                .Tx_load_strobe(Tx_load_strobe),
                .Rx_load_strobe(Rx_load_strobe),
                .spi_clock(clock)
                );	
                 
endmodule
                 											
													