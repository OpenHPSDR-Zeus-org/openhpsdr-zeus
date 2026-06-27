/***********************************************************
*
*	Mercury DDC receiver 
*
************************************************************/

// V1.0 23 December 2008 

// (C) Phil Harman VK6APH 2006,2007,2008


//  HPSDR - High Performance Software Defined Radio
//
//  Mercury to Atlas bus interface.
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


/* 	This program interfaces the LT2208 to a PC over USB.
	The data from the LT2208 is in 16 bit parallel format and 
	is valid at the positive edge of the LT2208 122.88MHz clock.
	
	The data is processed by a CORDIC NCO to produce I and Q
	outputs.  These are decimated by 320 in CIC filters then by 2 
	in an  CFIR filter to give output data at 192kHz to feed
	the Atlas bus in I2S format for tranfer via Ozy to a suitable PC program.
	
	Change log:
	
	
	1 Dec 2008 - First production release 
	 
*/

//////////////////////////////////////////////////////////////
//
//              Quartus V8.1  Notes
//
//////////////////////////////////////////////////////////////

/*
	In order to get this code to compile without timing errors under
	Quartus  V8.1 I needed to use the following settings:
	
	- Timing Analysis Settings - Use Classic 
	- Analysis and Synthesis Settings\Power Up Dont Care [not checked]
	- Analysis and Synthesis Settings\Restructure Multiplexers  [OFF]
	- Fitter Settings\Optimise fast-corner timing [ON]
	
*/

	
module Mercury(

input   OSC_10MHZ,			// 10MHz TCXO input 
inout   ext_10MHZ, 			// 10MHz reference to/from Atlas pin C16
input   CLKA,				// 122.88MHz clock from LT2208
input [15:0]INA,			// samples from LT2208
input  CC,					// Command & Control from Atlas C20
output reg ATTRLY,			// Antenna relay control
output A6, 					// MCLK_12MHZ (12.288MHz) to Atlas bus A6
input  C4, 					// LROUT (Rx audio) from Atlas bus
input  C8,					// CBLCK from Atlas bus
input  C9, 					// CLRCLK from Atlas bus
input  C17, 				// CLK_MCLK from Atlas bus
output MDOUT,				// I and Q out to Atlas bus on A10  
input  BCLK,				// 12.288MHz from Atlas bus C6 for I2S encoder
input  LRCLK,				// 192kHz from Atlas bus C7 for I2S encoder
output CDIN,				// Rx audio out to TLV320
output CBCLK, 				// 3.072MHz BCLK from Atlas C8
output CLRCLK, 				// 48KHz L/R clock from Atlas C9
output CLRCOUT,				// ditto 
output CMCLK, 				// 12.288MHz master clock from Atlas C17
output CMODE,				// SPI interface to TLV320
output reg MOSI,			// SPI interface to TLV320
output reg SCLK,			// SPI interface to TLV320
output reg nCS,				// SPI interface to TLV320
output SPI_data,			// SPI data to Alex
output SPI_clock,			// SPI clock to Alex
output Tx_load_strobe,		// SPI Tx data load strobe to Alex
output Rx_load_strobe,		// SPI Rx data load strobe to Alex
output FPGA_PLL, 			// PLL control volts to loop filter 
output LVDS_TXE,			// LVDS Tx enable
output LVDS_RXE_N,			// LVDS Rx enable
input  OVERFLOW,			// ADC overflow bit
output reg DITHER,			// ADC dither control bit
output SHDN,				// ADC shutdown bit
output reg PGA, 			// ADC preamp gain
output reg RAND, 			// ADC ramdonizer bit
output INIT_DONE,			// INIT_DONE LED 
output TEST0,				// Test point 
output TEST1,				// Test point
output TEST2,				// Test point
output TEST3,				// Test point
output DEBUG_LED0,			// Debug LED
output DEBUG_LED1,			// Debug LED
output DEBUG_LED2,			// Debug LED
output DEBUG_LED3,			// Debug LED
output DEBUG_LED4,			// Debug LED
output DEBUG_LED5,			// Debug LED
output DEBUG_LED6,			// Debug LED
output DEBUG_LED7			// Debug LED
);


reg data_ready;				// set at end of decimation

