// V2.9  2009 
//
// Copyright 2007, 2007, 2008, 2009 Phil Harman VK6APH
// Copyright 2009 Kirk Weedman KD7IRS 
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
  use by other cards e.g. Mercury and Penelope.
  
  Change log:
  
  12 Apr 2009 - Modified to use NWirwe - Kirk Weedman, KD7IRS
  18 May 2009 - Released as V1.2 
  
*/


/*
  IMPORTANT:  In Analysis and Synthesis Settings make sure Power-Up Don't Care is NOT checked.
  Built with Quartus V9.0
*/

//    Atlas bus
//
//    Pin   Signal    Function
//    A11   CDOUT_P   mic data to Ozy
//    C12   J_CDIN    I2S I&Q data from Ozy to Janus
//    C19   P_CDIN    NWire I&Q data from Ozy to Penelope
//    C22   M_LR_clk  from Mercury to Ozy
//    C23   P_IQ_sync  from Penelope to Ozy
//    C20   CC        Command and Control data from Ozy       
//
////////////////////////////////////////////////////////////


module Penelope (
        _10MHZ,ext_10MHZ,C122_clk,A11,C19,C22,LED2,LED3,LED4,LED5,LED6,LED7,
        USEROUT0,USEROUT1,USEROUT2,USEROUT3,USEROUT4,USEROUT5,USEROUT6,DAC,nLVDSRXE,LVDSTXE,
        FPGA_PLL,PTT,PTT_in,nCS,CMODE,TLV320_CDIN,TLV320_CDOUT,TLV320_CBCLK,TLV320_CLRCIN,TLV320_CLRCOUT,TLV320_CMCLK,
        CC, ADCMOSI,ADCCLK,ADCMISO,nADCCS,PWM0,PWM1,PWM2,FPGA_PTT,A5
        );

input  wire _10MHZ;
inout  tri  ext_10MHZ;  // 10MHz reference to Atlas pin C16
input  wire C122_clk;
output wire A11;        // CDOUT_P (Mic) to Atlas bus 
input  wire C19;        // P_IQ_data (NWire I&Q) from Ozy
output wire C22;        // P_IQ_sync from Penelope to Ozy
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
output tri  PTT_in;     // PTT on Atlas bus - C15
output wire nCS;
output wire CMODE;
output wire TLV320_CDIN;
input  wire TLV320_CDOUT;      // Mic or Line In data from TLV320
output wire TLV320_CBCLK;
output wire TLV320_CLRCIN;
output wire TLV320_CLRCOUT;
output wire TLV320_CMCLK;      // 12.288MHz
input  wire CC;                // Command & Control data from Ozy C20
output wire ADCMOSI;
output wire ADCCLK;
input  wire ADCMISO;
output wire nADCCS;
output wire PWM0;
output wire PWM1;
output wire PWM2;
output wire FPGA_PTT;   // controls PTT FET Q3
output wire A5;         // serial # out to Atlas bus on A5

wire   source_122MHZ;   // Set when internal 122.88MHz source is used and sent to LVDS

assign CMODE    = 1'b0; // Set to 0 for I2C mode
assign nCS      = 1'b1; // I2C address of TLV320 is 0x1B

localparam SERIAL = 8'd12;  // software version serial number = 1.2

localparam C122_TPD = 1.3;
//////////////////////////////////////////////////////////////
//
//		Reset
//
//////////////////////////////////////////////////////////////

reg C122_rst;
reg [10:0] C122_rst_cnt;

always @(posedge C122_clk)
begin
  if (!C122_rst_cnt[10])
    C122_rst_cnt <= #C122_TPD C122_rst_cnt + 1'b1;

  C122_rst <= #C122_TPD C122_rst_cnt[10] ? 1'b0 : 1'b1;
end

//////////////////////////////////////////////////////////////
//
//    CLOCKS
//
//////////////////////////////////////////////////////////////

localparam SPEED_48K = 2'b00;

reg        C122_DFS1, C122_DFS0;
reg        C122_cgen_rst;
reg  [1:0] C122_SPEED;

// create a slower system clock = 122.88Mhz / 10 = 12.288Mhz
clk_div TLVCLK (.reset(C122_rst), .clk_in(C122_clk), .clk_out(TLV320_CMCLK));

