/***********************************************************
*
*	Mercury DDC receiver 
*
************************************************************/

// V2.6 11 Feb 2009 

// (C) Phil Harman VK6APH 2006,2007,2008, 2009


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
	outputs.  These are decimated by 320/640/1280 in CIC filters then by 2 
	in an  CFIR filter to give output data at 192/96/48kHz to feed
	the Atlas bus in I2S format for tranfer via Ozy to a suitable PC program.
	
	Change log:
	
	23 Dec 2008 - Production release
	23 Dec 2008 - Added code from Alex VE3NEA that provides 48/96/192KHz sampling
	26 Dec 2008 - Working on pin drive issues
	            - Changed CORDIC to wide_phase to give 1Hz steps
	30 Dec 2008 - Changed pins to 3.0v LVTTL, set A6 and C16 to 16mA, no R, no PCI diode, output termination off
	            - added modifications by Kirk Weedman, KD7IRS.
	            - released as V2.1
	 1 Jan 2009 - Used Kirk's oddClockDiv.
                - Tidy comments
                - Move the instantiation of certain variables up higher in the lines of
                  code so that compilers like ModelSim won't produce errors
     3 Jan 2009 - Fix Alex relay data not being sent correctly
                - changed frequency to phase_word since that is correct term
     4 Jan 2009 - Code tidy by Kirk, KD7IRS, released as v2.3
     5 Jan 2009 - Added spectrum data to A12
     9 Jan 2009 - Change A10 to 16mA, no R, no PCI diode, output termination off 
			    - Added ADC overflow bit on C18
    10 Jan 2009 - Changed spectrum data to 0,1,2....4095 for testing
				- Released as V2.4
	12 Jan 2009 - Spectrum data now ADC samples
				- Simplified ADC Overlow flag code 
				- same release number
				- Added software serial number on serno, C19 
	26 Jan 2009 - fixed spectum data, was sending every 16th word. 
			    - same release no
    31 Jan 2009 - Added seperate code for serial # and spectrum 
				- released as V2.5
	 2 Feb 2009 - code refactored
	 4 Feb 2009 - Changed ext_10MHZ to max current for testing - no effect
	 5 Feb 2009 - moved ext_10MHz to A22 for testing - was pin 82
	 6 Feb 2009 - started debug of position sensitivity
	            - changed Synthesis options for State Machine Processing  = User Encoded
	            - If EP4 in FX2 not ready then don't send spectrum data over Atlas
	 9 Feb 2009 - Fixed bug in spectrum code
				- 10MHz back on C16
				- Reset Spectrum FIFO when full set
	
	To turn on SignalTap -   Assignments>Settings>SignalTap II Logic Analyzer > Enable			
	 
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
	- Synthesis options for State Machine Processing  = User Encoded
	
*/

	
module Mercury(

input         OSC_10MHZ,  // 10MHz TCXO input 
inout         ext_10MHZ,  // 10MHz reference to/from Atlas pin C16
input         CLKA,       // 122.88MHz clock from LT2208
input  [15:0] INA,        // samples from LT2208
input         CC,         // Command & Control from Atlas C20
output        ATTRLY,     // Antenna relay control
output        A6,         // MCLK_12MHZ (12.288MHz) to Atlas bus A6
input         C4,         // LROUT (Rx audio) from Atlas bus
input         C8,         // CBLCK from Atlas bus
input         C9,         // CLRCLK from Atlas bus
input         C17,        // CLK_MCLK from Atlas bus
output        MDOUT,      // I and Q out to Atlas bus on A10  
input         BCLK,       // 12.288MHz from Atlas bus C6 for I2S encoder
input         LRCLK,      // 192/96/48kHz from Atlas bus C7 for I2S encoder
output        CDIN,       // Rx audio out to TLV320
output        CBCLK,      // 3.072MHz BCLK from Atlas C8
output        CLRCLK,     // 48KHz L/R clock from Atlas C9
output        CLRCOUT,    // ditto 
output        CMCLK,      // 12.288MHz master clock from Atlas C17
output        CMODE,      // SPI interface to TLV320
output  	  MOSI,       // SPI interface to TLV320
output   	  SCLK,       // SPI interface to TLV320
output    	  nCS,        // SPI interface to TLV320
output        SPI_data,   // SPI data to Alex
output        SPI_clock,  // SPI clock to Alex
output        Tx_load_strobe,   // SPI Tx data load strobe to Alex
output        Rx_load_strobe,   // SPI Rx data load strobe to Alex
output        FPGA_PLL,   // PLL control volts to loop filter 
output        LVDS_TXE,   // LVDS Tx enable
output        LVDS_RXE_N, // LVDS Rx enable
input         OVERFLOW,   // ADC overflow bit
output        DITHER,     // ADC dither control bit
output        SHDN,       // ADC shutdown bit
output        PGA,        // ADC preamp gain
output        RAND,       // ADC ramdonizer bit
output        INIT_DONE,  // INIT_DONE LED 
output        TEST0,      // Test point 
output        TEST1,      // Test point
output        TEST2,      // Test point
output        TEST3,      // Test point
output        DEBUG_LED0, // Debug LED
output        DEBUG_LED1, // Debug LED
output        DEBUG_LED2, // Debug LED
output        DEBUG_LED3, // Debug LED
output        DEBUG_LED4, // Debug LED
output        DEBUG_LED5, // Debug LED
output        DEBUG_LED6, // Debug LED
output        DEBUG_LED7, // Debug LED
input         DFS0, DFS1, // I/Q sampling rate selection
output  	  spectrum,	  // ADC samples for bandscope A12
output reg    ADC_OVERFLOW, // set when ADC overflows C18
inout  wire   serno,	    // serial # out to Atlas bus on C19
input  wire   full			// high when spectrum FIFO in Ozy is full

);