// Assign FPGA pass through connections
assign CDIN = C4;			// Rx audio data in I2S format to TLV320
assign CLRCLK = C9;			// 48kHz CLRLCK from Atlas C9
assign CLRCOUT = C9;		// ditto 
assign CBCLK = C8;			// 3.072MHz CBCLK from Atlas C8
assign CMCLK = C17;			// 12.288MHz CLK_MCLK from Atlas C17

// enable LT2208 
assign SHDN =  1'b0;		// 0 = normal operation
assign INIT_DONE = 1'b0;	// turn INIT_DONE LED on 


////////////////////////////////////////////////////////////////////////
//
//		Reset and enable code
//
////////////////////////////////////////////////////////////////////////

// holds clk_enable = 0 for first 1024 clock counts

reg clk_enable;
reg [10:0]reset_count;

always @ (posedge clock) begin
	if (reset_count[10]) begin
		clk_enable <= 1'b1;		// clk_enable high to run
		end
	else begin
		clk_enable <= 0;
		reset_count <= reset_count + 1'b1;
	end
end 



// A Digital Output Randomizer is fitted to the LT2208. This complements bits 15 to 1 if 
// bit 0 is 1. This helps to reduce any pickup by the A/D input of the digital outputs. 
// We need to de-ramdomize the LT2208 data if this is turned on. 

always @ (posedge clock) 
begin 
	if (RAND) begin									// RAND set so de-ramdomize
		if (INA[0]) temp_ADC <= {~INA[15:1],INA[0]};
		else temp_ADC <= INA;
	end
	else temp_ADC <= INA;							// not set so just copy data
end 

//////////////////////////////////////////////////////////////
//
// 		Set up TLV320 using SPI 
//
/////////////////////////////////////////////////////////////

/* 

NOTE: TLV320 is set up via SPI rather then I2C since with
a complete system i.e. Mercury, Penelope and Janus, then 
there will be 3 TLV320s and only two options for I2C addresses.

Data to send to TLV320 is 

 	1E 00 - Reset chip
 	12 01 - set digital interface active
 	08 14 - D/A on
 	0C 00 - All chip power on
 	0E 02 - Slave, 16 bit, I2S
 	10 00 - 48k, Normal mode
 	0A 00 - turn D/A mute off

*/

reg index;
reg [15:0]tdata;
reg [2:0]load;
reg [3:0]TLV;
reg [15:0] TLV_data;
reg [3:0] bit_cnt;

// Set up TLV320 data to send 

always @ (posedge index)		
begin
load <= load + 3'b1;			// select next data word to send
case (load)
3'd0: tdata <= 16'h1E00;		// data to load into TLV320
3'd1: tdata <= 16'h1201;
3'd2: tdata <= 16'h0814;		// D/A on 
3'd3: tdata <= 16'h0C00;
3'd4: tdata <= 16'h0E02;
3'd5: tdata <= 16'h1000;
3'd6: tdata <= 16'h0A00;
default: load <= 0;
endcase
end

// State machine to send data to TLV320 via SPI interface

assign CMODE = 1'b1;		// Set to 1 for SPI mode

always @ (posedge BCLK)		// use 12.288MHz BCLK clock for SPI
begin
case (TLV)
4'd0: begin
         nCS <= 1'b1;                   // set TLV320 CS high
         bit_cnt <= 4'd15;             	// set starting bit count to 15
         index <= ~index;               // load next data to send
         TLV <= TLV + 4'b1;
      end
 4'd1: begin
         nCS <= 1'b0;                   // start data transfer with nCS low
         TLV_data <= tdata;
         MOSI <= TLV_data[bit_cnt];    	// set data up
         TLV <= TLV + 4'b1;
       end
 4'd2: begin
         SCLK <= 1'b1;                  // clock data into TLV320
         TLV <= TLV + 4'b1;
       end
 4'd3: begin
         SCLK <= 1'b0;                  // reset clock
         TLV <= TLV + 4'b1;
       end
 4'd4: begin
          if(bit_cnt == 0) begin   		// word transfer is complete, check for any more
            index <= ~index;
            TLV <= 4'd5;
          end
          else begin
            bit_cnt <= bit_cnt - 1'b1;
            TLV <= 4'b1;                   // go round again
          end
       end                                 // end transfer
 4'd5: begin
         if (load == 7)begin               // stop when all data sent
            TLV <= 4'd5;                   // hang out here forever
            nCS <= 1'b1;                   // set CS high
         end
         else TLV <= 0;                    // else get next data
       end
 default: TLV <= 0;
 endcase
 end

