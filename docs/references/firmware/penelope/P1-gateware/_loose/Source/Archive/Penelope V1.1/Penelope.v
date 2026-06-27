// V1.1  23 Feb 2009 
//
// Copyright 2007, 2007, 2008, 2009 Phil Harman VK6APH
// Modified by Kirk Weedman KD7IRS - Dec 26, 2008
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  Penelope Exciter.
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



/*  
  This program interfaces the AD9744 ADC to PowerSDR over USB.
  The data to the AD9744  is in 14 bit parallel format and 
  is sent at the negative edge of the 122.88MHz clock.
  
  The 48kHz I and Q data from PowerSDR is interpolated by 2560 in a CIC filter to 
  give a data rate of 122.88MHz. The I and Q data in I2S format is taken from the 
  Atlas bus. 
  
  The data is processed by a CORDIC NCO and passed to the AD9744 DAC. 
  
  Command and control data is broadcast  over the Atlas bus (C20) for
  use by other cards e.g. Mercury and Penelope.  The data is in 
  I2S format with the clock being CBLCK and the start of each frame
  being indicated using the negative edge of CLRCLK.
  
  The data format is as follows:
  
  <PTT[48]><address[47:44]><frequency[43:12]><clock_select[11:8]><OC[7:1]><Mode[0]> 
  
  for a total of 49 bits. Frequency is in Hz and 32 bit binary format and 
  OC is the open collector data on Penelope.
  
  Change log:

   5  Apr  2008 - Production code released
   30 Dec  2008 - Moved to Quartus V8.1
   1  Jan  2009 - Add Kirk, KD7IRS, mods plus comment and code tidy
   13 Jan  2009 - Added software serial number to serno, Atlas C19
   19 Jan  2009 - Added ALC_in to serno data at posedge LRCLK
   20 Jan  2009 - Changed set_level to 0.8 = 0.8 * 65536 = 52428 = 16'hCCCC. 
   23 Feb  2009 - Released as V1.1

  
  
  TO DO:
  - inhibit RF output if PTT_out not active
    
  
*/


/*
  IMPORTANT:  In Analysis and Synthesis Settings make sure Power-Up Don't Care is NOT checked.
  Built with Quartus V8.1
*/

//    Atlas bus
//
//    Pin   Signal    Function
//    C12   CDIN      I2S format I&Q data from Ozy
//    C17   CLK_MCLK  12.288MHz master clock to Atlas bus
//    A11   CDOUT_P   I2S mic data to Ozy
//    C8    CBCLK     ~3MHZ clock from Atlas via Ozy
//    C9    CLRCLK    48kHz clock from Atlas via Ozy
//    C4    LRCLK     LR Rx Audio from Atlas via Ozy  
//    C20   CC        Command and Control data from Ozy       
//
////////////////////////////////////////////////////////////


module Penelope (
        _10MHZ,ext_10MHZ,_122MHZ,A5,A11,C4,C8,C9,C12,C17,LED2,LED3,LED4,LED5,LED6,LED7,
        USEROUT0,USEROUT1,USEROUT2,USEROUT3,USEROUT4,USEROUT5,USEROUT6,DAC,nLVDSRXE,LVDSTXE,
        FPGA_PLL,PTT,PTT_in,nCS,CMODE,CDOUT,CBCLK,CLRCIN,CLRCOUT,LROUT,CMCLK,CC,
        ADCMOSI,ADCCLK,ADCMISO,nADCCS,PWM0,PWM1,PWM2,FPGA_PTT,serno, BCLK, LRCLK
        );

