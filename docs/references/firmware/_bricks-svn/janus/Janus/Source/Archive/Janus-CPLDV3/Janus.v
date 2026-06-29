// 
// Copyright (c) Bill Tracey 2006 (bill@ewjt.com) & Phil Harman (phil@pharman.org)
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  Janus to Ozy interface. 
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
// 
// CPLD code for Janus board to interface with a Xylo board.
// Sets I/O pins on ADC and DAC chips plus provides clocks.
//
// TLV320 is in slave mode and AK5394A is Master. Note that both devices are fed from 
// the Master Clock CLK_MCLK (12.288MHz) which comes from Atlas. CLK_MCLK is generated in Ozy and 
// is either CLK_12MHZ from Janus looped to CLK_MCLK or is derived from Mercury or Penelope if fitted.
//
// Sets up TLV320 via its I2C interface.
//
// Enables the 12.288MHz clock to be phased locked to a 10MHz reference as follows:
// The  10MHz reference signal and 12.288MHz VCXO are divided 
// to give 8kHz signals.  These are fed to a Phase Frequency Detector (PFD)
// the output of which is filtered and fed to the VCXO. 
// The PFD has a lock detector that is active low. 
// The circuit checks that the 10MHz reference is present, if not 
// it connects the 8kHz from the VCXO to the PFD output so that 
// the VCXO LPF output is constant at 3.3v/2 which sets the VCXO to its nominal value.
//
// All unused pins on the CPLD have been set as tristate inputs in Quartus II
// 
// Built using Quartus II v6.1  build 201.


// Change log:
//
//	12 August 2006 - increased reset to 3 seconds otherwise AK 5394A did not 
//                   reset correctly when connected to OZY. VK6APH 
//	12 August 2006 - forced speed to 48k for testing 
//  13 August 2006 - AK5394A now reset at power on and speed setting via Ozy
//  14 August 2006 - inverted PTT pass to Atlas C3
//  19 August 2006 - Interface to Atlas pins changed - V1.0
//  21 August 2006 - Change to use I2C interface to TLV320AIC23B  
//  19 November 2006 - Changes to use the V2 hardware. Replace 24.576MHz clock with 12.288MHz. 
//  28 November 2006 - Added PLL to enable 12.288MHz clock to be locked to 10MHz reference
//  10 February 2007 - Changed I Q to I2S format and added PWM DAC for I and Q outputs
//  12 February 2007 - Added clock doubler to run PWM DAC from 2 x 48MHz. 
//  22 February 2007 - Added CLK_MCLK input that is used clock the AD5394A and TLV320AIC23B
//  23 February 2007 - Removed clock doubler due to noise in audio output 
//  28 February 2007 - Changed PTT to be logic 1 if active and high Z if not
//	 6 April    2007 - Changed AK5394A to run in Slave mode
//   2 April    2009 - Removed C17 CLK_MCLK (12.288MHz) and sourced this clock from C5.
//					 - Released as V3.0  
//
//
//  IMPORTANT: AK5394A nRST is connected to AK_reset input. Unless this is connected to 
//  +3.3v or an Ozy board the AK5394A will remain in reset and produce no clocks etc.

module Janus(
   	input  CLK_12MHZ,
   	input  CAL,
   	output CBCLK,
   	output CDIN,
   	input  CDOUT,
   	output CLRCIN,
   	output CLRCOUT,
   	output CMCLK,
   	output CMODE,
   	output DFS0,
   	output DFS1,
   	output TUNE,
   	output LED1,
   	output LED2,
   	output EXP4,
   	input  FSYNC,
   	output HPF,
   	output IPWM,
   	output LRCLK,
   	output MCLK,
   	output nCS,
   	output nRST,
   	input  PTT,
   	output QPWM,	// Q data out
   	output SCLK,	
  	input  SDOUT,
   	output SMODE1,
   	output SMODE2,
   	input  SSCK,  	// was output for spi interface 
   	input  MOSI,  	// was output for spi interface 
   	output ZCAL,
   	input  C2,		// nRST
	input  CLK_48MHZ,	// 48MHz clock from FX2 for PWM 
	input  IQOUT,	// I and Q data in I2S format 
	output C5,		// 12.288MHz clock to Atlas bus
	input  C6,		// SCLK (BCLK)
	input  C7,		// LRCLK
	input  C8,  	// CBCLK
	input  C9,		// CLRCIN/CLRCOUT
	output C10,		// SDOUT
	output C11,		// CDOUT
	input  C12,		// CDIN
	input  C13,		// DFS0
	input  C14,		// DFS1
	inout  C15, 	// !PTT
	input  ref_in 	// C16 - 10MHz reference in from Atlas bus
); 

reg index;
reg [15:0]tdata;
reg [2:0]load;
reg [3:0]TLV;
reg [15:0] TLV_data;
reg TLV_CLK;
reg data; 
wire pfd_out;
reg ref_8k;
reg osc_8k;
wire cout1;
wire cout2;
reg ref_OK; // is low when 10MHz reference signal is present

//////////////////////////////////////////////////////////////
//
// 		PLL to lock 12.288MHz clock to 10MHz reference
//
/////////////////////////////////////////////////////////////

// Uses Altera LPM_COUNTER Megafunction for the dividers

// divide 10MHz reference clock by 625 to give  16kHz
counter1 counter1(.clock(ref_in), .cout(cout1));