//////////////////////////////////////////////////////////////
//
//		CLOCKS
//
//////////////////////////////////////////////////////////////

// Generate 122.88MHz/10 MCLK_12MHZ for Atlas bus

reg MCLK_12MHZ;
reg [2:0]MCLK_count;
always @ (posedge clock)
begin
	if (MCLK_count == 4)  // divide 122.88MHz clock by 10 to give 12.288MHz
		begin
		MCLK_12MHZ <= ~MCLK_12MHZ;
		MCLK_count <= 0;
		end
	else MCLK_count <= MCLK_count + 1'b1;
end
	
// Generate CBCLK (3.072MHz)/4 for SPI interface
	
reg SPI_clk;
reg SPI_count;
always @(posedge CBCLK)
begin
	if(SPI_count == 1)begin
		SPI_clk <= ~SPI_clk;
		SPI_count <=0;
	end 
	else SPI_count <= SPI_count + 1'b1;
end	

// Select 122.88MHz source. If source_122MHZ set then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

wire clock;

assign clock = CLKA;	// use clock out of LT2208 as master clock
assign LVDS_RXE_N = source_122MHZ ? 1'b1 : 1'b0; // enable LVDS receiver if clock is external
assign LVDS_TXE = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if  Mercury is the source 

// send MCLK_12MHZ to Atlas A6 
assign A6 = MCLK_12MHZ;

// select 10MHz reference source. If ref_ext is set use Mercury's 10MHz ref and send to Atlas C16
wire reference;
assign reference = ref_ext ? OSC_10MHZ : ext_10MHZ ; 
assign ext_10MHZ = ref_ext ? OSC_10MHZ : 1'bZ ; 		// C16 is bidirectional so set high Z if input. 

//////////////////////////////////////////////////////////////
//
//		Convert frequency to phase word 
//
//////////////////////////////////////////////////////////////

/*	
	Calculates  (frequency * 2^32) /122.88e6
	Each calculation takes ~ 0.6uS @ 122.88MHz
	This method is quite fast enough and uses much lower LEs than a Megafunction

*/

wire [31:0]freq;
wire ready;
always @ (posedge ready)		// strobe frequecy when ready is set
begin
	frequency <= frequency_HZ;	// frequecy_HZ is current frequency in Hz e.g. 14,195,000Hz
end 

division division_DDS(.quotient(freq),.ready(ready),.dividend(frequency),.divider(32'd122880000),.clk(clock));


// sync frequecy change to 122.88MHz clock
reg [31:0]sync_frequency;
always @ (posedge clock)
begin
	sync_frequency <= freq;
end

//////////////////////////////////////////////////////////////
//
//		CORDIC NCO 
//
//////////////////////////////////////////////////////////////

//Code rotates A/D input at set frequency  and produces I & Q 
wire [17:0]i_out;
wire [17:0]q_out;
reg  [15:0]temp_ADC;
wire[31:0] phase;
reg [31:0]frequency;

// The phase accumulator takes a 32 bit frequency dword and outputs a 32 bit phase dword on each clock
phase_accumulator rx_phase_accumulator(.clk(~clock),.reset(~clk_enable),.frequency(sync_frequency),.phase_out(phase));


// set i_in to 0 for real inputs 
cordic_VK6APH cordic (.i_in(16'd0), .q_in(temp_ADC), .iout(i_out), .qout(q_out), .ain(phase[31:12]), .clk(clock));

///////////////////////////////////////////////////////////////
//
//		CIC Filters - designed using MatLab
//
///////////////////////////////////////////////////////////////

wire signed [23:0]cic_out_i;
wire signed [23:0]cic_out_q;
wire signed [23:0]cic_out_i_1;
wire signed [23:0]cic_out_q_1;
wire signed [23:0]cic_out_i_2;
wire signed [23:0]cic_out_q_2;
wire ce_out_i_1;				// narrow pulse when data available
wire ce_out_q_1;				// narrow pulse when data available
wire ce_out_i_2;				// narrow pulse when data available
wire ce_out_q_2;				// narrow pulse when data available
wire ce_out_i;					// narrow pulse when data available
wire ce_out_q;					// narrow pulse when data available

//first CIC - decimate by 10, 3rd order - clock out is 12.288MHz
cic_10_1_3_nofraction cic_I_1( .clk(clock),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(i_out),.filter_out(cic_out_i_1),.ce_out(ce_out_i_1));
cic_10_1_3_nofraction cic_Q_1( .clk(clock),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(q_out),.filter_out(cic_out_q_1),.ce_out(ce_out_q_1));

//second CIC - decimate by 8, 5th order - clock out is 1.536MHz
cic_8_1_5_nofraction cic_I_2( .clk(ce_out_i_1),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_out_i_1),.filter_out(cic_out_i_2),.ce_out(ce_out_i_2));
cic_8_1_5_nofraction cic_Q_2( .clk(ce_out_q_1),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_out_q_1),.filter_out(cic_out_q_2),.ce_out(ce_out_q_2));

//third CIC - decimate by 4, 12th order - clock out is 384kHz
cic_4_1_12_nofraction cic_I_3( .clk(ce_out_i_2),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_out_i_2),.filter_out(cic_out_i),.ce_out(ce_out_i));
cic_4_1_12_nofraction cic_Q_3( .clk(ce_out_q_2),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_out_q_2),.filter_out(cic_out_q),.ce_out(ce_out_q));