input  wire _10MHZ;
inout  wire ext_10MHZ;  // 10MHz reference to Atlas pin C16
input  wire _122MHZ;
output wire A5;         // PCLK_12MHZ (12.288MHz) to Atlas bus
output wire A11;        // CDOUT_P (Mic) to Atlas bus 
input  wire C4;         // LROUT (Rx audio) from Atlas bus
input  wire C8;         // CBLCK from Atlas bus
input  wire C9;         // CLRCLK from Atlas bus
input  wire C12;        // CDIN (I&Q) from Atlas bus
input  wire C17;        // CLK_MCLK from Atlas bus
output wire LED2;
output wire LED3;
output wire LED4;
output wire LED5;
output wire LED6;
output wire LED7;
output wire USEROUT0;
output wire USEROUT1;
output wire USEROUT2;
output wire USEROUT3;
output wire USEROUT4;
output wire USEROUT5;
output wire USEROUT6;
output wire [13:0] DAC;
output wire nLVDSRXE;
output wire LVDSTXE;
output wire FPGA_PLL;
input  wire PTT;        // PTT from mic connector or DB25 pin 1
inout  wire PTT_in;     // PTT on Atlas bus - C15
output wire nCS;
output wire CMODE;
input  wire CDOUT;      // Mic or Line In out to Atlas Bus
output wire CBCLK;
output wire CLRCIN;
output wire CLRCOUT;
output wire LROUT;      // LR Rx audio from Atlas C4
output wire CMCLK;      // 12.288MHz CLK_MCLK from Atlas C17
input  wire CC;        // Command & Control data from Ozy C12
output wire ADCMOSI;
output wire ADCCLK;
input  wire ADCMISO;
output wire nADCCS;
output wire PWM0;
output wire PWM1;
output wire PWM2;
output wire FPGA_PTT;   // controls PTT FET Q3
inout  reg  serno;      // serial # out to Atlas bus on C19
input  wire BCLK;		// used by serial # 
input  wire LRCLK; 		// used by serial #

        
wire        CLK_MCLK;
wire        CDIN;
wire        CLRCLK;
wire        CLK_122MHZ;
wire        source_122MHZ;  // Set when internal 122.88MHz source is used and sent to LVDS

// link through FPGA where required

assign A11      = CDOUT;
assign CBCLK    = C8;
assign CLK_MCLK = C17;
assign LROUT    = C4;   // Rx audio (actually CDIN on TLV320) 
assign CDIN     = C12;  // I&Q from Atlas bus
assign CMODE    = 1'b0; // Set to 0 for I2C mode
assign nCS      = 1'b1; // I2C address of TLV320 is 0x1B

localparam SERIAL = 8'd11;  // software version serial number = 1.1

////////////////////////////////////////////////////////////////////////
//
//    Reset
//
////////////////////////////////////////////////////////////////////////

// holds clk_enable = 0 for first 1024 clock counts


reg clk_enable;

reg [10:0] reset_count;

always @ (posedge CLK_122MHZ)
begin
  if (reset_count[10])
    clk_enable  <= 1'b1;    // clk_enable high to run
  else
  begin
    clk_enable  <= 1'b0;
    reset_count <= reset_count + 11'b1;
  end
end 

//////////////////////////////////////////////////////////////
//
//    CLOCKS
//
//////////////////////////////////////////////////////////////

// Generate PCLK_12MHZ for Atlas bus A5


reg [2:0] CMCLK_counter;
reg PCLK_12MHZ;

always @ (posedge CLK_122MHZ)
begin
  if (CMCLK_counter == 4) // divide 122.88MHz clock by 10 to give 12.288MHz
  begin
    PCLK_12MHZ    <= ~PCLK_12MHZ;
    CMCLK_counter <= 3'b0;
  end
  else
    CMCLK_counter <= CMCLK_counter + 3'b1;
end

// send CLRCLK to TLV320, I2S and C&C decoders

assign CLRCIN  = C9;  // C9 on Altas bus is CLRCLK (48KHz)
assign CLRCOUT = C9;
assign CLRCLK  = C9;

assign CMCLK = CLK_MCLK;  // send CMCLK to TLV320 

// Select 122.88MHz source. If source_122MHZ set then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

assign CLK_122MHZ = _122MHZ; 
assign nLVDSRXE = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDSTXE  = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if  Penny is the source 

// send PCLK_12MHZ to Atlas A5 