localparam SERIAL = 8'd26;  // software version serial number - 2.6

wire  [15:0] temp_ADC;
wire  CLK_122MHz;

// Assign FPGA pass through connections
assign CDIN       = C4;   // Rx audio data in I2S format to TLV320
assign CLRCLK     = C9;   // 48kHz CLRLCK from Atlas C9
assign CLRCOUT    = C9;   // ditto 
assign CBCLK      = C8;   // 3.072MHz CBCLK from Atlas C8
assign CMCLK      = C17;  // 12.288MHz CLK_MCLK from Atlas C17
// enable LT2208 
assign SHDN       = 1'b0;	// 0 = normal operation
assign INIT_DONE  = 1'b0;	// turn INIT_DONE LED on
assign CLK_122MHz = CLKA;	// use clock out of LT2208 as master clock
assign PGA        = 1'b0; 	// 1 = gain of 1.5(3dB), 0 = gain of 1

//----------------------------------------
// Remove effect of randomizer if on
//----------------------------------------
random random_inst(
	.clock(CLK_122MHz),
	.ADC_samples(INA),
	.random(RAND),
	.out(temp_ADC)
	);
	
//----------------------------------- 
// Set up TLV320 using SPI 
//-----------------------------------
TLV320  TLV320_inst(
	.clock(BCLK),
	.nCS(nCS),
	.MOSI(MOSI),
	.SCLK(SCLK),
	.CMODE(CMODE)
	);

//----------------------------------------------------------
//		CLOCKS
//----------------------------------------------------------
wire MCLK_12MHZ;
wire SPI_clk;
clocks	clocks_inst(
	.clock(CLK_122MHz),
	.CBCLK(CBCLK),
	.MCLK_12MHZ(MCLK_12MHZ),
	.SPI_clk(SPI_clk)
	);

assign A6 = MCLK_12MHZ; // send MCLK_12MHZ to Atlas A6

// Select 122.88MHz source. If source_122MHZ set then use Mercury's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS
wire source_122MHZ;		// Set when internal 122.88MHz source is used and sent to LVDS

assign LVDS_RXE_N = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDS_TXE   = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if  Mercury is the source 

// select 10MHz reference source. If ref_ext is set use Mercury's 10MHz ref and send to Atlas C16
wire reference;
wire ref_ext;			// Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? OSC_10MHZ : ext_10MHZ ; 
assign ext_10MHZ = ref_ext ? OSC_10MHZ : 1'bZ ; 		// C16 is bidirectional so set high Z if input.


//---------------------------------------------------
//		Convert frequency to phase word 
//---------------------------------------------------
wire[31:0]frequency_HZ;
wire[31:0]sync_phase_word;

freq_to_phase  freq_to_phase_inst (
	.clock(CLK_122MHz),
	.CBCLK(CBCLK),
	.frequency_HZ(frequency_HZ),   		
	.sync_phase_word(sync_phase_word)
	);

//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------
wire [23:0] rx_out_data_I;
wire [23:0] rx_out_data_Q;

receiver receiver_inst(
  //control
  .clock(CLK_122MHz),
  .rate({DFS1, DFS0}), //00=48, 01=96, 10=192 kHz
  .frequency(sync_phase_word),
  .out_strobe(),
  //input
  .in_data(temp_ADC),
  //output
  .out_data_I(rx_out_data_I),
  .out_data_Q(rx_out_data_Q)
  );