//////////////////////////////////////////////////////////////////////
//
//		CFIR 
//
//////////////////////////////////////////////////////////////////////

// compensates for sinx/x response of CIC, provides ultimage rejection and decimates by 2
// output data rate is 192kHz

wire signed [47:0]FIR_i_out;
wire signed [47:0]FIR_q_out;
wire FIR_strobe;

FIR_top  FIR(.clk(clock),.reset(~clk_enable),.data_in_I(cic_out_i),.data_in_Q(cic_out_q),.strobe_in(ce_out_i ),.data_out_I(FIR_i_out),.data_out_Q(FIR_q_out),.strobe_out(FIR_strobe));

//////////////////////////////////////////////////////////////////////
//
//		Remove DC offset from CFIR output 
//
//////////////////////////////////////////////////////////////////////

wire signed [23:0]i_no_dc;
wire signed [23:0]q_no_dc;
wire signed [23:0]dc_in_i;
wire signed [23:0]dc_in_q;

assign dc_in_i = FIR_i_out[47:24];
assign dc_in_q = FIR_q_out[47:24];

dc_offset_correct_new dc_offset_correct_i(.clk(~FIR_strobe),.clken(clk_enable),.data_in(dc_in_i),.data_out(i_no_dc),.dc_level_out());
dc_offset_correct_new dc_offset_correct_q(.clk(~FIR_strobe),.clken(clk_enable),.data_in(dc_in_q),.data_out(q_no_dc),.dc_level_out());

// Sync dc correct output to neg edge of BCLK

reg signed [23:0]i;
reg signed [23:0]q;

always @ (negedge BCLK) 
begin
	i <= i_no_dc;
	q <= q_no_dc;
end 

// I2S encoder to send I and Q data to Atlas on C10

I2SEncode  I2S(.LRCLK(LRCLK), .BCLK(BCLK), .left_sample(i), .right_sample(q), .outbit(MDOUT)); 

///////////////////////////////////////////////////////////
//
//    Command and Control Decoder 
//
///////////////////////////////////////////////////////////
/*

	The C&C encoder in Ozy broadcasts data over the Atlas bus (C20) for
	use by other cards e.g. Mercury and Penelope.  The data is in 
	I2S format with the clock being CBLCK and the start of each frame
	being indicated using the negative edge of CLRCLK.
	
	The data format is as follows:
	
	<[58]PTT><[57:54]address><[53:22]frequency><[21:18]clock_select><[17:11]OC><[10]Mode>
	<[9]PGA><[8]DITHER><[7]RAND><[6:5]ATTEN><[4:3]TX_relay><[2]Rout><[1:0]RX_relay> 
	
	for a total of 59 bits. Frequency is in Hz and 32 bit binary format and 
	OC is the open collector data on Penelope. Mode is for a future Class E PA,
	PGA, DITHER and RAND are ADC settings and ATTEN the attenuator on Alex
	
	The clock source (clock_select) decodes as follows:
	
	0x00  = 10MHz reference from Atlas bus ie Gibraltar
	0x01  = 10MHz reference from Penelope
	0x10  = 10MHz reference from Mercury
	00xx  = 122.88MHz source from Penelope 
	01xx  = 122.88MHz source from Mercury 
	
*/