assign A5 = PCLK_12MHZ; 

// select 10MHz reference source. If ref_ext is set use Penelope's 10MHz ref and send to Atlas C16

wire reference;
wire ref_ext;        // Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? _10MHZ : ext_10MHZ ; 
assign ext_10MHZ = ref_ext ? _10MHZ : 1'bZ ;    // C16 is bidirectional so set high Z if input. 


//////////////////////////////////////////////////////////////
//
//    ADC SPI interface 
//
//////////////////////////////////////////////////////////////

wire [11:0] AIN5;  // holds 12 bit ADC value of Forward Power detector.

ADC ADC_SPI(.clock(CBCLK), .SCLK(ADCCLK), .nCS(nADCCS), .MISO(ADCMISO), .MOSI(ADCMOSI), .AIN5(AIN5));



//////////////////////////////////////////////////////////////
//
//    Convert frequency to phase word 
//
//////////////////////////////////////////////////////////////

/*  
  Calculates  ratio of frequency/122.88Mhz
*/

reg  [31:0] frequency_HZ;   // frequency control bits for CORDIC
reg  [31:0] sync_frequency; // sync frequency change to 122MHz clock
wire [63:0] ratio;

localparam M2 = 32'd1172812403;  // B57 = 2^57.  B57/122880000 = M2
/*
 B scalar multiplication will be used to do the F/122.88Mhz function
 where: F * C = R
 0 <= F <= 65,000,000 hz
 C = 1/122,880,000 hz
 0 <= R < 1

 This method will use a 32 bit by 32 bit multiply to obtain the answer as follows:
 1. F will never be larger than 65,000,000 and it takes 26 bits to hold this value. This will
    be a B0 number since we dont need more resolution than 1 hz - i.e. fractions of a hertz.
 2. C is a constant.  Notice that the largest value we could multiply this constant by is B26
    and have a signed value less than 1.  Multiplying again by B31 would give us the biggest
    signed value we could hold in a 32 bit number.  Therefore we multiply by B57 (26+31).
    This gives a value of M2 = 1,172,812,403 (B57/122880000)
 3. Now if we multiply the B0 number by the B57 number (M2) we get a result that is a B57 number.
    This is the result of the desire single 32 bit by 32 bit multiply.  Now if we want a scaled
    32 bit signed number that has a range -1 <= R < 1, then we want a B31 number.  Thus we shift
    the 64 bit result right 32 bits (B57 -> B31) or merely select the appropriate bits of the
    64 bit result. Sweet!  However since R is always >= 0 we will use an unsigned B32 result
*/
reg [3:0] div_dly;
reg       freq_changed; // 1 clock wide pulse when new frequency is obtained from C & C data

assign ratio = frequency_HZ * M2; // B0 * B57 number = B57 number

always @ (posedge CLK_122MHZ)   // save frequency when ready is set
begin
  div_dly <= {div_dly[2:0], freq_changed};

// this should be a multi-cycle delay path since it takes 4 clock cycles
  if (div_dly[3]) // wait 4 clocks for 32bit by 32 bit multiply to settle out
    sync_frequency <= ratio[56:25]; // B57 -> B32 number since R is always >= 0
end 

////////////////////////////////////////////////////////////////
//
//  I2S Receiver for I and Q data on Atlas Bus
//
////////////////////////////////////////////////////////////////

// receive I and Q data from Atlas bus in I2S format
// I and Q data is on CDIN on pin C12 of Atlas Bus
// CBCLK timing WRT CLRCLK
//
// IQ_state
//    QW------>|<---QD-------------------------------------------------------->|<-IW---
//CLRCLK    +-------------------------------------------------------------------
//      ----+                                 
//
// IQ_state
//    IW------>|<---ID-------------------------------------------------------->|<-QW---
//      ----+                                 
//CLRCLK    +-------------------------------------------------------------------
//
//         +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ 
// CBCLK --+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ +-+ 
//
//                +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
// data ----------+D15+D14+D13+D12+D11+D10+D9 +D8 +D7 +D6 +D5 +D4 +D3 +D2 +D1 +D0 +-------  
//                +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+