//----------------------------------------------------
// I2S encoder to send I and Q data to Atlas on C10
//----------------------------------------------------

wire test;

I2SEncode  I2S(
//newI2S newI2S_inst(
	.LRCLK(LRCLK),
	.BCLK(BCLK), 
	.left_sample(rx_out_data_I), 
	.right_sample(rx_out_data_Q), 
	.outbit(MDOUT)
	); 

//------------------------------------
//   ADC Overflow set
//------------------------------------
always @ (posedge LRCLK) ADC_OVERFLOW <= OVERFLOW;




//-----------------------------------------
//		Spectrum data capture
//-----------------------------------------

// Load consecutive ADC samples into FIFO until it is full, 4096 words.
// Wait until all data has been sent and then repeat.

reg [15:0] test_count;


always @ (posedge CLK_122MHz) 
begin
	if (wrfull) begin
		wrreq <= 1'b0;
	end
	if (wrempty ) wrreq <= 1'b1;
	if (wrreq) begin
		//test_count <= test_count + 511;
		end
end

wire aclr;
wire rdempty;
assign aclr = full;

// 16 x 4096 data FIFO
wire rdreq; reg wrreq; wire wrfull; wire [15:0]q; wire wrempty;
spectrum_FIFO spectrum_FIFO_inst(.data(temp_ADC),.rdclk(BCLK),.rdreq(rdreq),.wrclk(~CLK_122MHz),
								 .wrreq(wrreq),.q(q),.wrempty(wrempty),.wrfull(wrfull), .aclr(aclr),.rdempty(rdempty));
//spectrum_FIFO inst_spectrum_FIFO(.data(test_count),.rdclk(~BCLK),.rdreq(rdreq),.wrclk(~CLK_122MHz),
//							.wrreq(wrreq),.q(q),.wrempty(wrempty),.wrfull(wrfull), .aclr(aclr),.rdempty(rdempty));

spectrum spectrum_inst(
	.BCLK(BCLK), 
	.LRCLK(LRCLK), 
	.full(full),
	.rdreq(rdreq),
	.q(q), 
	.spectrum(spectrum)
	);


//-----------------------------------------
//		Software Serial Number 
//-----------------------------------------
software_serial software_serial_inst(
	.BCLK(BCLK),
	.LRCLK(LRCLK),
	.SERIAL(SERIAL),
	.serno(serno)
	);


//--------------------------------------------------------
//    Command and Control Decoder 
//--------------------------------------------------------
wire [1:0] ATTEN;
wire [1:0] TX_relay;
wire [1:0] RX_relay;
wire PTT_out;
wire Rout;

localparam ADDRESS = 4'b0; 	// Address for Mercury data

command_control command_control_inst(
	.CBCLK(CBCLK),
	.CLRCLK(CLRCLK),
	.CC(CC),
	.PTT_out(PTT_out),
	.ATTEN(ATTEN),
	.TX_relay(TX_relay),
	.Rout(Rout),
	.RX_relay(RX_relay),
	.frequency_HZ(frequency_HZ),
	.ATTRLY(ATTRLY), 
	.RAND(RAND),
	.DITHER(DITHER),
	.ref_ext(ref_ext),
	.source_122MHZ(source_122MHZ),
	.ADDRESS(ADDRESS)
	 );
													

//---------------------------------------------------------
//		Alex control 
//---------------------------------------------------------

alex alex_inst(
	.frequency_HZ(frequency_HZ),
	.ATTEN(ATTEN),
	.PTT_out(PTT_out), 
	.SPI_data(SPI_data), 
	.Tx_load_strobe(Tx_load_strobe), 
	.Rx_load_strobe(Rx_load_strobe), 
	.SPI_clock(SPI_clock), 
	.clock(SPI_clk)
	);										
													
													
//------------------------------------
//    PLL 
//------------------------------------
wire osc_80khz;
wire ref_80khz;

PLL PLL_inst(
	.clock(CLK_122MHz), 
	.reference(reference), 
	.FPGA_PLL(FPGA_PLL),
	.osc_80khz(osc_80khz),
	.ref_80khz(ref_80khz)
	);


// LEDs for testing 0 = off, 1 = on
assign DEBUG_LED0 = OVERFLOW; 		// LED 0 on when ADC Overflow

// check for correct Alex relay selection

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



//------------------------------------------------------------------------------
//                          blink!
//------------------------------------------------------------------------------
reg [26:0]counter;
always @(posedge CLKA) counter = counter + 1'b1;
assign {DEBUG_LED2,DEBUG_LED1} = counter[26:25];  // slow flash for this version!


endmodule 