reg [5:0] bits;     // how many bits clocked 
reg [1:0]CC_state;
reg [58:0] CCdata;	// 54 bits of C&C data

always @(posedge CBCLK)  // use CBCLK  from Atlas C8 
begin
case(CC_state)
0:	begin
	if (CLRCLK == 0)CC_state <= 0;			// loop until CLRLCK is high   
	else CC_state <= 1;
	end
1:	begin
		if (CLRCLK)	CC_state <= 1;			// loop until CLRCLK is low  
		else begin
		bits <= 6'd58;						
		CC_state <= 2;
		end
	end
2:	begin
	CCdata[bits] <= CC;						// this is the second CBCLK after negedge of CLRCLK
		if (bits == 0)CC_state <= 0; 		// done so restart
		else begin
		bits <= bits - 1'b1;
		CC_state <= 2;  
		end
	end
default: CC_state <= 0;
endcase
end

// decode C & C data into variables and sync to 48kHz LR clock

reg PTT_out;
reg [3:0]Address;		// Address in C&C header, set to 0 for now
reg [31:0]frequency_HZ;	// frequency control bits for CORDIC
reg [3:0]clock_select;	// 10MHz and 122.88MHz clock selection
wire ref_ext;			// Set when internal 10MHz reference sent to Atlas C16
wire source_122MHZ;		// Set when internal 122.88MHz source is used and sent to LVDS
reg [1:0] ATTEN;		// attenuator setting on Alex
reg [1:0]TX_relay;		// Tx relay setting on Alex
reg Rout;				// Rx1 out on Alex
reg [1:0]RX_relay;		// Rx relay setting on Alex

always @ (negedge CLRCLK)  
begin 
	PTT_out <= (CCdata[58]); 	// PTT from PC via USB 
	Address <= CCdata[57:54];
	if(Address == 0)begin
		frequency_HZ <= CCdata[53:22];
		clock_select <= CCdata[21:18];     
		//OC <= CCdata[17:11];		// Penelope Open Collectors, not used by Mercury
		PGA    <= 1'b0; 			// 1 = gain of 1.5(3dB), 0 = gain of 1
		ATTRLY <=  ~CCdata[9];		// 1 = Attenuator on, 0 = Preamp on 
		DITHER <= CCdata[8];		// 1 = dither on
		RAND   <=  CCdata[7];		// 1 = randomizer on 
		ATTEN  <= CCdata[6:5];		// Attenuator setting on Alex
		TX_relay <= CCdata[4:3];	// Tx relay selection on Alex
		Rout <= CCdata[2];			// Rx_1_out on Alex
		RX_relay <= CCdata[1:0];	// Rx relay selection on Alex
		end	
end

assign ref_ext = clock_select[1]; 			// if set use internally and send to C16 else get from C16
assign source_122MHZ = clock_select[2] ; 	// if set use internally and send to LVDS else
											// get from LVDS 



													
//////////////////////////////////////////////////////////////
//
//		Alex Filter selection
//
//	The frequency sent by PowerSDR is the indicated frequency
//  less the 9kHz IF. In order to select filters at the correct
//  frequency we need to add the IF offset to the current frequency.
//
//////////////////////////////////////////////////////////////

wire [6:0]LPF;
wire [5:0]select_HPF;
wire [31:0]frequency_plus_IF; // add PowerSDR IF frequecy (9kHz) to current frequency

assign frequency_plus_IF = frequency + 32'd9000; // add 9kHz IF offset 

LPF_select Alex_LPF_select(.frequency(frequency_plus_IF), .LPF(LPF));
HPF_select Alex_HPF_select(.frequency(frequency_plus_IF), .HPF(select_HPF));

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

wire ANT1;			
wire ANT2;
wire ANT3;
wire Rx_1_out;
wire Transverter;
wire Rx_2_in;
wire Rx_1_in;

assign Rx_1_out = Rout;