// make sure CBCLK/CLRCLK and BCLK/LRCLK stay synced when speed changes
always @(posedge C122_clk)
begin
  if (C122_rst)
    C122_SPEED <= 2'b00;
  else if (C122_SPEED != {C122_DFS1,C122_DFS0})
    C122_SPEED <= {C122_DFS1,C122_DFS0};

  if (C122_rst)
    C122_cgen_rst <= 1'b1;
  else if (C122_SPEED != {C122_DFS1,C122_DFS0})
    C122_cgen_rst <= 1'b1;
  else
    C122_cgen_rst <= 1'b0;
end

wire C122_cbrise, C122_cbfall;
wire C122_CBCLK, CLRCLK;

clk_lrclk_gen clrgen 
    (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(C122_CBCLK),
     .Brise(C122_cbrise), .Bfall(C122_cbfall), .LRCLK(CLRCLK), .Speed(SPEED_48K));

assign TLV320_CBCLK   = C122_CBCLK;
assign TLV320_CLRCIN  = CLRCLK;
assign TLV320_CLRCOUT = CLRCLK;

wire LRfall;
clk_lrclk_gen lrgen (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(), .LRCLK(),
                     .LRfall(LRfall), .Speed({C122_DFS1,C122_DFS0}));

// Select 122.88MHz source. If source_122MHZ set then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

assign nLVDSRXE = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDSTXE  = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if Penny is the source 


// select 10MHz reference source. If ref_ext is set use Penelope's 10MHz ref and send to Atlas C16

wire reference;
wire ref_ext;        // Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? _10MHZ : ext_10MHZ; 
assign ext_10MHZ = ref_ext ? _10MHZ : 1'bz;    // C16 is bidirectional so set high Z if input. 


//////////////////////////////////////////////////////////////
//
//    ADC SPI interface 
//
//////////////////////////////////////////////////////////////

wire [11:0] AIN5;  // holds 12 bit ADC value of Forward Power detector.

ADC ADC_SPI(.clock(C122_CBCLK), .SCLK(ADCCLK), .nCS(nADCCS), .MISO(ADCMISO), .MOSI(ADCMOSI), .AIN5(AIN5));



//////////////////////////////////////////////////////////////
//
//    Convert frequency to phase word
//
//    Calculates  ratio of frequency/122.88Mhz
//
//////////////////////////////////////////////////////////////
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
reg   [31:0] C122_frequency_HZ;   // frequency control bits for CORDIC
reg   [31:0] C122_last_freq;
reg   [31:0] C122_phase_word;
wire  [63:0] C122_ratio;

localparam M2 = 32'd1172812403;  // B57 = 2^57.   M2 = B57/122880000

assign C122_ratio = C122_frequency_HZ * M2; // B0 * B57 number = B57 number

always @ (posedge C122_clk)
begin
  if (C122_cbfall) // time between C122_cbfall is enough for C122_ratio calculation to settle
  begin
    C122_last_freq    <= C122_frequency_HZ;
    if (C122_last_freq != C122_frequency_HZ) // frequency changed
      C122_phase_word <= C122_ratio[56:25]; // B57 -> B32 number since R is always >= 0  
  end
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

always @ (posedge C122_clk)
begin
  if (C122_cbfall)
  begin
    if ((ALC_in < ALC_out) || (attack_cnt == ATTACK_RANGE))
      attack_cnt <= 0;
    else
      attack_cnt <= attack_cnt + 1'b1; // ALC_in >= ALC_out so run attack counter

    if ((ALC_in >= ALC_out) || (decay_cnt == DECAY_RANGE))
      decay_cnt <= 0;
    else
      decay_cnt <= decay_cnt + 1'b1; // ALC_in < ALC_out so run decay counter

    if ((ALC_in < ALC_out) && (decay_cnt == DECAY_RANGE))
      ALC_out <= ALC_out - 1'b1;  // slow decay
    else if ((ALC_in > ALC_out) && (attack_cnt == ATTACK_RANGE))
      ALC_out <= ALC_out + 1'b1;  // quick attack
  end
end

wire [15:0] PWM0_Data;
wire [15:0] PWM1_Data;
wire [15:0] PWM2_Data;
reg  [15:0] C122_ALC_i;
reg  [15:0] C122_ALC_q;

reg  [11:0] C122_ain5;

always @(posedge C122_clk)  // bring AIN5 into C122_clk domain
begin: CB_AIN5_TO_C122
  reg  [11:0] ain1, ain0;

  {ain1, ain0} <= {ain0, AIN5};
  if (C122_cbfall)
    C122_ain5 <= ain1;
end

assign PWM0_Data = C122_ALC_i;
assign PWM1_Data = {1'b0,C122_ain5,3'd0}; // PWM1 has RF output envelope.
//assign PWM1_Data = C122_ALC_q;
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