reg  [3:0] IQ_cnt;     // how many bits clocked 
reg  [2:0] IQ_state;
reg  [2:0] IQ_state_next;
reg [15:0] I_data;
reg [15:0] Q_data;

localparam  IQ_IDLE = 0,
            IQ_IW   = 1,
            IQ_ID   = 2,
            IQ_QW   = 3,
            IQ_QD   = 4;

// sync I and Q data to 48kHz LR clock
reg [15:0] I_sync_data;
reg [15:0] Q_sync_data;

always @ (posedge CBCLK)
begin
  IQ_state <= IQ_state_next;

  if ((IQ_state == IQ_IW) && !CLRCLK)
    IQ_cnt <= 15;
  else if ((IQ_state == IQ_QW) && CLRCLK)
    IQ_cnt <= 15;
  else if (IQ_cnt != 0)
    IQ_cnt <= IQ_cnt - 4'b1;

  if (IQ_state == IQ_ID)
    I_data <= {I_data[14:0],CDIN};    // get 16 bits of I data 

  if (IQ_state == IQ_QD)
    Q_data <= {Q_data[14:0],CDIN};    // get 16 bits of Q data


  if ((IQ_state == IQ_IW) && !CLRCLK) // did CLRCLK just go low?
  begin // use negative CLRCLK edge so that both I and Q are from same frame.
    I_sync_data <= I_data; 
    Q_sync_data <= Q_data;
  end
end

always @*
begin
  case (IQ_state)
  IQ_IDLE:
  begin
    if (!CLRCLK)
      IQ_state_next = IQ_IDLE;    // wait here until CLRLCK is high
    else
      IQ_state_next = IQ_IW;
  end

  IQ_IW:
  begin
    if (CLRCLK)
      IQ_state_next = IQ_IW;      // wait here until CLRCLK is low
    else
      IQ_state_next = IQ_ID;
  end

  IQ_ID:
  begin
    if (IQ_cnt == 0)
      IQ_state_next = IQ_QW;
    else
      IQ_state_next = IQ_ID;      // I_data capture during this state
  end

  IQ_QW:
  begin
    if (!CLRCLK)
      IQ_state_next = IQ_QW;      // wait here until CLRCLK is high
    else
      IQ_state_next = IQ_QD;
  end

  IQ_QD:
  begin
    if (IQ_cnt == 0)
      IQ_state_next = IQ_IW;
    else
      IQ_state_next = IQ_QD;      // Q_data capture during this state
  end

  default:
    IQ_state_next = IQ_IDLE;
  endcase
end


////////////////////////////////////////////////////////////////
//
//  ALC
//
////////////////////////////////////////////////////////////////

// The following code provides fast attack and slow decay for the 
// ALC voltage. The output from the ALC ADC is compared with its
// previous sample. If higher, or the same, the new value is used.
// If lower, then the previous value is used but decremented by 1 each 
// time through the loop. This provides a (linear) slow decay of
// approximately 2 seconds. Extend ALC input to 21 bits to 
// get sufficient delay. 

/*

	This is how the ALC works.  Its basically a low gain control loop . The 
	inputs are a pre-set reference level (that determines the maximum RF 
	output level) and the other is the output of an ADC/Integrator  that 
	provides a voltage proportional to the RF envelope. The output is a 
	gain value (1...0.0000).

	In order to continuously measure the peak RF output a fast attack, slow 
	decay integrator is used at the output of the ADC. This prevents the 
	feedback voltage varying at speech rates and causing intermodulation 
	distortion.

	If the integrator voltage is < reference then do nothing since we have 
	not reached the max RF output yet. If the integrator is > reference 
	then reduce I and Q inputs to the DAC (by x gain) so that the max RF 
	output is not exceeded.  Since the loop gain is low this will not be 
	exact ( we are not trying to hold the output constant like in an AGC 
	system) but the long term trend will be that the Integrator output = 
	reference.

	The higher the values of I and Q then the lower to gain needs to be.
	
*/