assign ANT1 = (TX_relay == 2'b00) ? 1'b1 : 1'b0;  		// select Tx antenna 1
assign ANT2 = (TX_relay == 2'b01) ? 1'b1 : 1'b0;		// select Tx antenna 2
assign ANT3 = (TX_relay == 2'b10) ? 1'b1 : 1'b0;		// select Tx antenna 3

assign Rx_1_in = (RX_relay == 2'b01) ? 1'b1 : 1'b0;		// select Rx antenna 1
assign Rx_2_in = (RX_relay == 2'b10) ? 1'b1 : 1'b0;		// select Rx antenna 2
assign Transverter = (RX_relay == 2'b11) ? 1'b1 : 1'b0;	// select Transverter input 


//////////////////////////////////////////////////////////////
//
//		Alex SPI interface
//
//////////////////////////////////////////////////////////////

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
assign Tx_red_led = PTT_out; 	// turn red led on when we Tx
assign TR_relay = PTT_out;		// turn on TR relay when PTT active

assign Alex_Tx_data = {LPF[6:4],Tx_red_led,TR_relay,ANT3,ANT2,ANT1,LPF[3:0],Tx_yellow_led,3'b000};

// define and concatinate the Rx data to send to Alex via SPI
assign Rx_red_led = PTT_out;	// turn red led on when we Tx

// turn 6m preamp on if frequency > 50MHz 
assign _6m_preamp = (frequency_plus_IF > 50000000) ? 1'b1 : 1'b0;

// if 6m preamp selected disconnect all filters 
wire [5:0]HPF;
assign HPF = _6m_preamp ? 6'd0 : select_HPF; 

// V3 Alex hardware
assign Alex_Rx_data = {Rx_red_led,_10dB_atten ,_20dB_atten, HPF[5], Rx_1_out,Rx_1_in,Rx_2_in,Transverter,
					   1'b0, HPF[4:2],_6m_preamp,HPF[1:0],Rx_yellow_led};
					   
// concatinate Tx and Rx data and send to SPI interface. SPI interface only sends on a change of Alex_data.
// All data is sent in about 120uS.
wire [31:0]Alex_data;
assign Alex_data[31:0] = {Alex_Tx_data[15:0],Alex_Rx_data[15:0]};

SPI   Alex_SPI_Tx(.Alex_data(Alex_data), .SPI_data(SPI_data),
				  .SPI_clock(SPI_clock), .Tx_load_strobe(Tx_load_strobe),
				  .Rx_load_strobe(Rx_load_strobe),.spi_clock(SPI_clk));												
													
													
///////////////////////////////////////////////////////////
//
//    PLL 
//
///////////////////////////////////////////////////////////

/* 
	Divide the 10MHz reference and 122.88MHz clock to give 80kHz signals.
	Apply these to an EXOR phase detector. If the 10MHz reference is not
	present the EXOR output will be a 80kHz square wave. When passed through 
	the loop filter this will provide a dc level of (3.3/2)v which will
	set the 122.88MHz VCXO to its nominal frequency.
	The selection of the internal or external 10MHz reference for the PLL
	is made using ref_ext.
*/

// div 10 MHz ref clock by 125 to get 80 khz 

wire ref_80khz; 
reg osc_80khz; 

oddClockDivider refClockDivider(reference, ref_80khz); 

// Divide  122.88 MHz by 1536 to get 80 khz 
reg [9:0] count_12288; 

always @ (posedge clock) begin
        if (count_12288 == 767) begin
                count_12288 <= 0;
                osc_80khz <= ~osc_80khz; 
        end
        else begin
                count_12288 <= 1'b1 + count_12288;
        end
end

// NOTE: If external reference is not available then phase detector 
// will be fed with 80kHz from 122.88MHz clock. Loop filter will 
// set VCXO control volts to 3.3v/2

// Apply to EXOR phase detector 
assign FPGA_PLL = ref_80khz ^ osc_80khz; 


// LEDs for testing 0 = off, 1 = on
assign DEBUG_LED0 = OVERFLOW; 		// LED 0 on when ADC Overflow	

// check for correct Alex relay selection
assign DEBUG_LED1 = 1'b1;
assign DEBUG_LED2 = 1'b1;
assign DEBUG_LED3 = TX_relay[0]; 
assign DEBUG_LED4 = TX_relay[1];
assign DEBUG_LED5 = RX_relay[0];
assign DEBUG_LED6 = RX_relay[1];
assign DEBUG_LED7 = Rout;


// Test pins
assign TEST0 = osc_80khz; // 80kHz from 122.88MHz
assign TEST1 = ref_80khz; // 80kHz from 10MHz
assign TEST2 = FPGA_PLL;  // phase detector output
assign TEST3 = 1'b0; 


endmodule 