wire [15:0] C122_res_i, C122_res_q;
reg  [15:0] C122_qx0;
reg  [15:0] C122_ix0;

wire [15:0] I_sync_data;
wire [15:0] Q_sync_data;

// unsigned => 0.9999 i.e. ~unity gain = (B16-1) = 2^16 - 1 = 65535 
assign set_level = 16'hCCCC;

assign ALC_level = {5'd0,ALC_out[15:5]}; // unsigned gain for ALC signal

assign gain = (set_level - ALC_level);

// use this to turn ALC off
// assign gain = set_level;

// signed multiply of I & Q by gain
ALC ALC_I(.out(C122_res_i), .sample(I_sync_data), .multiply(gain));

ALC ALC_Q(.out(C122_res_q), .sample(Q_sync_data), .multiply(gain));

// latch I & Q data into  CIC when ce_out_x goes high. 
wire               C122_ce_out_i;   // narrow pulse when data required
wire               C122_ce_out_q;   // narrow pulse when data required
reg  signed [15:0] C122_cic_i;
reg  signed [15:0] C122_cic_q;

always @ (posedge C122_clk)
begin
  if (C122_cbfall) // 16 by 16 bit multiply should be possible in 1 CBCLK cycle
  begin
    C122_ALC_i <= C122_res_i; 
    C122_ALC_q <= C122_res_q;
  end

  if (C122_ce_out_i)
    C122_cic_i <= C122_ALC_i;

  if (C122_ce_out_q)
    C122_cic_q <= C122_ALC_q;
end 

////////////////////////////////////////////////////////////////
//
//  Interpolating CIC filter  R = 2560  N = 5
//
////////////////////////////////////////////////////////////////

wire [15:0] C122_cic_out_i;
wire [15:0] C122_cic_out_q;

cicint cic_I(.clk(C122_clk), .clk_enable(1'b1), .reset(C122_rst), .filter_in(C122_cic_i),
             .filter_out(C122_cic_out_i), .ce_out(C122_ce_out_i));
cicint cic_Q(.clk(C122_clk), .clk_enable(1'b1), .reset(C122_rst), .filter_in(C122_cic_q),
             .filter_out(C122_cic_out_q), .ce_out(C122_ce_out_q));


//////////////////////////////////////////////////////////////
//
//    CORDIC NCO 
//
//////////////////////////////////////////////////////////////

// Code rotates input at set frequency and produces I & Q /

wire [17:0] C122_i_out; 
wire [17:0] C122_q_out; 
wire [31:0] C122_phase;

// The phase accumulator takes a 32 bit frequency dword and outputs a 32 bit phase dword on each clock
phase_accumulator rx_phase_accumulator
  (.clk(C122_clk), .reset(C122_rst), .frequency(C122_phase_word), .phase_out(C122_phase));


// The cordic takes I and Q in along with the top 15 bits of the phase dword.
// The I and Q out are freq shifted
cordic_16 tx_cordic
      (.i_in(C122_cic_out_q), .q_in(C122_cic_out_i),
       .iout(C122_i_out), .qout(C122_q_out), .ain(C122_phase[31:12]), .clk(C122_clk));


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

assign  DAC[13:0] = {C122_i_out[17], C122_i_out[15:3]};   // use q_out if 90 degree phase shift required by EER Tx etc


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

always @(posedge C122_clk)
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
	The C&C encoder broadcasts data over the Atlas bus C20 for
	use by other cards e.g. Mercury and Penelope.
	
	The data fomat is as follows:
	
	<[60:59]DFS1,DFS0><[58]PTT><[57:54]address><[53:22]frequency><[21:18]clock_select><[17:11]OC>
	<[10]Mode><[9]PGA><[8]DITHER><[7]RAND><[6:5]ATTEN><[4:3]TX_relay><[2]Rout><[1:0]RX_relay> 
		
	for a total of 61 bits. Frequency is in Hz and 32 bit binary format and 
	OC is the open collector data on Penelope. Mode is for a future Class E PA,
	PGA, DITHER and RAND are ADC settings and ATTEN the attenuator on Alex
	
	The clock source (clock_select) decodes as follows:
	
	0x00  = 10MHz reference from Atlas bus ie Gibraltar
	0x01  = 10MHz reference from Penelope
	0x10  = 10MHz reference from Mercury
	00xx  = 122.88MHz source from Penelope 
	01xx  = 122.88MHz source from Mercury 
	
*/
wire   [60:0] rcv_data;
wire          rcv_rdy;

// get necessary C & C data

reg         PTT_out;       // PTT to Penelope
reg   [3:0] C122_clock_select;  // 10MHz and 122.88MHz clock selection
reg   [6:0] OC;            // Open Collector outputs data
parameter PENNY_ADDR = 4'b0; // set C&C address that Penny will respond to

always @ (posedge C122_clk)
begin
  if (C122_rst)
  begin
    C122_DFS1         <= 1'b0;   // I/Q sampling rate selection
    C122_DFS0         <= 1'b0;
    PTT_out           <= 1'b0; 
    C122_frequency_HZ <= 32'b0;
    C122_clock_select <= 4'b0000;     
    OC                <= 7'b0;   // Penelope Open Collectors
  end
  else if (rcv_rdy)
  begin
    C122_DFS1    <= rcv_data[60];
    C122_DFS0    <= rcv_data[59];
    PTT_out      <= rcv_data[58];
    if (rcv_data[57:54] == PENNY_ADDR) // check that the C&C data is for this board
    begin
      C122_frequency_HZ <= rcv_data[53:22];
      C122_clock_select <= rcv_data[21:18];     
      OC                <= rcv_data[17:11]; // Penelope Open Collectors
    end
  end
end

NWire_rcv  #(.DATA_BITS(61), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(500)) 
      CCrcv (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xrcv_data(rcv_data), .xrcv_rdy(rcv_rdy), .xrcv_ack(rcv_rdy), .din(CC));

// if set use internal 10MHz TCXO and send to C16 else get from C16
assign ref_ext        = C122_clock_select[0];
// if set use internally and send to LVDS else get from LVDS 
assign source_122MHZ  = (C122_clock_select[3:2] == 2'b00);

////////////////////////////////////////////////////////////////
//
//		Get P_IQ_data (Atlas C19) and then synchronize it to CBCLK/CLRCLK 
//
//////////////////////////////////////////////////////////////
wire        IQ_rdy;

NWire_rcv #(.DATA_BITS(32), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000))
     IQPWM (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
            .xrcv_rdy(IQ_rdy), .xrcv_ack(IQ_rdy), .xrcv_data({I_sync_data, Q_sync_data}),
            .din(C19));
                 
assign C22 = CLRCLK; // P_IQ_sync -> so Ozy knows when to send P_IQ_data

I2S_xmit #(.DATA_BITS(32))  // CLRCLK running at 48KHz
  IQD (.rst(C122_rst), .lrclk(CLRCLK), .clk(C122_clk), .CBrise(C122_cbrise),
       .CBfall(C122_cbfall), .sample({I_sync_data,Q_sync_data}), .outbit(TLV320_CDIN));

///////////////////////////////////////////////////////////
//
// send data to CDOUT_P (Atlas A11)
//
///////////////////////////////////////////////////////////
reg  [15:0] C122_mic_data;
wire        C122_mic_rdy;
wire [31:0] C122_mic_LR;

NWire_xmit #(.SEND_FREQ(48000),.DATA_BITS(16), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
      P_MIC (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(C122_mic_data), .xreq(LRfall), .xrdy(), .xack(), .dout(A11));

always @(posedge C122_clk)
begin
  if (C122_mic_rdy) // this happens before LRfall
    C122_mic_data <= C122_mic_LR[31:16]; // we're only using the Left data
end

I2S_rcv #(32,2,1) // WARNING: values 2,1 may need adjusting for best capture of data
   PJD (.xrst(C122_rst), .xclk(C122_clk), .xData(C122_mic_LR),
        .xData_rdy(C122_mic_rdy), .BCLK(C122_CBCLK), .LRCLK(CLRCLK), .din(TLV320_CDOUT));
///////////////////////////////////////////////////////////
//
//    Serial Number & Power out Encoder 
//
///////////////////////////////////////////////////////////

// Sends current software serial # as an 8 bit value.
// Sends ALC as 12 bits

wire [19:0] xmit_data;

assign xmit_data = {SERIAL,ALC_in[15:4]};

NWire_xmit  #(.DATA_BITS(20), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
      ser_no (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
              .xdata(xmit_data), .xreq(1'b1), .xrdy(), .xack(), .dout(A5));

///////////////////////////////////////////////////////////
//
//    PTT 
//
///////////////////////////////////////////////////////////

/* 
  The Atlas bus has a tri-state PTT line (C15 = PTT_in) that
  when high indicates a board on the bus has requested PTT. 
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

always @ (posedge C122_clk)
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