// divide by 2 to give a square wave
always @ (posedge cout1)
	ref_8k <= ~ref_8k;


// divide 12.288MHz ADC  clock by 768 to give 16kHz
counter2 counter2(.clock(CLK_12MHZ), .cout(cout2));

// divide by 2 to give a square wave
always @ (posedge cout2)
	osc_8k <= ~osc_8k;


// apply to PFD
wire lock;
pfd Janus_pfd(.ref_in(ref_8k),.osc_in(osc_8k),.pfd_out(pfd_out),.lock(lock));

//
// check if the 10MHz reference signal is present.
// If so use PFD output else use 8kHz so as to set the 
// VCXO control voltage to 3.3v/2 
//

always @ (posedge ref_in or posedge osc_8k)
begin 
	if (ref_in)ref_OK <= 1'b0;   // ref_OK goes low if ref present so we can drive an LED
	else ref_OK <= 1'b1;		 // goes high if no reference 
end 

// select the signal to send to the loop LPF depending if the 10MHz reference is present
assign  TUNE = ref_OK ?  osc_8k : pfd_out; 

///////////////////////////////////////////////////////////
//
//    I2S receiver 
//
///////////////////////////////////////////////////////////

// receive I and Q data from Atlas bus in I2S format

reg [3:0] bit_count;     // how many bits clocked 
reg [2:0]IQ_state;
reg [15:0] I_data;
reg [15:0] Q_data;
wire CLRCLK;

assign CLRCLK = C9;

always @(posedge CBCLK)
begin
case(IQ_state)
0:	begin
	if (!CLRCLK)IQ_state <= 0;					// loop until CLRLCK is high
	else IQ_state <= 1;
	end
1:	if (CLRCLK)IQ_state <= 1;					// loop until CLRCLK is low
	else IQ_state <= 2;
2:	begin
	I_data[bit_count] <= IQOUT;					// get 16 bits of I data 
	bit_count <= bit_count - 1'b1;
		if (bit_count == 0)	IQ_state <= 3; 
		else IQ_state <= 2;  
	end
3:	if (!CLRCLK)IQ_state <= 3; 					// loop until CLRLCK is high
	else IQ_state <= 4;
4:	begin
	Q_data[bit_count] <= IQOUT;					// get 16 bits of Q data
	bit_count <= bit_count - 1'b1;
		if (bit_count == 0)	IQ_state <= 0; 		// done so start again
		else IQ_state <= 4;					
	end 
	default: IQ_state <= 0;
endcase
end

// sync I and Q data to 48kHz LR clock

reg [15:0] I_sync_data;
reg [15:0] Q_sync_data;

always @ (negedge CLRCLK)  // use negative edge so that both I and Q are from same frame.
begin 
	I_sync_data <= I_data;
	Q_sync_data <= Q_data;
end 

///////////////////////////////////////////////////////////
//
//    PWM DAC for I and Q outputs 
//
///////////////////////////////////////////////////////////

reg [16:0] I_accumulator;
reg [16:0] Q_accumulator;
reg [15:0] I_in;
reg [15:0] Q_in;


always @(negedge CLK_48MHZ)						// clock PWM at 48MHz
begin
        I_in <= I_sync_data + 16'h8000;         // so that 0 in gives 50:50 mark/space
        Q_in <= Q_sync_data + 16'h8000;
        I_accumulator <= I_accumulator[15:0] + I_in;
        Q_accumulator <= Q_accumulator[15:0] + Q_in;

end

assign IPWM = I_accumulator[16];       			// send to FPGA pins
assign QPWM = Q_accumulator[16];



//////////////////////////////////////////////////////////////
//
// 		CPLD to Atlas bus interface
//
/////////////////////////////////////////////////////////////

// Atlas outputs
assign C5 = CLK_12MHZ;		
assign SCLK = C6;
assign LRCLK = C7;
assign C10 = SDOUT; 
assign C11 = CDOUT;
assign C15 = (PTT == 0) ? 1'b1 : 1'bz; // PTT is 1 if active and high Z if not

// Atlas inputs
assign nRST = C2;			// reset AK5394A on power up
assign CBCLK = C8;
assign CLRCIN = C9; 		// LRCLK for TLV320
assign CLRCOUT = C9;		// LRCLK for TLV320
assign CDIN = C12;
assign DFS0 = C13; 			// set AK speed
assign DFS1 = C14; 			// set AK speed

// AK5394A pins
assign MCLK = CLK_12MHZ; 	// On board Master clock 
assign HPF = 1'b1; 			// HPF in AK on
assign SMODE1 = 1'b0; 		// Slave mode, I2S
assign SMODE2 = 1'b1; 		// Slave mode, I2S
assign ZCAL = 1'b1;			// Calibrate AK from A/D inputs

// LTV320 pins
assign CMCLK = CLK_12MHZ; 	// On board Master clock 
assign CMODE = 1'b0;		// Set to 0 for I2C
assign nCS = 1'b0;   		// this results in an i2c addr of 0x1a 

// Other pins 

assign LED1 = ref_OK;		  	// Green LED on when 10MHz reference signal is present
assign LED2 = (lock & ref_OK);  // Yellow LED on when loop is locked and we have a reference signal 
assign EXP4 = 1'b0;

endmodule 