localparam DEC_SEC = 2000;              // seconds*1000 => milliseconds
localparam ATT_SEC = 10;               // seconds*1000 => milliseconds
localparam CLK_RATE = 12288000/4/1000;  // speed of CBCLK/1000 = clocks/second/1000
localparam DECAY_RANGE = (DEC_SEC*CLK_RATE)>>16; // 2000 mS decay for full 16 bit range
localparam ATTACK_RANGE = (ATT_SEC*CLK_RATE)>>16; // 100 mS attack for full 16 bit range
localparam DR = clogb2(DECAY_RANGE);    // number of bits needed to hold range from 0 - DECAY_RANGE
localparam AR = clogb2(ATTACK_RANGE);   // number of bits needed to hold range from 0 - ATTACK_RANGE

wire    [15:0] ALC_in;
reg     [15:0] ALC_out;
reg     [DR:0] decay_cnt;
reg     [AR:0] attack_cnt;

assign ALC_in = {AIN5, 4'b0}; // convert to a 16 bit value range

always @ (posedge CBCLK)
begin
  if ((ALC_in < ALC_out) || (attack_cnt == ATTACK_RANGE))
    attack_cnt <= 0;
  else
    attack_cnt <= attack_cnt + 1; // ALC_in >= ALC_out so run attack counter

  if ((ALC_in >= ALC_out) || (decay_cnt == DECAY_RANGE))
    decay_cnt <= 0;
  else
    decay_cnt <= decay_cnt + 1; // ALC_in < ALC_out so run decay counter

  if ((ALC_in < ALC_out) && (decay_cnt == DECAY_RANGE))
    ALC_out <= ALC_out - 1'b1;  // slow decay
  else if ((ALC_in > ALC_out) && (attack_cnt == ATTACK_RANGE))
    ALC_out <= ALC_out + 1'b1;  // quick attack
end

wire [15:0] PWM0_Data;
wire [15:0] PWM1_Data;
wire [15:0] PWM2_Data;
reg  [15:0] ALC_i;
reg  [15:0] ALC_q;

reg  [11:0] ain5_122MHZ, ain0;

always @(posedge CLK_122MHZ)  // bring AIN5 into CLK_122MHZ domain
begin
  {ain5_122MHZ, ain0} <= {ain0, AIN5};
end

assign PWM0_Data = ALC_i;
assign PWM1_Data = {1'b0,ain5_122MHZ,3'd0}; // PWM1 has RF output envelope.
//assign PWM1_Data = ALC_q;
assign PWM2_Data = {1'b0,ALC_out[15:1]};    // PWM2 has ALC volts for testing


/*
  The Automatic Level Control (ALC) works as follows. The I and Q samples are multipled 
  by a value 'gain'. This is a fractional value such that 0.9999 is represented as 65535. 
  With no RF output the gain is set to 1 (actually 0.9999). When RF is produced this is
  converted into a DC level, linearized and fed to a 12 bit ADC. The output of the ADC is 
  then subtracted from the gain, hence as the RF output increase the gain reduces which
  in the asemtote is a preset level that corresponds to ~0.5W of RF output.
*/
wire [15:0] set_level;
wire [15:0] gain;
wire [15:0] ALC_level;

wire [15:0] res_i, res_q;
reg  [15:0] qx1;
reg  [15:0] qx0;
reg  [15:0] ix1;
reg  [15:0] ix0;

// unsigned => 0.9999 i.e. ~unity gain = (B16-1) = 2^16 - 1 = 65535 
assign set_level = 16'hCCCC;

assign ALC_level = {5'd0,ALC_out[15:5]}; // unsigned gain for ALC signal

assign gain = (set_level - ALC_level);

// use this to turn ALC off
// assign gain = set_level;

// signed multiply of I & Q by gain
ALC ALC_I(.out(res_i), .sample(I_sync_data), .multiply(gain));

ALC ALC_Q(.out(res_q), .sample(Q_sync_data), .multiply(gain));

always @(posedge CBCLK)  // 16 by 16 bit multiply should be possible in 1 CBCLK cycle
begin // restore sign in the final result
  ix0 <= res_i; 
  qx0 <= res_q;
end

// latch I & Q data into  CIC when ce_out_x goes high. 
wire        ce_out_i;   // narrow pulse when data required
wire        ce_out_q;   // narrow pulse when data required
reg  signed [15:0] cic_i;
reg  signed [15:0] cic_q;

always @ (posedge CLK_122MHZ)
begin
  {ALC_i, ix1} <= {ix1, ix0}; // From CBCLK domain into CLK_122MHZ domain
  if (ce_out_i)
    cic_i <= ALC_i;

  {ALC_q, qx1} <= {qx1, qx0}; // From CBCLK domain into CLK_122MHZ domain
  if (ce_out_q)
    cic_q <= ALC_q;
end 

////////////////////////////////////////////////////////////////
//
//  Interpolating CIC filter  R = 2560  N = 5
//
////////////////////////////////////////////////////////////////

wire [15:0] cic_out_i;
wire [15:0] cic_out_q;

cicint cic_I(.clk(CLK_122MHZ),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_i),.filter_out(cic_out_i),.ce_out(ce_out_i));
cicint cic_Q(.clk(CLK_122MHZ),.clk_enable(clk_enable),.reset(~clk_enable),.filter_in(cic_q),.filter_out(cic_out_q),.ce_out(ce_out_q));


//////////////////////////////////////////////////////////////
//
//    CORDIC NCO 
//
//////////////////////////////////////////////////////////////

// Code rotates input at set frequency and produces I & Q /

wire [17:0] i_out; 
wire [17:0] q_out; 
wire [31:0] phase;

// The phase accumulator takes a 32 bit frequency dword and outputs a 32 bit phase dword on each clock
phase_accumulator rx_phase_accumulator(.clk(CLK_122MHZ),.reset(~clk_enable),.frequency(sync_frequency),.phase_out(phase));


// The cordic takes I and Q in along with the top 15 bits of the phase dword.  The I and Q out are freq shifted
cordic_16 tx_cordic(.i_in(cic_out_q),.q_in(cic_out_i),.iout(i_out),.qout(q_out),.ain(phase[31:12]),.clk(CLK_122MHZ));


// NOTE:  I and Q inputs reversed to give correct sideband out - FIX THIS 

/* 
  We can use either the I or Q output from the CORDIC directly to drive the DAC.

    exp(jw) = cos(w) + j sin(w)

  When multplying two complex sinusoids f1 and f2, you get only f1+f2, no
  difference frequency.

      Z = exp(j*f1) * exp(j*f2) = exp(j*(f1+f2))
        = cos(f1 + f2) + j sin(f1 + f2)
*/

// Add some gain  before we feed the DAC so we can drive to 1/2W on 6m. This is necessary since the 
// interpolating CIC has a loss since it does not interpolate by 2^n. 

assign  DAC[13:0] = {i_out[17], i_out[15:3]};   // use q_out if 90 degree phase shift required by EER Tx etc


/////////////////////////////////////////////////////////////////
//
// Single bit PWM 16 bit D/A converters
//
/////////////////////////////////////////////////////////////////


reg  [15:0] PWM0_Data_in;
reg  [15:0] PWM1_Data_in;
reg  [15:0] PWM2_Data_in;
reg  [16:0] PWM0_accumulator;
reg  [16:0] PWM1_accumulator;
reg  [16:0] PWM2_accumulator;

// This runs off the 122.88MHz clock to provide adequate resolution.

always @(posedge CLK_122MHZ)
begin
  PWM0_Data_in      <= PWM0_Data + 16'h8000;         // so that 0 in gives 50:50 mark/space
  PWM1_Data_in      <= PWM1_Data + 16'h8000;
  PWM2_Data_in      <= PWM2_Data + 16'h8000;
  PWM0_accumulator  <= PWM0_accumulator[15:0] + PWM0_Data_in;
  PWM1_accumulator  <= PWM1_accumulator[15:0] + PWM1_Data_in;
  PWM2_accumulator  <= PWM2_accumulator[15:0] + PWM2_Data_in;
end

assign PWM0 = PWM0_accumulator[16];       // send to PWM LPFs for now 
assign PWM1 = PWM1_accumulator[16]; 
assign PWM2 = PWM2_accumulator[16]; 

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
  
  <PTT[48]><address[47:44]><frequency[43:12]><clock_select[11:8]><OC[7:1]><Mode[0]> 
  
  for a total of 49 bits. Frequency is in Hz and 32 bit binary format and 
  OC is the open collector data on Penelope.
  
  The clock source decodes as follows:
  
  0x00  = 10MHz reference from Atlas bus ie Gibraltar
  0x01  = 10MHz reference from Penelope
  0x10  = 10MHz reference from Mercury
  00xx  = 122.88MHz source from Penelope 
  01xx  = 122.88MHz source from Mercury 
  
*/

reg  [5:0] CC_cnt;    // how many bits clocked 
reg  [1:0] CC_state;
reg  [1:0] CC_state_next;
reg [48:0] CC_data; // 49 bits of C&C data
reg        CC_drdy; // new data ready

localparam CC_IDLE  = 0,
           CC_HI    = 1,
           CC_DATA  = 2;

always @(posedge CBCLK)  
begin
  CC_state <= CC_state_next;

  if ((CC_state == CC_HI) && !CLRCLK)
    CC_cnt <= 6'd48;
  else if (CC_cnt != 0)
    CC_cnt <= CC_cnt - 6'b1;

  if ((CC_state == CC_IDLE) && CLRCLK)
    CC_drdy <= 1'b1;
  else
    CC_drdy <= 1'b0;

  if (CC_state == CC_DATA)
    CC_data <= {CC_data[47:0],CC}; // shift the data in - muxing (CC_data[CC_cnt] <= CC) is much slower.
end

always @*
begin
  case (CC_state)
  CC_IDLE:
  begin
    if (!CLRCLK)
      CC_state_next = CC_IDLE;    // wait here until CLRLCK is high   
    else
      CC_state_next = CC_HI;
  end

  CC_HI:
  begin
    if (CLRCLK)
      CC_state_next = CC_HI;      // wait here until CLRCLK is low  
    else
      CC_state_next = CC_DATA;
  end

  CC_DATA:
  begin
    if (CC_cnt == 0)
      CC_state_next = CC_IDLE;    // done so restart
    else
      CC_state_next = CC_DATA;    // capture CC_data during this CLRCLK=0 time
  end

  default:
    CC_state_next = CC_IDLE;
  endcase
end


// decode C & C data into variables and sync to 48kHz LR clock

reg         PTT_out;       // PTT to Penelope
reg   [3:0] clock_select;  // 10MHz and 122.88MHz clock selection
reg   [6:0] OC;            // Open Collector outputs data
reg   [2:0] ccrdy;
parameter PENNY_ADDR = 4'b0; // set C&C address that Penny will respond to

always @ (posedge CLK_122MHZ)  
begin
  ccrdy <= {ccrdy[1:0], CC_drdy};
  
  if (ccrdy[2] & !ccrdy[1]) // time to update info? CC_data will be stable here
  begin  
    // local PTT also sets PTT_out active to avoid delay over USB link
    PTT_out       <= CC_data[48];
    if (CC_data[47:44] == PENNY_ADDR) 
    begin
      frequency_HZ  <= CC_data[43:12];
      freq_changed  <= (CC_data[43:12] != frequency_HZ) ? 1'b1 : 1'b0;
      clock_select  <= CC_data[11:8];    
      OC            <= CC_data[7:1];
    end
  end
  else
    freq_changed  <= 1'b0;
end

// if set use internal 10MHz TCXO and send to C16 else get from C16
assign ref_ext        = clock_select[0];
// if set use internally and send to LVDS else get from LVDS 
assign source_122MHZ  = ~clock_select[2];


///////////////////////////////////////////////////////////
//
//    Serial Number & Power out Encoder 
//
///////////////////////////////////////////////////////////

// Sends current software serial # as an 8 bit value in I2S
// format. Sends the serial number as bits 7 to 0 on 
// negative edge of LRCLK. 
// Sends ALC as 12 bits on positive edge of LRCLK

reg [2:0]serno_state;
reg [3:0]serno_data_count;

always @ (posedge BCLK)
begin	
case(serno_state)
0:  if (LRCLK)	 serno_state <= 1;	// loop until LRCLK is high
	else serno_state <= 0;
	
1:	if (LRCLK)	serno_state <= 1;	// wait until it goes low - this is first BCLK after negedge of LRCLK
	else begin
		serno_data_count <= 7; 		// need to have data available for Ozy on next BCLK
		serno_state <= 2;
	end
	
2:	if (serno_data_count == 0)
		serno_state <= 3;
	else begin
		serno_data_count <= serno_data_count - 1'b1;
		serno_state <= 2;
	end
	
3:	if (!LRCLK) serno_state <= 3;	// loop until LRCLK goes high
	else begin
		serno_data_count <= 11;
		serno_state <= 4;
	end 
	
4: if (serno_data_count == 0)
		serno_state <= 0;
	else begin
		serno_data_count <= serno_data_count - 1'b1;
		serno_state <= 4;
	end

default: serno_state <= 0;
endcase
end 

// serial number data must be available on the 2nd positive edge of BCLK after the LRCLK transition
always @ (negedge BCLK)
begin
	if (serno_state == 2)
		serno <= SERIAL[serno_data_count];	// format serial number
	else if (serno_state == 4)
		serno <= ALC_in[serno_data_count + 4];  // bits [15:4] are ALC 
	else serno <= 1'bz;						// serial # bus is high Z when not in use 
end


///////////////////////////////////////////////////////////
//
//    PTT 
//
///////////////////////////////////////////////////////////

/* 
  The Atlas bus has a tri-state PTT line (C15 PTT_in) that
  when high indicates  a board on the bus has requested PTT. 
  When inactive set high Z, a pull down resistor on the bus will 
  set PTT_in to logic 0.
*/

assign  PTT_in    = PTT ? 1'bz: 1'b1;  // set high if PTT active(low) else high Z 
assign  FPGA_PTT  = PTT_out;       // turn PTT FET Q3 on when Txing

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

always @ (posedge CLK_122MHZ)
begin
  if (count_12288 == 767)
  begin
    count_12288 <= 0;
    osc_80khz   <= ~osc_80khz; 
  end
  else
    count_12288 <= count_12288 + 10'b1;
end


// Apply to EXOR phase detector 
assign FPGA_PLL = ref_80khz ^ osc_80khz; 


/////////////////////////////////////////////////////////////////
//
// General I O pins
//
/////////////////////////////////////////////////////////////////


// LEDs for testing       PCB LED Marking
// NOTE: LED1 is power on 

// Bar graph for power output 
assign LED2 = (AIN5 > 250)?  1'b0 : 1'b1;  
assign LED3 = (AIN5 > 500)?  1'b0 : 1'b1; 
assign LED4 = (AIN5 > 1000)? 1'b0 : 1'b1;  
assign LED5 = (AIN5 > 2000)? 1'b0 : 1'b1;  
assign LED6 = (AIN5 > 3000)? 1'b0 : 1'b1;  

assign LED7 = 0;    // LED7 ON so we can see code has loaded OK 

// User open collector outputs 
assign USEROUT0 = OC[0];
assign USEROUT1 = OC[1];
assign USEROUT2 = OC[2];
assign USEROUT3 = OC[3];
assign USEROUT4 = OC[4];
assign USEROUT5 = OC[5];
assign USEROUT6 = OC[6];

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule 

