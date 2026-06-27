//
//  HPSDR - High Performance Software Defined Radio
//
//  Metis code. 
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


/*  12 September 2011 - Metis code V1.5 - copyright 2010, 2011 Phil Harman VK6APH 


	Change Log:
		2011 March 02 - added microphone or line in selection
		           04 - add support for multiple Mercury receivers/boards - Joe K5SO
				   12 - added DHCP server address to DHCPREQUEST.
				   28 - Modify code such that multiple Metis boards can be used. Allow Discovery from different PC to that sending
				        Start/Stop. Save PC IP address etc independantly for Discovery and other commands.
			 Apr    2 - Reset sequence number when not running rather then when Discovery received.  
			        3 - Add wide band spectrum and on/off enable
			 May   13 - Added independant serial number for wide bandscope data.
			 July  29 - Added FWD and REV power from Penny(Lane)
			 Sep   12 - Added support for manual selection of Alex relays. Released as V1.5.
			          - Added PennyLane/Hermes Drive level to existing C&C data rather than an additional nWire. Removed C18 connection						 
						**** Built using Quartus V9.1 sp2 ****

		2012 Oct   3 - changes by Joe K5SO 
						 - Added line in gain (line_boost) control for Penny(Lane)
						 - Fixed bug in C&C byte stream selection/assignments of frequencies for multiple receivers
						 - Added independent manual control of HPF/LPF relays for Alex 2
						 - Added user output connections for pins 1-4 on the DB-9 connector on Metis, mapped to 
						   bits C3[3:0] when C0[7:1] = 0001_010 as follows: 
								USEROUT0: DB9 pin 1 = C3[0] open drain output, FPGA pin 134
								USEROUT1: DB9 pin 2 = C3[1] open drain output, FPGA pin 135
								USEROUT2: DB9 pin 3 = C3[2] 3.3V TTL output, FPGA pin 137
								USEROUT3: DB9 pin 4 = C3[3] 3.3V TTL output, FPGA pin 173
						 - Released as version number v1.9 
						 **** Built using Quartus II V11.1 sp2 ****
						 
	   2012 Oct 21 - changes by Joe K5SO
						- added default power up clock select for 122.88 MHz clock to be Penelope
						- Released as version number v2.0
						**** Bulit using Quartus II V11.1 sp2 ****

		2012 Oct 26 - changes by Joe K5SO
						- fixed bug in Rx freq assignments that resulted in improper freq assignments 
						  when certain hardware configurations/PC options are selected
						- Released as version number v2.1
						
		2012 Dec  3 - Compiler configuration changes to eliminate hangs on certain FPGAs


		
						
						**** Built using Quartus II V11.1 sp2 ****
			    
	NOTES:  	  	- Under Settings > Analysis & Synthesis Settings > Verilig HDL Input, select SystemVerilog-2005 so can pass RAM to a module

					
	LEDs  - LED[0] is located at the top of the board
	
	LED[0]  	- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
				  and swap between fast and slow flash if not full duplex
	LED[1]		- Lights when the PHY receives Ethernet traffic
	LED[2]  	- Lights when the PHY transmits Ethernet traffic
	LED[3]  	- Lights when an Ethernet broadcast is detected
	LED[4]  	- Lights when traffic to the boards MAC address is detected
	LED[5]  	- Lights when an ARP request is received
	LED[6]  	- Displays state of DHCP negotiations or static IP - off if not full duplex, on if DHCP ACK, slow flash if DHCP NAK,
	              fast flash if DHCP time out and long then short flash if static IP. If NAK or time out an IPIPA IP address will be used.
	LED[7]  	- Lights when a ping request is received
	LED[8]  	- Lights when a Metis discovery packet is received
	LED[9]  	- Lights when a Metis discovery packet reply is sent
	LED[10]  	- Lights when 0x7F7F7F received from PC
	LED[11]     - Lights when we detect a receive sequence error or EPCS16 is being erased or programmed
	LED[12]     - HEART_BEAT  - Flashes once per second
					


					
	Timing:			- Each UDP/IP frame payload from the PC consists of 2 x HPSDR format USB frames. Each USB frame is composed of 512 bytes
						
						<sync><sync><sync><C0><C1><C2><C3><C4><Lh><Ll><Rh><Rl><Ih><Il><Qh><Ql>..............
					  
					  Hence there are 2*(512-8)/8 = 126  L,R,I and Q 16 bit samples per UDP frame. 
					  Samples need to be sent at 48ksps. Hence number of UDP frames per second = 48000/126 = 381.
					  Therefore the maximum time between frames is  1000/381 = 2.62mS
					
*/

module Metis (
			  ATLAS_A2,ATLAS_A3,ATLAS_A4,ATLAS_A5,  
			  ATLAS_A6,ATLAS_A7,ATLAS_A8,ATLAS_A9, 
			  ATLAS_A10,ATLAS_A11, ATLAS_A12,ATLAS_A13,ATLAS_A20,ATLAS_A21,
			  ATLAS_C2,ATLAS_C15,ATLAS_C19,ATLAS_C20,ATLAS_C21,ATLAS_C22,ATLAS_C23,ATLAS_C24,
			  RAM_A0,RAM_A1,RAM_A2,RAM_A3,RAM_A4,RAM_A5,RAM_A6,RAM_A7,RAM_A8,RAM_A9,RAM_A10,RAM_A11,RAM_A12,RAM_A13,HEART_BEAT,
			  PHY_TX,PHY_RX,PHY_DV,PHY_TX_CLOCK,PHY_TX_EN,
			  PHY_RX_CLOCK,PHY_CLK125,PHY_MDIO,PHY_MDC,PHY_INT_N,PHY_RESET_N,CLK_25MHZ,
			  MODE2, IN2, IN1, IN0, SCK, SI, CONFIG, NODE_ADDR_CS, NCONFIG, USEROUT0,USEROUT1,USEROUT2,USEROUT3
			  );

		   
// Atlas Bus

input   wire     ATLAS_A2;  // Mercury4 firmware version number & ADC overload (Multiple Rx-3)
input   wire     ATLAS_A3;  // Mercury3 firmware version number & ADC overload (Multiple Rx-2)
input   wire     ATLAS_A4;  // Mercury2 firmware version number & ADC overload (Multiple Rx-1)
input	wire	 ATLAS_A5;  // P_SER		Penelope NWire serial number, etc 
input	wire	 ATLAS_A6;  // Mercury1 firmware version number & ADC overload (Multiple Rx-0) 
input	wire	 ATLAS_A7;  // MDOUT[3]		I&Q from Mercury - Multiple Rx-3
input	wire	 ATLAS_A8;  // MDOUT[2]		I&Q from Mercury - Multiple Rx-2
input	wire	 ATLAS_A9;  // MDOUT[1]		I&Q from Mercury - Multiple Rx-1
input	wire	 ATLAS_A10; // MDOUT[0]		I&Q from Mercury - Multiple Rx-0
input	wire	 ATLAS_A11; // CDOUT_P		Mic from TLV320 on Penelope

input	wire	 ATLAS_A12; 	// SP_DATA		NWire spectrum data from Mercury
input wire   ATLAS_A13;    // 1PPS from GPS receiver to time stamp data for Chirp signals
output wire  ATLAS_A20; 	// I2C SCL
output wire  ATLAS_A21; 	// I2C SDA
output wire  ATLAS_C2;     // reset Mercury boards
input	wire	 ATLAS_C15; 	// PTT_in		PTT input from Atlas bus - active high
// ATLAS_A19 is not connected since POR chip 
output wire	 ATLAS_C19; 	// P_IQ_data to Penelope
output wire	 ATLAS_C20; 	// CC			Command & Control
output wire	 ATLAS_C21; 	// trigger		Spectrum data Trigger signal to Mercury
input	wire	 ATLAS_C22; 	// P_IQ_sync	P_IQ_sync from Penelope
input	wire	 ATLAS_C23; 	// M_LR_sync	M_LR_sync from Mercury
output wire	 ATLAS_C24; 	// M_LR_data	M_LR_data to Mercury and Penny in nWire format

// user IO
input wire        IN0;
input wire        IN1;
input wire 	      IN2;


// MAC EEPROM
output wire		SCK; 					// clock on MAC EEPROM
output wire		SI;						// serial in on MAC EEPROM
input  wire 	CONFIG; 				// SO on MAC EEPROM
output wire 	NODE_ADDR_CS;			// CS on MAC EEPROM
wire SO;	assign SO = CONFIG;			// serial out on MAC EEPROM
wire CS;	assign NODE_ADDR_CS = CS; 	// Chip select on MAC EEPROM

// CW key and PTT
wire dash_n;	assign dash_n 	= IN0; 	// CW dash key, DP9 pin 6, active low
wire dot_n;		assign dot_n 	= IN1; 	// CW dot key, DB9 pin 7, active low
wire PTT_n; 	assign PTT_n  	= IN2; 	// PTT from DB9 pin 8, active low

// Reload FPGA 
output wire 	NCONFIG;				// when high causes FPGA to reload from EPCS16 

//DB-9 connector output connections
output wire		USEROUT0;				// DB-9 pin 1, FPGA pin 134 open drain output
output wire		USEROUT1;				// DB-9 pin 2, FPGA pin 135 open drain output
output wire		USEROUT2;				// DB-9 pin 3, FPGA pin 137 3.3V TTL output
output wire		USEROUT3;				// DB-9 pin 4, FPGA pin 173 3.3V TTL output

// Assign Atlas pins to signals
wire PTT_in;	assign PTT_in   	= ATLAS_C15;  	// PTT from Atlas, Active high
wire CC;	    assign ATLAS_C20	= CC;			// Command & Control data
wire CDOUT_P;	assign CDOUT_P  	= ATLAS_A11;	// Mic data from Penelope


// RAM
output	wire     RAM_A0;		
output	wire     RAM_A1;
output	wire     RAM_A2;
output	wire     RAM_A3;
output	wire     RAM_A4;
output	wire     RAM_A5;
output	wire     RAM_A6;
output	wire     RAM_A7;
output	wire     RAM_A8;
output	wire     RAM_A9;
output	wire     RAM_A10;
output	wire     RAM_A11;
output	wire     RAM_A12;
output	wire     RAM_A13;



// PHY
output  wire [3:0]PHY_TX;
input   wire [3:0]PHY_RX;		   
input	wire     PHY_DV;					// PHY has data flag
output	wire     PHY_TX_CLOCK;		// PHY Tx data clock
output	wire	 PHY_TX_EN;				// PHY Tx enable
input	wire	 PHY_RX_CLOCK;      		// PHY Rx data clock
input	wire     PHY_CLK125;				// 125MHz clock from PHY PLL
inout	wire     PHY_MDIO;				// data line to PHY MDIO
output	wire 	 PHY_MDC;				// 2.5MHz clock to PHY MDIO
input	wire 	 PHY_INT_N;	
output	wire     PHY_RESET_N;
input	wire 	 CLK_25MHZ;					// 25MHz clock 

// Heart beat LED
output wire 	  HEART_BEAT;			// LED, flashes, runs off PHY 125MHz clock.

// speed select via JP2
input wire MODE2;							// high with jumper off

parameter M_TPD   = 4;
parameter IF_TPD  = 2;

localparam Metis_serialno = 8'd22;	// Serial number of this version
localparam NR = 4;                  //Maximum number of receiver channels

localparam RX_FIFO_SZ  = 4096; 		// 16 by 4096 deep RX FIFO
localparam TX_FIFO_SZ  = 1024; 		// 16 by 1024 deep TX FIFO  
localparam SP_FIFO_SZ  = 1024; 		// 16 by 1024 deep SP FIFO

wire [NR-1:0] MDOUT;       			// I&Q data from Mercury 
assign  MDOUT = {ATLAS_A7,ATLAS_A8,ATLAS_A9,ATLAS_A10};

wire PPS;									// 1PPS from GPS receiver to time stamp data
assign PPS = ATLAS_A13;

//------------------------------------------------------------
//		Clocks
//------------------------------------------------------------

wire C125_clk; 	assign C125_clk = PHY_CLK125;	// use PHY 125MHz clock for system clock
wire Tx_clock;
wire Tx_clock_2;
wire C125_locked; // high when PLL locked
wire IF_clk;
wire PHY_data_clock;
wire I2C_clock;
wire PHY_speed;				// 0 = 100T, 1 = 1000T
wire EEPROM_clock;		

// use PLL to generate 48MHz (as a replacement for the FX2 clock), 25MHz and 12.5MHz from 125MHz plus 800kHz for I2C.
// C0 = 48MHz, C1 = 25MHz, C2 = 12.5MHz, C3 = 800kHz

PLL_clocks PLL_inst(.areset(), .inclk0(C125_clk), .c0(IF_clk), .c1(Tx_clock), .c2(Tx_clock_2), .c3(I2C_clock), .locked(C125_locked));

assign PHY_TX_CLOCK = ~Tx_clock;

// divide 25MHz clock by 10 for PHY MDIO clock
reg PHY_MDIO_clk;
reg [4:0]PHY_count;
reg [27:0] C2_cnt;

always @ (posedge Tx_clock)
begin 
	if (PHY_count == 4) begin
		PHY_MDIO_clk <= ~PHY_MDIO_clk;
		PHY_count <= 0;
	end 
	else PHY_count <= PHY_count + 1'b1;
end

assign EEPROM_clock = PHY_MDIO_clk; // 2.5MHz

assign PHY_speed = MODE2;		// high for 1000T, low for 100T

// select data clock speed based on JP2 and speed that network is running at
// assign PHY_data_clock = (PHY_speed & speed_1000T) ? PHY_RX_CLOCK : ~PHY_100T_state;

// force 100T for now 
assign PHY_data_clock = ~PHY_100T_state;

//--------------------------------------------------------------
// Reset Lines - C125_rst, IF_rst
//--------------------------------------------------------------

wire  C125_rst;
wire  IF_rst;

assign C125_rst  = (!C125_locked || reset);
assign IF_rst = (C125_rst || reset);

assign PHY_RESET_N = 1'b1;  			// Allow PYH to run for now

localparam read_reg_address = 5'd31; 	// PHY register to read from - gives connect speed and fully duplex

//------------------------------------------------------------
//  Reset and initialisation
//------------------------------------------------------------

/* 
	Hold the code in reset whilst we do the following:
	
	Get the boards MAC address from the EEPROM.
	
	Then setup the PHY registers and read from the PHY until it indicates it has 
	negotiated a speed.  Read connection speed and that we are running full duplex.
	
	LED0 incates PHY status - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	
	Then wait a second (for the network to stabilise) then  attempt to obtain an IP address using DHCP
	- supplied address is in YIADDR.  If the DHCP request either times out, or results in a NAK, retry four 
	additional times with a 2 second delay between each retry.
	
	If after the retries a DHCP assigned IP address is not available use an APIPA IP address or an assigned one
	from Flash.
	
	Inhibit replying to a Metis Discovery request until an IP address has been applied.
	
	LED6 indicates the result of DHCP - on if ACK, slow flash if NAK, fast flash if time out and 
	long then short flash if static IP
	
	Once an IP address has been assigned set IP_valid flag. When set enables a response to a Discovery request.
	
	Wait for a Metis discovery frame - once received enable HPSDR data to PC.
	
	Enable rest of code.
	
*/

reg reset;
reg [4:0]start_up;
reg [47:0]This_MAC; 	// holds the MAC address of this Metis board
reg read_MAC; 
wire MAC_ready;
reg DHCP_start;
reg [24:0]delay;
reg duplex;				// set when we are connected full duplex
reg speed_100T;			// set when we are connected at 100MHz
reg speed_1000T;		// set when we are connected at 1GHz
reg Tx_reset;			// when set prevents HPSDR UDP/IP Tx data being sent
reg [2:0]DHCP_retries;	// DHCP retry counter
reg IP_valid;			// set when Metis has a valid IP address assigned by DHCP or APIPA
reg Assigned_IP_valid;	// set if IP address assigned by PC is not 0.0.0.0. or 255.255.255.255
reg use_IPIPA;			// set when no DHCP or assigned IP available so use APIAP

always @ (posedge Tx_clock_2)
begin
	case (start_up)
	// get the MAC address for this board
0:	begin 
		IP_valid <= 1'b0;						// clear IP valid flag
		Assigned_IP_valid <= 1'b0;				// clear IP in flash memory valid
		reset <= 1'b1;
		ATLAS_C2 <= 1'b1;                // prevent Mercury boards from initializing
		Tx_reset <= 1'b1;						// prevent I&Q data Tx until all initialised 
		read_MAC <= 1'b1;
		use_IPIPA <= 0;							// clear IPIPA flag
		start_up <= start_up + 1'b1;
	end
	// wait until we have read the EEPROM then the IP address
1:  begin
		if (MAC_ready) begin 				// MAC_ready goes high when EEPROM read
			read_MAC <= 0;
			read_IP <= 1'b1;				// set read IP flag
			start_up <= start_up + 1'b1;
		end
		else start_up <= 1'b1;
	end
	// read the IP address from EEPROM then set up the PHY
2:	begin
		if (IP_ready) begin
			read_IP <= 0;
    		write_PHY <= 1'b1;				// set write to PHY flag
			start_up <= start_up + 1'b1;
		end
		else start_up <= 2;    
    end			
	// check the IP address read from the flash memory is valid. Set up the PHY MDIO registers
3: begin
	   if (AssignIP != 0 && AssignIP != 32'hFF_FF_FF_FF)
			Assigned_IP_valid <= 1'b1;	
	   if (write_done) begin
			write_PHY <= 0;						// clear write PHY flag so it does not run again
			duplex <= 0;						// clear duplex and speed flags
			speed_100T <= 0;
			speed_1000T <= 0; 
			read_PHY <= 1'b1;					// set read from PHY flag
			start_up <= start_up + 1'b1;
		end 
		else start_up <= 3;						// loop here till write is done
	end 
	
	// loop reading PHY Register 31 bits [3],[5] & [6] to determine if final connection is full duplex at 100T or 1000T.
	// Set speed and duplex bits.
	// If an IP address has been assigned (i.e. != 0) then continue else	
	// once connected delay 1 second before trying DHCP to give network time to stabilise.
4: begin
		if (read_done  && (register_data[5] || register_data[6])) begin
			duplex <= register_data[3];			// get connection status and speed
			speed_100T  <= register_data[5];
			speed_1000T <= register_data[6];
			read_PHY <= 0;						// clear read PHY flag so it does not run again	
			reset <= 0;	
			if (duplex) begin					// loop here is not fully duplex network connection
				// if an IP address has been assigned then skip DHCP etc
				if (Assigned_IP_valid) start_up <= 6;
				// allow rest of code to run now so we can get IP address. If 						
				else if (delay == 12500000) begin	// delay 1 second so that PHY is ready for DHCP transaction
					DHCP_start <= 1'b1;				// start DHCP process
					if (time_out)					// loop until the DHCP module has cleared its time_out flag
						start_up <= 4;
					else begin
						delay <= 0;					// reset delay for DHCP retries
						start_up <= start_up + 1'b1;
					end 
				end 
				else delay <= delay + 1'b1;
			end 
		end 
		else start_up <= 4;						// keep reading Register 1 until we have a valid speed and full duplex		
   end 

	// get an IP address from the DHCP server, move to next state if successful, retry 3 times if NAK or time out.		
5:  begin 
		DHCP_start <= 0;
		if (DHCP_ACK) 							// have DHCP assigned IP address so continue
			start_up <= start_up + 1'b1;
		else if (DHCP_NAK || time_out) begin	// try again 3 more times with 1 second delay between attempts
			if (DHCP_retries == 3) begin		// no more DHCP retries so use IPIPA address and  continue
				use_IPIPA <= 1'b1;
				start_up <= start_up + 1'b1;
			end
			else begin
				DHCP_retries <= DHCP_retries + 1'b1;	// try DHCP again
				start_up <= 4;
			end	
		end		
		else begin
		start_up <= 5;
		end
	end
	
	// Have a valid IP address and a full duplex PHY connection so enable Tx code 
6:  begin
		IP_valid <= 1'b1;					// we now have a valid IP address so can respond to Discovery requests etc
		ATLAS_C2 = 1'b0;					// release clock_hold, to allow Mercury boards to run 
		Tx_reset <= 0;						// release reset so UDP/IP Tx code can run
		start_up <= start_up + 1'b1;						
		read_PHY <= 1'b1;					// set read from PHY flag
		
	end
	// loop checking we still have a Network connection by reading speed from PHY registers - restart if network connection lost
7:	begin
		if (read_done) begin 
			read_PHY <= 0;
			if (register_data[5] || register_data[6])
				start_up <= 6;							// network connection OK
			else start_up <= 0;							// lost network connection so re-start
		end 
	end
	default: start_up <= 0;
    endcase
end 

//  KEEP ADCs ON THE MERCURY BOARDS SHUTDOWN INITIALLY
//reg [28:0] Merc_hold_cnt;
//
//always @ (posedge Tx_clock_2)
//begin
//  if (!Merc_hold_cnt[28])
//		Merc_hold_cnt <= Merc_hold_cnt + 1'b1;
//	ATLAS_C2 <= Merc_hold_cnt[28] ? 1'b0 : 1'b1;
//end


//----------------------------------------------------------------------------------
// read and write to the EEPROM	(NOTE: Max clock frequency is 20MHz)
//----------------------------------------------------------------------------------
wire read_IP;
wire IP_ready;
				
EEPROM EEPROM_inst(.clock(EEPROM_clock), .read_MAC(read_MAC), .read_IP(read_IP), .write_IP(), 
				   .IP_to_write(), .CS(CS), .SCK(SCK), .SI(SI), .SO(SO), .This_MAC(This_MAC),
				   .This_IP(AssignIP), .MAC_ready(MAC_ready), .IP_ready(IP_ready), .IP_write_done());
				   				 
								
//------------------------------------------------------------------------------------
//  If DHCP provides an IP address for Metis use that else use a random APIPA address
//------------------------------------------------------------------------------------

// Use an APIPA address of 169.254.(last two bytes of the MAC address)

wire [31:0] This_IP;
wire [31:0]AssignIP;			// IP address read from EEPROM

assign This_IP =  Assigned_IP_valid ? AssignIP : 
				          use_IPIPA ? {8'd169, 8'd254, This_MAC[15:0]} : YIADDR;

//----------------------------------------------------------------------------------
// Read/Write the  PHY MDIO registers (NOTE: Max clock frequency is 2.5MHz)
//----------------------------------------------------------------------------------
wire write_done; 
reg write_PHY;
reg read_PHY;
wire PHY_clock;
wire read_done;
wire [15:0]register_data; 

MDIO MDIO_inst(.clk(PHY_MDIO_clk), .write_PHY(write_PHY), .write_done(write_done), .read_PHY(read_PHY),
	  .clock(PHY_MDC), .MDIO_inout(PHY_MDIO), .read_done(read_done),
	  .read_reg_address(read_reg_address), .register_data(register_data),.speed(PHY_speed));

//----------------------------------------------------------------------------------
//  Renew the DHCP supplied IP address at half the lease period
//----------------------------------------------------------------------------------

/*
	Request a DHCP IP address at IP_lease/2 seconds if we have a valid DHCP assigned IP address.
	The IP_lease is obtained from the DHCP server and returned during the DHCP ACK.
	This is the number of seconds that the IP lease is valid. 
	
	Divide this value by 2 then multiply by the clock rate to give the delay time.
	
	If an IP_lease time of zero is received then the lease time is set to 24 days.
*/

wire [51:0]lease_time;
assign lease_time = (IP_lease == 0) ?  52'h7735_8C8C_A6C0 : (IP_lease >> 1) * 12500000; // 24 days if no lease time given
// assign lease_time = (IP_lease == 0) ? 52'h7735_8C8C_A6C0  : (52'd4 * 52'd12500000);  // every 4 seconds for testing


reg [24:0]IP_delay;
reg DHCP_renew;
reg [3:0]renew_DHCP_retries;
reg [51:0]renew_counter;
reg [24:0]renew_timer; 
reg [2:0]renew;
reg printf;
reg DHCP_request_renew;
reg second_time;				// set if can't get a DHCP IP address after two tries.
reg DHCP_discover_broadcast;    // last ditch attempt so do a discovery broadcast

always @(posedge Tx_clock_2)
begin 
case (renew)

0:	begin 
	renew_timer <= 0;
		if (DHCP_ACK) begin							 // only run if we have a  valid DHCP supplied IP address
			if (renew_counter == lease_time )begin
				renew_counter <= 0;
				renew <= renew + 1'b1;
			end
			else renew_counter <= renew_counter + 1'b1;
		end 
		else renew <= 0;
	end 
// Renew DHCP IP address
1:	begin
		if (second_time) 
			renew <= 4;
		else begin 
			DHCP_request_renew <= 1'b1;
			renew <= renew + 1'b1;
		end 
	end

// delay so the request is seen then return
2:	renew <= renew + 1'b1;

 
// get an IP address from the DHCP server, move to next state if successful, if not reset lease timer to 1/4 previous value
3: begin
	DHCP_request_renew <= 0;
		if (renew_timer != 2 * 12500000) begin  // delay for 2 seconds before we look for ACK, NAK or time_out
			renew_timer <= renew_timer + 1'b1;
			renew <= 3;
		end 		
		else begin
			if (DHCP_NAK || time_out) begin		// did not renew so set timer to lease_time/4
				second_time <= 1'b1;
				renew_counter = (lease_time - lease_time >> 4);  // i.e. 0.75 * lease_time
				renew <= 0;
			end
			else begin	
				renew_counter <= 0; 			// did renew so reset counter and continue.
				renew <= 0;
			end 
		end
	end 

// have not got an IP address the second time we tryed so use a broadcast and loop here
4:	begin 
	DHCP_discover_broadcast <= 1'b1;		// do a DHCP discovery
	renew <= renew + 1'b1;
	end 
	
// if we get a DHCP_ACK then continue else give up 
5:	begin
	DHCP_discover_broadcast <= 0;
		if (renew_timer != 2 * 12500000) begin  // delay for 2 seconds before we look for ACK, NAK or time_out
			renew_timer <= renew_timer + 1'b1;
			renew <= 5;
		end 
		else if (DHCP_NAK || time_out) 	// did not renew so give up
				renew <= 5;
		else begin 						// did renew so continue
			second_time <= 0;
			renew <= 0;
		end 
	end 

	
default: renew <= 0;
endcase
end 


//----------------------------------------------------------------------------------
//  printf - debug. Send broadcast raw Ethernet frame every 3 seconds
//----------------------------------------------------------------------------------

//reg [25:0]printf_counter;    // OK for upto 5 seconds 
//always @(posedge Tx_clock_2)
//begin
//	if (printf_counter == 3 * 12500000) begin
//		printf_counter <= 0;
//		printf <= 1'b1;
//	end 
//	else begin
//		printf_counter <= printf_counter + 1'b1;
//		printf <= 0;
//	end 
//end 

//----------------------------------------------------------------------------------
//  See if we can get an IP address using DHCP
//----------------------------------------------------------------------------------

wire time_out;
wire DHCP_request;

DHCP DHCP_inst(Tx_clock_2, (DHCP_start || DHCP_discover_broadcast), DHCP_renew, DHCP_discover , DHCP_offer, time_out, DHCP_request, DHCP_ACK);

//----------------------------------------------------------------------------------
//  I2C Master to setup TLV320 DAC on Penelope and select mic gain and input source
//----------------------------------------------------------------------------------

I2C_Master I2C_Master_inst(.clock(I2C_clock), .reset(Tx_reset), .SCL(ATLAS_A20), .SDA(ATLAS_A21), .mic_gain(IF_Mic_boost), .line_in(IF_Line_in), .line_boost(IF_line_boost));


//-----------------------------------------------------
//   Rx_MAC - PHY Receive Interface  
//-----------------------------------------------------

wire [7:0]ping_data[0:59];
wire [15:0]Port;
wire [15:0]Discovery_Port;		// PC port doing a Discovery
wire broadcast;
wire ARP_request;
wire ping_request;
wire Rx_enable;
wire this_MAC;  				// set when packet addressed to this MAC
wire DHCP_offer; 				// set when we get a valid DHCP_offer
wire [31:0]YIADDR;				// DHCP supplied IP address for this board
wire [31:0]DHCP_IP;  			// IP address of DHCP server offering IP address 
wire DHCP_ACK, DHCP_NAK;
wire [31:0]PC_IP;				// IP address of the PC we are connecting to
wire [31:0]Discovery_IP;		// IP address of the PC doing a Discovery
wire [47:0]PC_MAC;				// MAC address of the PC we are connecting to
wire [47:0]Discovery_MAC;		// MAC address of the PC doing a Discovery
wire [31:0]Use_IP;				// Assigned IP address, if zero then use DHCP
wire METIS_discovery;			// pulse high when Metis_discovery received
wire [47:0]ARP_PC_MAC; 			// MAC address of PC requesting ARP
wire [31:0]ARP_PC_IP;			// IP address of PC requesting ARP
wire [47:0]Ping_PC_MAC; 		// MAC address of PC requesting ping
wire [31:0]Ping_PC_IP;			// IP address of PC requesting ping
wire [15:0]Length;				// Lenght of frame - used by ping
wire data_match;				// for debug use 
wire PHY_100T_state;			// used as system clock at 100T
wire [7:0] Rx_fifo_data;		// byte from PHY to send to Rx_fifo
wire rs232_write_strobe;
wire seq_error;					// set when we receive a sequence error
wire run;						// set to send data to PC
wire wide_spectrum;				// set to send wide spectrum data
wire [31:0]IP_lease;			// holds IP lease in seconds from DHCP ACK packet
wire [47:0]DHCP_MAC;			// MAC address of DHCP server 
wire erase;						// set when we receive an erase EPCS16 command
wire erase_ACK;					// set when ASMI interface acks the erase command
wire [31:0]num_blocks;			// number of 256 byte blocks to save in EPCS16
wire EPCS_FIFO_enable;			// EPCS fifo write enable


Rx_MAC Rx_MAC_inst (.PHY_RX_CLOCK(PHY_RX_CLOCK), .PHY_data_clock(PHY_data_clock),.PHY_DV(PHY_DV), .PHY_RX(PHY_RX),
			        .broadcast(broadcast), .ARP_request(ARP_request), .ping_request(ping_request),  
			        .Rx_enable(Rx_enable), .this_MAC(this_MAC), .Rx_fifo_data(Rx_fifo_data), .ping_data(ping_data),
			        .DHCP_offer(DHCP_offer), .RS232_Tx(rs232_write_strobe), .RS232_data(RS232_data),
			        .This_MAC(This_MAC), .YIADDR(YIADDR), .DHCP_ACK(DHCP_ACK), .DHCP_NAK(DHCP_NAK),
			        .METIS_discovery(METIS_discovery), .METIS_discover_sent(METIS_discover_sent), .PC_IP(PC_IP), .PC_MAC(PC_MAC),
			        .This_IP(This_IP), .Length(Length), .PHY_100T_state(PHY_100T_state),
			        .ARP_PC_MAC(ARP_PC_MAC), .ARP_PC_IP(ARP_PC_IP), .Ping_PC_MAC(Ping_PC_MAC), 
			        .Ping_PC_IP(Ping_PC_IP), .Port(Port), .seq_error(seq_error), .data_match(data_match),
			        .run(run), .IP_lease(IP_lease), .DHCP_IP(DHCP_IP), .DHCP_MAC(DHCP_MAC),
			        .erase(erase), .erase_ACK(erase_ACK), .num_blocks(num_blocks), .EPCS_FIFO_enable(EPCS_FIFO_enable),
			        .wide_spectrum(wide_spectrum)
			        );
			        


//-----------------------------------------------------
//   Tx_MAC - PHY Transmit Interface  
//-----------------------------------------------------

wire [10:0] PHY_Tx_rdused;  
wire LED;
wire Tx_fifo_rdreq;
wire ARP_sent;
wire  DHCP_discover;
reg  [7:0] RS232_data;
reg  RS232_Tx;
wire DHCP_request_sent;
wire DHCP_discover_sent;
wire METIS_discover_sent;
wire Tx_CTL;
wire [3:0]TD;


Tx_MAC Tx_MAC_inst (.Tx_clock(Tx_clock), .Tx_clock_2(Tx_clock_2), .IF_rst(IF_rst),
					.Send_ARP(Send_ARP),.ping_reply(ping_reply),.PHY_Tx_data(PHY_Tx_data),
					.PHY_Tx_rdused(PHY_Tx_rdused), .ping_data(ping_data), .LED(LED),
					.Tx_fifo_rdreq(Tx_fifo_rdreq),.Tx_CTL(PHY_TX_EN), .ARP_sent(ARP_sent),
					.ping_sent(ping_sent), .TD(PHY_TX),.DHCP_request(DHCP_request),
					.DHCP_discover_sent(DHCP_discover_sent), .This_MAC(This_MAC),
					.DHCP_discover(DHCP_discover), .DHCP_IP(DHCP_IP), .DHCP_request_sent(DHCP_request_sent),
					.METIS_discovery(METIS_discovery), .PC_IP(PC_IP), .PC_MAC(PC_MAC), .Length(Length),
			        .Port(Port), .This_IP(This_IP), .METIS_discover_sent(METIS_discover_sent),
			        .ARP_PC_MAC(ARP_PC_MAC), .ARP_PC_IP(ARP_PC_IP), .Ping_PC_IP(Ping_PC_IP),
			        .Ping_PC_MAC(Ping_PC_MAC), .speed_100T(1'b1), .Tx_reset(Tx_reset),
			        .run(run), .IP_valid(IP_valid), .printf(printf), .IP_lease(IP_lease),
			        .DHCP_MAC(DHCP_MAC), .DHCP_request_renew(DHCP_request_renew),
			        .erase_done(erase_done), .erase_done_ACK(erase_done_ACK), .send_more(send_more),
			        .send_more_ACK(send_more_ACK), .Metis_serialno(Metis_serialno),
			        .sp_fifo_rddata(sp_fifo_rddata), .sp_fifo_rdreq(sp_fifo_rdreq), 
			        .wide_spectrum(wide_spectrum), 
					  .have_sp_data(have_sp_data)
			        ); 
	
	
//------------------------------------------------------------------------
//   Tx_fifo  (1024 words) Dual clock FIFO - Altera Megafunction (dcfifo)
//------------------------------------------------------------------------

/*
        Data from the Tx FIFO Controller  is written to the FIFO using IF_tx_fifo_wreq. 
        FIFO is 1024 WORDS long.
        NB: The output flags are only valid after a read/write clock has taken place
        
        
						---------------------
	IF_tx_fifo_wdata 	|data[15:0]		wrful| IF_tx_fifo_full
						|				     |
	IF_tx_fifo_wreq		|wreq		  wrempty| IF_tx_fifo_empty
						|					 |
		IF_clk			|>wrclk	  wrused[9:0]| IF_tx_fifo_used
						---------------------
    Tx_fifo_rdreq		|rdreq		   q[7:0]| PHY_Tx_data
						|					 |
	   Tx_clock_2		|>rdclk		  rdempty| 
						|		rdusedw[10:0]| PHY_Tx_rdused  (0 to 2047 bytes)
						---------------------
						|                    |
	IF_tx_fifo_clr OR  	|aclr                |
	IF_rst				---------------------
				
        

*/

Tx_fifo Tx_fifo_inst(.wrclk (IF_clk),.rdreq (Tx_fifo_rdreq),.rdclk (Tx_clock_2),.wrreq (IF_tx_fifo_wreq), 
                .data ({IF_tx_fifo_wdata[7:0], IF_tx_fifo_wdata[15:8]}),.q (PHY_Tx_data),.wrusedw(IF_tx_fifo_used), .wrfull(IF_tx_fifo_full),
                .rdempty(),.rdusedw(PHY_Tx_rdused),.wrempty(IF_tx_fifo_empty),.aclr(IF_rst || IF_tx_fifo_clr ));

wire [7:0] PHY_Tx_data;
reg PHY_Tx_rdreq;
reg [3:0]sync_TD;
wire PHY_Tx_rdempty;



//------------------------ sequence ARP and Ping requests -----------------------------------

// send ARP reply, set Send_ARP on the request and clear when done ** may not be requried if ARP_request is not too fast.
reg Send_ARP;
reg ping_sent;
reg ping_reply;

always @ (posedge PHY_RX_CLOCK)
begin
	if (ARP_request)
		Send_ARP <= 1'b1;
	if (ARP_sent)
		Send_ARP <= 0;
	if (ping_request)
		ping_reply <= 1'b1;
	if (ping_sent)
		ping_reply <= 0;	
end


//----------------------------------------------------
//   Receive PHY FIFO 
//----------------------------------------------------

/*
					    PHY_Rx_fifo (16k bytes) 
					
					---------------------
	   Rx_fifo_data |data[7:0]	  wrfull | PHY_wrfull ----> Flash LED!
					|				     |
		Rx_enable	|wrreq				 |
					|					 |									    
	PHY_data_clock	|>wrclk	 			 |
					---------------------								
	IF_PHY_drdy     |rdreq		  q[15:0]| IF_PHY_data [swap Endian] 
					|					 |					  			
			     	|   		  rdempty| IF_PHY_rdempty 
			        |                    | 							
			 IF_clk	|>rdclk rdusedw[12:0]| 		    
					---------------------								
					|                    |
			IF_rst  |aclr                |								
					---------------------								
 
 NOTE: the rdempty stays asserted until enough words have been written to the input port to fill an entire word on the 
 output port. Hence 4 writes must take place for this to happen. 
 Also, rdusedw indicates how many 16 bit samples are available to be read. 
 
*/

wire PHY_wrfull;
wire IF_PHY_rdempty;
wire IF_PHY_drdy;


PHY_Rx_fifo PHY_Rx_fifo_inst(.wrclk (PHY_data_clock),.rdreq (IF_PHY_drdy),.rdclk (IF_clk),.wrreq(Rx_enable),  //IF_PHY_drdy
                .data (Rx_fifo_data),.q ({IF_PHY_data[7:0],IF_PHY_data[15:8]}), .wrusedw(), .rdempty(IF_PHY_rdempty), .rdusedw(),
                .wrfull(PHY_wrfull),.aclr(IF_rst | PHY_wrfull), .rdfull());

//--------------------------------------------------------------------------
//			EPCS16 Erase and Program code 
//--------------------------------------------------------------------------

/*
					    EPCS_fifo (1k bytes) 
					
					---------------------
	  Rx_fifo_data  |data[7:0]	         | 
					|				     |
   EPCS_FIFO_enable	|wrreq		         | 
					|					 |									    
	PHY_data_clock	|>wrclk	 			 |
					---------------------								
	   EPCS_rdreq   |rdreq		  q[7:0] | EPCS_data
					|					 |					  			
			     	|   		         |  
			        |                    | 							
         Tx_clock 	|>rdclk  rdusedw[9:0]| EPCS_Rx_used	    
					---------------------								
					|                    |
			IF_rst  |aclr                |								
					---------------------						
*/

wire [7:0]EPCS_data;
wire [9:0]EPCS_Rx_used;
wire  EPCS_rdreq;

EPCS_fifo EPCS_fifo_inst(.wrclk (PHY_data_clock),.rdreq (EPCS_rdreq),.rdclk (Tx_clock),.wrreq(EPCS_FIFO_enable), 
                .data (Rx_fifo_data),.q (EPCS_data), .rdusedw(EPCS_Rx_used), .aclr(IF_rst));
                
                
//----------------------------
// 			ASMI Interface
//----------------------------
wire busy;
wire erase_done;
wire send_more;
wire erase_done_ACK;
wire send_more_ACK;

ASMI_interface  ASMI_int_inst(.clock(Tx_clock), .busy(busy), .erase(erase), .erase_ACK(erase_ACK), .IF_PHY_data(EPCS_data),
							 .IF_Rx_used(EPCS_Rx_used), .rdreq(EPCS_rdreq), .erase_done(erase_done), .num_blocks(num_blocks),
							 .erase_done_ACK(erase_done_ACK), .send_more(send_more), .send_more_ACK(send_more_ACK), .NCONFIG(NCONFIG)); 




///////////////////////////////////////////////////////////////////////////////////////////////////////
//
//  Receive MDOUT and CDOUT_P data to put in TX FIFO
//
///////////////////////////////////////////////////////////////////////////////////////////////////////

wire          IF_P_mic_Data_rdy;
wire   [47:0] IF_M_IQ_Data [0:NR-1];
wire [NR-1:0] IF_M_IQ_Data_rdy;
reg     [2:0] IF_clock_s;
wire   [63:0] IF_tx_IQ_mic_data;
reg           IF_tx_IQ_mic_rdy;
wire          IF_tx_IQ_mic_ack;
wire   [47:0] IF_IQ_Data;
wire   [15:0] IF_mic_Data;
wire    [2:0] IF_chan; 			// which Mercury receiver channel Tx_fifo_ctrl.v is interested in
reg     [2:0] IF_last_chan; 	// number of Mercury channels to send back to PC

always @*
begin
  if (IF_rst)
    IF_tx_IQ_mic_rdy = 1'b0;
  else if (IF_conf[1])
    IF_tx_IQ_mic_rdy = IF_M_IQ_Data_rdy[0]; // always uses channel 0 ready  ************   
  else 
    IF_tx_IQ_mic_rdy = IF_P_mic_Data_rdy;
end

assign IF_IQ_Data = IF_M_IQ_Data[IF_chan];
                    
// if time_stamp is set then send 1PPS from Atlas A13 to LSB of mic data and zero all other bits.                    
assign IF_tx_IQ_mic_data = IF_time_stamp ? {IF_IQ_Data,{15'b0,PPS}}: {IF_IQ_Data, IF_mic_Data};  


NWire_rcv #(.DATA_BITS(16), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(20000))
    P_MIC (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
           .xrcv_rdy(IF_P_mic_Data_rdy), .xrcv_ack(IF_tx_IQ_mic_ack),
           .xrcv_data(IF_mic_Data), .din(CDOUT_P) );

generate
  genvar c;
  for (c = 0; c < NR; c = c + 1) // max of 4 receivers
  begin: MDC // NR Mercury Data Channels/Receivers

    NWire_rcv #(.DATA_BITS(48), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(20000))
          M_IQ (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
                .xrcv_rdy(IF_M_IQ_Data_rdy[c]), .xrcv_ack(IF_tx_IQ_mic_ack),
                .xrcv_data(IF_M_IQ_Data[c]), .din(MDOUT[c]) );
  end
endgenerate

///////////////////////////////////////////////////////////////
//
//     Tx_fifo Control - creates IF_tx_fifo_wdata and IF_tx_fifo_wreq signals
//
//////////////////////////////////////////////////////////////
localparam RFSZ = clogb2(RX_FIFO_SZ-1);  // number of bits needed to hold 0 - (RX_FIFO_SZ-1)
localparam TFSZ = clogb2(TX_FIFO_SZ-1);  // number of bits needed to hold 0 - (TX_FIFO_SZ-1)
localparam SFSZ = clogb2(SP_FIFO_SZ-1);  // number of bits needed to hold 0 - (SP_FIFO_SZ-1)

wire     [15:0] IF_tx_fifo_wdata;   // data to Tx FIFO
wire            IF_tx_fifo_wreq;    // set when we want to send data to the Tx FIFO
wire            IF_tx_fifo_full;
wire [9:0] IF_tx_fifo_used;
wire     [15:0] IF_tx_fifo_rdata;
wire            IF_tx_fifo_empty;

wire [RFSZ-1:0] IF_Rx_fifo_used;    // read side count
wire            IF_Rx_fifo_full;

wire            clean_dash;      // debounced dash
wire            clean_dot;       // debounced dot
wire            clean_PTT_in;    // debounced button
reg       [7:0] Penny_serialno;
reg       [7:0] Merc_serialno;   // up to 4 Mercury boards
reg       [7:0] Merc2_version;
reg       [7:0] Merc3_version;
reg       [7:0] Merc4_version;

reg      [11:0] Penny_ALC;

reg             ADC_OVERLOAD;    // up to 4 Mercury boards
reg             ADC_OVERLOAD2;
reg             ADC_OVERLOAD3;
reg             ADC_OVERLOAD4;

wire   [RFSZ:0] RX_USED;
wire            IF_tx_fifo_clr;

assign RX_USED = {IF_Rx_fifo_full,IF_Rx_fifo_used};

reg   [1:0] IF_conf;

Tx_fifo_ctrl #(RX_FIFO_SZ, TX_FIFO_SZ) TXFC 
           (IF_rst, IF_clk, IF_tx_fifo_wdata, IF_tx_fifo_wreq, IF_tx_fifo_full,
            IF_tx_fifo_used, IF_tx_fifo_clr, IF_tx_IQ_mic_rdy, IF_tx_IQ_mic_ack,
            IF_chan, IF_last_chan, IF_tx_IQ_mic_data,
            clean_dash, clean_dot, clean_PTT_in, ADC_OVERLOAD, ADC_OVERLOAD2,
            ADC_OVERLOAD3, ADC_OVERLOAD4, Penny_serialno, Merc_serialno, Merc2_version,
            Merc3_version, Merc4_version, Metis_serialno, Penny_ALC, FWD, REV);


/////////////////////////////////////////////////////////////
//
//   Rx_fifo  (4096 words) single clock FIFO
//
/////////////////////////////////////////////////////////////

wire [15:0] IF_Rx_fifo_rdata;
reg         IF_Rx_fifo_rreq;    // controls reading of fifo

wire [15:0] IF_PHY_data;
reg         IF_Rx_fifo_wreq;

// NOTE: Reset Rx_fifo when {IF_DFS1,IF_DFS0} changes!!!???
FIFO #(RX_FIFO_SZ) RXF (.rst(IF_rst), .clk (IF_clk), .full(IF_Rx_fifo_full), .usedw(IF_Rx_fifo_used), 
          .wrreq (IF_Rx_fifo_wreq), .data (IF_PHY_data), 
          .rdreq (IF_Rx_fifo_rreq), .q (IF_Rx_fifo_rdata) );


//------------------------------------------------
//   SP_fifo  (4096 words) dual clock FIFO
//------------------------------------------------

/*
        The spectrum data FIFO is 16 by 4096 words long on the input.
        Output is in Bytes for easy interface to the PHY code
        NB: The output flags are only valid after a read/write clock has taken place

       
							   SP_fifo
						---------------------
	 sp_fifo_wdata |data[15:0]	   wrfull| sp_fifo_wrfull
						|				         |
	sp_fifo_wrreq	|wrreq	     wrempty| sp_fifo_wrempty
						|				         |
			IF_clk	|>wrclk [11:0]wrusedw| sp_fifo_wrused
						---------------------
	sp_fifo_rdreq	|rdreq		   q[7:0]| sp_fifo_rddata
						|                    | 
						|				         |
		Tx_clock_2	|>rdclk		         | 
						|		               | 
						---------------------
						|                    |
	   IF_rst OR   |aclr                |
		!run   	   |                    |
	    				---------------------
		
*/

reg [15:0]sp_fifo_wdata;
wire  spd_rdy;
wire  sp_fifo_rdreq;
wire [7:0]sp_fifo_rddata;
wire sp_fifo_wrempty;
wire sp_fifo_wrfull;
wire sp_fifo_wrreq;
wire [11:0]sp_fifo_wrused;


//--------------------------------------------------
//  Receive Wideband Spectrum Data from Mercury
//--------------------------------------------------

//	When wide_spectrum is set and sp_fifo_wrempty then a trigger is sent to Mercury (Atlas_C21) which sends 4k words 
//  of consecutive ADC samples.
//  Reset fifo when !run so the data always starts at a known state.

wire spd_ack;
wire have_sp_data;


SP_fifo  SPF (.aclr(IF_rst | !run), .wrclk (IF_clk), .rdclk(Tx_clock_2), 
             .wrreq (sp_fifo_wrreq), .data (sp_fifo_wdata), .rdreq (sp_fifo_rdreq),
             .q(sp_fifo_rddata), .wrfull(sp_fifo_wrfull), .wrempty(sp_fifo_wrempty), .wrusedw(sp_fifo_wrused)); 


NWire_rcv #(.DATA_BITS(16), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(80000))  
       SPD (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
            .xrcv_rdy(spd_rdy), .xrcv_ack(spd_ack), .xrcv_data(sp_fifo_wdata), .din(ATLAS_A12) );

assign ATLAS_C21 =  sp_fifo_wrempty & wide_spectrum;  // get wideband data when SP fifo is empty and wide spectrum is selected


sp_rcv_ctrl SPC (.clk(IF_clk), .reset(IF_rst), .spd_rdy(spd_rdy), .spd_ack(spd_ack), 
				 .sp_fifo_wrempty(sp_fifo_wrempty), .sp_fifo_wrfull(sp_fifo_wrfull), .write(sp_fifo_wrreq), .have_sp_data(have_sp_data));




//////////////////////////////////////////////////////////////
//
//   Sync and  C&C  Detector
//
//////////////////////////////////////////////////////////////

/*

  Read the value of IF_PHY_data whenever IF_PHY_drdy is set.
  Look for sync and if found decode the C&C data.
  Then send subsequent data to Rx FIF0 until end of frame.
	
*/

reg   [2:0] IF_SYNC_state;
reg   [2:0] IF_SYNC_state_next;
reg   [7:0] IF_SYNC_frame_cnt; // 256-4 words = 252 words
reg   [7:0] IF_Rx_ctrl_0;   // control C0 from PC
reg   [7:0] IF_Rx_ctrl_1;   // control C1 from PC
reg   [7:0] IF_Rx_ctrl_2;   // control C2 from PC
reg   [7:0] IF_Rx_ctrl_3;   // control C3 from PC
reg   [7:0] IF_Rx_ctrl_4;   // control C4 from PC
reg         IF_Rx_save;
//reg  have_sync;

localparam SYNC_IDLE   = 1'd0,
           SYNC_START  = 1'd1,
           SYNC_RX_1_2 = 2'd2,
           SYNC_RX_3_4 = 2'd3,
           SYNC_FINISH = 3'd4;

always @ (posedge IF_clk)
begin
  if (IF_rst)
    IF_SYNC_state <= #IF_TPD SYNC_IDLE;
  else
    IF_SYNC_state <= #IF_TPD IF_SYNC_state_next;

  if (IF_rst)
    IF_Rx_save <= #IF_TPD 1'b0;
  else
    IF_Rx_save <= #IF_TPD IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_3_4);

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_START) && (IF_PHY_data[15:8] == 8'h7F))
    IF_Rx_ctrl_0  <= #IF_TPD IF_PHY_data[7:0];

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_1_2))
  begin
    IF_Rx_ctrl_1  <= #IF_TPD IF_PHY_data[15:8];
    IF_Rx_ctrl_2  <= #IF_TPD IF_PHY_data[7:0];
  end

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_3_4))
  begin
    IF_Rx_ctrl_3  <= #IF_TPD IF_PHY_data[15:8];
    IF_Rx_ctrl_4  <= #IF_TPD IF_PHY_data[7:0];
  end

  if (IF_SYNC_state == SYNC_START)
    IF_SYNC_frame_cnt <= 0;					    			// reset sync counter
  else if (IF_PHY_drdy && (IF_SYNC_state == SYNC_FINISH))
    IF_SYNC_frame_cnt <= IF_SYNC_frame_cnt + 1'b1;		    // increment if we have data to store
end

always @*
begin
  case (IF_SYNC_state)
    // state SYNC_IDLE  - loop until we find start of sync sequence
    SYNC_IDLE:
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Sync bytes not saved in Rx_fifo

      if (IF_rst || !IF_PHY_drdy) 
        IF_SYNC_state_next = SYNC_IDLE;    // wait till we get data from PC
      else if (IF_PHY_data == 16'h7F7F)
        IF_SYNC_state_next = SYNC_START;   // possible start of sync
      else
        IF_SYNC_state_next = SYNC_IDLE;
    end	

    // check for 0x7F  sync character & get Rx control_0 
    SYNC_START:
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Sync bytes not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_START;   // wait till we get data from PC
      else if (IF_PHY_data[15:8] == 8'h7F)
        IF_SYNC_state_next = SYNC_RX_1_2;  // have sync so continue
      else
        IF_SYNC_state_next = SYNC_IDLE;    // start searching for sync sequence again
    end

    
    SYNC_RX_1_2:                        	// save Rx control 1 & 2
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Rx control 1 & 2 not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_RX_1_2;  // wait till we get data from PC
      else
        IF_SYNC_state_next = SYNC_RX_3_4;
    end

    SYNC_RX_3_4:                        	// save Rx control 3 & 4
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Rx control 3 & 4 not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_RX_3_4;  // wait till we get data from PC
      else
        IF_SYNC_state_next = SYNC_FINISH;
    end

    // Remainder of data goes to Rx_fifo, re-start looking
    // for a new SYNC at end of this frame. 
    // Note: due to the use of IF_PHY_drdy data will only be written to the 
    // Rx fifo if there is room. Also the frame_count will only be incremented if IF_PHY_drdy is true.
    SYNC_FINISH:
    begin    
	  IF_Rx_fifo_wreq  = IF_PHY_drdy;
	  if (IF_SYNC_frame_cnt == ((512-8)/2)) begin  // frame ended, go get sync again
		IF_SYNC_state_next = SYNC_IDLE;
	  end 
	  else IF_SYNC_state_next = SYNC_FINISH;
    end

    default:
    begin
      IF_Rx_fifo_wreq  = 1'b0;
      IF_SYNC_state_next = SYNC_IDLE;
    end
	endcase
end

wire have_room;
assign have_room = (IF_Rx_fifo_used < RX_FIFO_SZ - ((512-8)/2)) ? 1'b1 : 1'b0;  // the /2 is because we send 16 bit values

// prevent read from PHY fifo if empty and writing to Rx fifo if not enough room 
assign  IF_PHY_drdy = have_room & ~IF_PHY_rdempty;


//////////////////////////////////////////////////////////////
//
//              Decode Command & Control data
//
//////////////////////////////////////////////////////////////

/*
	Decode IF_Rx_ctrl_0....IF_Rx_ctrl_4.

	Decode frequency (for Mercury and Penelope), PTT and Speed 

	The current frequency is set by the PC by decoding 
	IF_Rx_ctrl_1... IF_Rx_ctrl_4 when IF_Rx_ctrl_0[7:1] = 7'b0000_001
		
      The sampling rate, either 192k, 96k or 48k is set by
      the PC by decoding IF_Rx_ctrl_1 when IF_Rx_ctrl_0[7:1] are all zero. IF_Rx_ctrl_1
      decodes as follows:

      IF_Rx_ctrl_1 = 8'bxxxx_xx00  - 48kHz
      IF_Rx_ctrl_1 = 8'bxxxx_xx01  - 96kHz
      IF_Rx_ctrl_1 = 8'bxxxx_xx10  - 192kHz

	Decode PTT from PowerSDR. Held in IF_Rx_ctrl_0[0] as follows
	
	0 = PTT inactive
	1 = PTT active
	
	Decode clock sources, when IF_Rx_ctrl_0[7:1] = 0,  IF_Rx_ctrl_1[4:2] indicates the following
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 12.288MHz source from Penelope 
	1xx  = 12.288MHz source from Mercury 
	
	Decode configuration, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_1[6:5] indicates the following
	
	00 = No Tx Rx boards
	01 = Penelope fitted
	10 = Mercury fitted
	11 = Both Penelope and Mercury fitted
	
	Decode Attenuator settings on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[1:0] indicates the following 
	
	00 = 0dB
	01 = 10dB
	10 = 20dB
	11 = 30dB
	
	Decode ADC settings on Mercury, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[4:2] indicates the following
	
	000 = Random, Dither, gain off
	1xx = Random ON
	x1x = Dither ON
	xx1 = Gain ON 
	
	Decode Rx relay settigs on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[6:5] indicates the following
	
	00 = None
	01 = Rx 1
	10 = Rx 2
	11 = Transverter
	
	Decode number of receivers to send back, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl4[5:3] contains the number of
	receiver IQ streams to interleave and send to the PC (000 = 1, 001 = 2, 010 = 3, 011 = 4)

	Decode Tx relay settigs on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_4[1:0] indicates the following
	
	00 = Tx 1
	01 = Tx 2
	10 = Tx 3
	
	Decode Rx_1_out relay settings on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[7] indicates the following

	1 = Rx_1_out on 
	
	Decode 1PPS time stamp from GPS on Atlas A13, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_4[6] = 1 then selected

	When IF_Rx_ctrl_0[7:1] == 7'b0001_001 decodes as follows:- 
	
	IF_Mic_boost	  <= IF_Rx_ctrl_2[0];   // decode mic boost 0 = 0dB, 1 = 20dB
	IF_Drive_Level	  <= IF_Rx_ctrl_1;	    // decode drive level for PennyLane and Hermes 
	IF_Line_in		  <= IF_Rx_ctrl_2[1];   // decode input, 0 = mic, 1 = line 
	IF_Alex 		  <= {IF_Rx_ctrl_2[6], IF_Rx_ctrl_4[6:0], IF_Rx_ctrl_3[6:0]}; // Alex manual relay selections 
	
	where IF_Alex data is 15 bits as follows:
	
	Manual, 17/15m LPF, 12/10m LPF, 6m LPF, 160m LPF, 80m LPF, 60/40m LPF, 30/20m LPF, 6m preamp, bypass HPFs, 1.5MHz HPF,
	6.5MHz HPF, 9.5MHz HPF, 20MHz HPF, 13MHz HPF
	
	When IF_Rx_ctrl_0[7:1] == 7'b0001_010 decodes as follows:
	
	IF_line_boost		<= IF_Rx_ctrl2[4:0]	// decode 5-bit line gain setting
	
*/

wire        PTT_out;
reg   [6:0] IF_OC;       		// open collectors on Penelope
reg   [3:0] IF_OD;       		// user outputs on Metis DB-9 pins 1-4
reg         IF_mode;     		// normal or Class E PA operation 
reg         IF_RAND;     		// when set randomizer in ADC on Mercury on
reg         IF_DITHER;   		// when set dither in ADC on Mercury on
reg         IF_PREAMP;      	// when zero 20 dB attenuator on Mercury #1 is inserted
reg   [1:0] IF_ATTEN;    		// decode attenuator setting on Alex
reg   [1:0] IF_TX_relay; 		// Tx relay setting on Alex
reg         IF_Rout;     		// Rx1 out on Alex
reg   [1:0] IF_RX_relay; 		// Rx relay setting on Alex 
reg  [31:0] IF_frequency [0:4]; // Penny, Merc1, Merc2, Merc3, Merc4
reg         IF_duplex;
reg 		IF_Mic_boost;			// Sets mic gain on Penny TLV320, 0 = 0dB, 1 = 20dB boost.
reg   [7:0] IF_Drive_Level; 	// Tx drive level for PennyLane and Hermes
reg         IF_Line_in;			// Selects input, mic = 0, line = 1
reg  [14:0] IF_Alex[0:3];		// Alex auto/manual mode select and HPF/LPF relay switching when in manual mode
reg			Alex_manual;		// Alex auto/manual selection (0=auto, 1=manual)
reg         IF_time_stamp;		// if set 1PPS input is connected to LSB of mic data, rest are zero
reg         common_Merc_freq;	// 0 = permit independent freqs to Mercury boards, 1 = common freq for all Mercury boards
reg         Decode_IF_f;		// set if RX_CNTRL_0 is specifying frequencies for multiple receivers
reg   [3:0] RxN_preamps;		// Mercury boards preamp states (0000=all preamps OFF, 0001=Merc1 preamp ON, 0010=Merc2 preamp ON, etc)
										// Note: presently there is a redundancy for Mercury #1 "pre-amp" selection because IF_PREAMP above also 
										// controls the Mercury #1 pre-amp state; this is to maintain compatability with earlier PC programs that select 
										// "preamp" via C&C byte C3[2] when C0[7:1] is zero
reg   [4:0] IF_line_boost;		// line-in gain setting

always @ (posedge IF_clk)
begin 
  if (IF_rst)
  begin // set up default values - 0 for now

    {IF_DFS1, IF_DFS0} <= 2'b00;   // speed 
    IF_clock_s         <= 3'b000; // clock source 
    IF_conf            <= 2'b00;   // configuration
    IF_mode            <= 1'b0;    // mode, normal or Class E PA
    IF_OC              <= 7'b0;    // open collectors on Penelope
	 IF_OD				  <= 4'b0;	  // user outputs on Metis DB-9 connector
    IF_ATTEN           <= 2'b0;    // Alex attenuator setting 
    IF_PREAMP          <= 1'b0;    // "pre-amp" selection
    IF_DITHER          <= 1'b0;    // dither on or off
    IF_RAND            <= 1'b0;    // randomizer on or off
    IF_RX_relay        <= 2'b0;    // Alex Rx relays
    IF_Rout            <= 1'b0;    // Alex Rx_1_out relay
    // RX_CONTROL_4
    IF_TX_relay        <= 2'b0;    // Alex Tx Relays
    IF_duplex          <= 1'b0;    // not in duplex mode
    IF_last_chan       <= 3'b000;  // default single receiver
    IF_Mic_boost       <= 1'b0;    // mic boost off    
    IF_Drive_Level     <= 8'b0;	  // drive at minimum 
    IF_Line_in	        <= 1'b0;	  // mic input selected
    IF_Alex[0]			  <= 15'b0;   // Alex 1 manual relay selection
	 IF_time_stamp		  <= 1'b0;	  // 1PPS time stamp
	 common_Merc_freq   <= 1'b0;    // default, permit independent Mercury board freqs
	 RxN_preamps        <= 4'b0;    // default, all Mercury preamps OFF
	 IF_Alex[1]			  <= 15'b0;	  // Alex 2 manual HPF/LPF relay selection
	 Alex_manual		  <= 1'b0;	  // Alex auto/manual selection (0=auto, 1=manual)
	 end
  else if (IF_Rx_save) // all Rx_control bytes are ready to be saved
  begin 								// Need to ensure that C&C data is stable 
    if (IF_Rx_ctrl_0[7:1] == 7'b0000_000)
    begin
      // RX_CONTROL_1
      {IF_DFS1, IF_DFS0}  <= IF_Rx_ctrl_1[1:0]; // decode speed 
      IF_clock_s          <= IF_Rx_ctrl_1[4:2]; // decode clock source 
      IF_conf             <= IF_Rx_ctrl_1[6:5]; // decode configuration
      // RX_CONTROL_2
      IF_mode             <= IF_Rx_ctrl_2[0];   // decode mode, normal or Class E PA
      IF_OC               <= IF_Rx_ctrl_2[7:1]; // decode open collectors on Penelope
      // RX_CONTROL_3
      IF_ATTEN            <= IF_Rx_ctrl_3[1:0]; // decode Alex attenuator setting 
      IF_PREAMP           <= IF_Rx_ctrl_3[2];   // decode "pre-amp" selection
      IF_DITHER           <= IF_Rx_ctrl_3[3];   // decode dither on or off
      IF_RAND             <= IF_Rx_ctrl_3[4];   // decode randomizer on or off
      IF_RX_relay         <= IF_Rx_ctrl_3[6:5]; // decode Alex Rx relays
      IF_Rout             <= IF_Rx_ctrl_3[7];   // decode Alex Rx_1_out relay
      // RX_CONTROL_4
      IF_TX_relay         <= IF_Rx_ctrl_4[1:0]; // decode Alex Tx Relays
      IF_duplex           <= IF_Rx_ctrl_4[2];   // save duplex mode
      IF_last_chan	     <= IF_Rx_ctrl_4[5:3]; // number of IQ streams to send to PC
		IF_time_stamp       <= IF_Rx_ctrl_4[6];   // 1PPS time stamp
		common_Merc_freq    <= IF_Rx_ctrl_4[7];   // force common freq to all Mercury boards

    end
    if (IF_Rx_ctrl_0[7:1] == 7'b0001_001)
    begin
	  IF_Mic_boost		  <= IF_Rx_ctrl_2[0];   // decode mic boost 0 = 0dB, 1 = 20dB
	  IF_Drive_Level	  <= IF_Rx_ctrl_1;	    // decode drive level for PennyLane and Hermes 
	  IF_Line_in		  <= IF_Rx_ctrl_2[1];   // decode input, 0 = mic, 1 = line 
	  IF_Alex[0] 		  <= {IF_Rx_ctrl_2[6], IF_Rx_ctrl_4[6:0], IF_Rx_ctrl_3[6:0]}; // Alex 1 manual relay selections
	  Alex_manual		  <= IF_Rx_ctrl_2[6];   // decode auto/manual selection for Alex (0=auto, 1=manual)
	  end
    if (IF_Rx_ctrl_0[7:1] == 7'b0001_010)
    begin
		RxN_preamps      <= IF_Rx_ctrl_1[3:0];  //decode preamp states for Mercury 1, 2, 3, 4
		//ATLAS_C2         <= IF_Rx_ctrl_1[4];    //if set apply one increment of phase shift in Merc2
		IF_line_boost	  <= IF_Rx_ctrl_2[4:0];  //decode line-in gain setting
		IF_OD				  <= IF_Rx_ctrl_3[3:0];  //decode Metis user output settings for DB-9 connector pins 1-4
	 end
	 if (IF_Rx_ctrl_0[7:1] == 7'b00001_011)
		IF_Alex[1]			<= {Alex_manual,IF_Rx_ctrl_3[6:0], IF_Rx_ctrl_2[6:0]}; // decode HPF/LPF settings for manual Alex 2 control
 
  end
end	

always @ (posedge IF_clk)
begin 
  if (IF_rst)
  begin // set up default values - 0 for now
    IF_frequency[0]    <= 32'd0;
    IF_frequency[1]    <= 32'd0;
    IF_frequency[2]    <= 32'd0;
    IF_frequency[3]    <= 32'd0;
    IF_frequency[4]    <= 32'd0;
  end
  else if (IF_Rx_save)
  begin   // decode IF_frequency[c] frequencies
	if (IF_Rx_ctrl_0[7:1] == 7'b0000_001) // specifies that C1-C4 now contains Tx and Rx1 frequency if not duplex; or Tx frequency if duplex
		IF_frequency[0]   <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4}; // decodes IF_frequency[0] frequency
   if (!IF_duplex && ((IF_last_chan == 3'b000) | common_Merc_freq))
		begin
		IF_frequency[1] <= IF_frequency[0]; 
		IF_frequency[2] <= IF_frequency[0]; 
		IF_frequency[3] <= IF_frequency[0]; 
		IF_frequency[4] <= IF_frequency[0]; 
		end 
  
  Decode_IF_f = (IF_Rx_ctrl_0[7:1] >= 7'd2) && (IF_Rx_ctrl_0[7:1] <= 7'd9) ? 1'b1 : 1'b0;
  
  if (Decode_IF_f & (!common_Merc_freq | IF_duplex)) // decodes IF_frequency[c] frequencies except IF_frequency[0]
		IF_frequency[IF_Rx_ctrl_0[4:1] - 1'b1] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4}; //Rx1 Rx2 Rx3 Rx4 etc frequencies		
  end
end

assign PTT_out = IF_Rx_ctrl_0[0]; // IF_Rx_ctrl_0 only updated when we get correct sync sequence


//////////////////////////////////////////////////////////////
//
//   State Machine to manage PWM interface
//
//////////////////////////////////////////////////////////////
/*

    The code loops until there are at least 4 words in the Rx_FIFO.

    The first word is the Left audio followed by the Right audio
    which is followed by I data and finally the Q data.
    	
    The words sent to the D/A converters must be sent at the sample rate
    of the A/D converters (48kHz) so is synced on the negative edge of the CLRCLK.
*/

reg   [2:0] IF_PWM_state;      // state for PWM
reg   [2:0] IF_PWM_state_next; // next state for PWM
reg  [15:0] IF_Left_Data;      // Left 16 bit PWM data for D/A converter
reg  [15:0] IF_Right_Data;     // Right 16 bit PWM data for D/A converter
reg  [15:0] IF_I_PWM;          // I 16 bit PWM data for D/A conveter
reg  [15:0] IF_Q_PWM;          // Q 16 bit PWM data for D/A conveter
reg         IF_get_samples;
wire        IF_get_rx_data;
wire        IF_bleed;
reg  [12:0] IF_bleed_cnt;
reg         IF_xmit_req;
reg         IF_xack, IF_xrdy;

// Bleed the RX FIFO if no data is being sent to Mercury or Penelope so that
// new RX Control data keeps coming in. Otherwise everything will come to a halt.  Have
// to keep getting RX Control data so we have new C & C data - specifically clock_s[].
// Bleed time is set to occur if the dead time is greater than one 24Khz period since
// CLRCLK is normally 48KHz.  Dead time can be different than one 24Khz period so long
// as it longer than one 48Khz period.  This problem occurs when switching 122.88Mhz clock
// sources
assign IF_get_rx_data = IF_get_samples || IF_bleed;

assign IF_bleed  = (IF_bleed_cnt == (48000000/12000));

localparam PWM_IDLE     = 0,
           PWM_START    = 1,
           PWM_LEFT     = 2,
           PWM_RIGHT    = 3,
           PWM_I_AUDIO  = 4,
           PWM_Q_AUDIO  = 5,
           PWM_WAIT     = 6,
           PWM_REQ      = 7;

always @ (posedge IF_clk) 
begin
  if (IF_rst)
    IF_PWM_state   <= #IF_TPD PWM_IDLE;
  else
    IF_PWM_state   <= #IF_TPD IF_PWM_state_next;

  if (IF_rst)
    IF_bleed_cnt   <= #IF_TPD 1'b0;
  else if (IF_get_rx_data)
    IF_bleed_cnt   <= #IF_TPD 1'b0; // keep clearing IF_bleed count any time we get data from RX FIFO
  else
    IF_bleed_cnt   <= #IF_TPD IF_bleed_cnt + 1'b1;

  // get Left audio
  if (IF_PWM_state == PWM_LEFT)
    IF_Left_Data   <= #IF_TPD IF_Rx_fifo_rdata;

  // get Right audio
  if (IF_PWM_state == PWM_RIGHT)
    IF_Right_Data  <= #IF_TPD IF_Rx_fifo_rdata;

  // get I audio
  if (IF_PWM_state == PWM_I_AUDIO)
    IF_I_PWM       <= #IF_TPD IF_Rx_fifo_rdata;

  // get Q audio
  if (IF_PWM_state == PWM_Q_AUDIO)
    IF_Q_PWM       <= #IF_TPD IF_Rx_fifo_rdata;

  if (IF_rst)
    IF_xmit_req    <= #IF_TPD 1'b0;
  else
    IF_xmit_req    <= #IF_TPD (IF_PWM_state == PWM_REQ) ? 1'b1 : 1'b0; // all data ready to be sent now

end

always @*
begin
  case (IF_PWM_state)
    PWM_IDLE:
    begin
      IF_Rx_fifo_rreq = 1'b0;

      if (!IF_get_rx_data)
        IF_PWM_state_next = PWM_IDLE;    // wait until time to get the donuts every 48/96/192Khz from oven (RX_FIFO)
      else if (RX_USED[RFSZ:2] == 1'b0)  // RX_USED < 4
        IF_PWM_state_next = PWM_WAIT;    // no new donuts so go empty handed = error
      else
        IF_PWM_state_next = PWM_START;   // ah! now it's time to get the donuts
    end

    // Start packaging the donuts
    PWM_START:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_LEFT;
    end

    // get Left audio
    PWM_LEFT:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_RIGHT;
    end

    // get Right audio
    PWM_RIGHT:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_I_AUDIO;
    end

    // get I audio
    PWM_I_AUDIO:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_Q_AUDIO;
    end

    // get Q audio
    PWM_Q_AUDIO:
    begin
      IF_Rx_fifo_rreq    = 1'b0;
      IF_PWM_state_next  = PWM_WAIT;
    end

    PWM_WAIT: // got donuts from oven and pack them.
    begin     // Now wait for shipping truck (NWire_xmit) ready to load them
      IF_Rx_fifo_rreq      = 1'b0;
      if (!IF_xrdy)
        IF_PWM_state_next  = PWM_WAIT;
      else
        IF_PWM_state_next  = PWM_REQ;
    end

    PWM_REQ: // load donuts and wait for shipping truck to leave
    begin
      IF_Rx_fifo_rreq      = 1'b0;
      if (!IF_xack)
        IF_PWM_state_next  = PWM_REQ;
      else
        IF_PWM_state_next  = PWM_IDLE; // truck has left the shipping dock
    end

    default:
    begin
      IF_Rx_fifo_rreq    = 1'b0;
      IF_PWM_state_next  = PWM_IDLE;
    end
  endcase
end

///////////////////////////////////////////////////////////////////////////////
//
// Left/Right Audio data transfers to Mercury(C24)
// I/Q Audio data transfer to Penelope(C19)
//
///////////////////////////////////////////////////////////////////////////////
wire       IF_m_rdy, IF_m_ack, IF_p_rdy, IF_p_ack;

wire IF_C23, IF_C22;
wire IF_m_pulse, IF_p_pulse;

cdc_sync cdc_c23 (.siga(ATLAS_C23), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_C23)); // C23 = M_LR_sync
pulsegen cdc_m   (.sig(IF_C23), .rst(IF_rst), .clk(IF_clk), .pulse(IF_m_pulse));

cdc_sync cdc_c22 (.siga(ATLAS_C22), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_C22)); // C22 = P_IQ_sync
pulsegen cdc_p   (.sig(IF_C22), .rst(IF_rst), .clk(IF_clk), .pulse(IF_p_pulse));


// IF_get_samples produces a single pulse telling when its time (48kHz) to get
// new data from RX_FIFO and send it to Mercury or Penelope
always @*
begin
  if (IF_conf[1])
    IF_get_samples = IF_m_pulse;  // Mercury installed so use rising edge of C23 (M_LR_sync)
  else 
    IF_get_samples = IF_p_pulse;  // Penelope installed so use rising edge of C22 (P_IQ_sync)
end

always @*
begin
  if (IF_conf[1])           // Mercury installed
    IF_xrdy = IF_m_rdy;
  else 				        // Penelope installed 
    IF_xrdy = IF_p_rdy;
end

always @*
begin
  if (IF_conf[1])           // Mercury installed
    IF_xack = IF_m_ack;
  else 				        // Penelope installed
    IF_xack = IF_p_ack;

end

// 16 bits, Audio, two channels for TLV320 on Mercury or Penelope
NWire_xmit #(.SEND_FREQ(50000),.DATA_BITS(32), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000))
  M_LRAudio (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xdata({IF_Left_Data,IF_Right_Data}), .xreq(IF_xmit_req), .xrdy(IF_m_rdy),
             .xack(IF_m_ack), .dout(ATLAS_C24));
             
// 16 bits, I & Q channels for Penelope - always at 48kHz
NWire_xmit #(.SEND_FREQ(50000),.DATA_BITS(32), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000))
  P_IQPWM (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
           .xdata({IF_I_PWM,IF_Q_PWM}), .xreq(IF_xmit_req), .xrdy(IF_p_rdy),
           .xack(IF_p_ack), .dout(ATLAS_C19));
           

///////////////////////////////////////////////////////////////
//
//              Implements Command & Control  encoder 
//
///////////////////////////////////////////////////////////////
/*
	The C&C encoder broadcasts data over the Atlas bus C20 for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
	<[90:89]DFS1,DFS0><[88]PTT><[87:84]address><[83:52]frequency><[51:49]clock_source><[48:42]OC>
	<[41]Mode><[40]PREAMP><[39]DITHER><[38]RAND><[37:36]ATTEN><[35:34]TX_relay><[33]Rout><[32:31]RX_relay>
	<[30:16]Alex><[15:8]Drive_Level><[7:5]multiRx_mode><[4:1]RxN_preamps><[0]common_Merc_freq> 
	
	
	Total of 91 bits. Frequency is in Hz and OC is the open collector data on Penelope.
	The clock source decodes as follows:
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 122.88MHz source from Penelope 
	1xx  = 122.88MHz source from Mercury

		
	For future expansion the three bit address enables specific C&C data to be send to individual boards.
	For the present for use with Mercury and Penelope the address is ignored. 

*/

wire [90:0] IF_xmit_data;
reg   [3:0] CC_address;     // C&C address  0 - 7 
reg   [2:0] multiRx_mode;   // 000 = single receiver; 001 = two receivers, 002 = three receivers, 003 = four receivers
reg         IF_DFS0;        // used to set sampling rate 48/96/192kHz
reg         IF_DFS1;        // ditto 
wire        IF_CC_rdy, IF_CC_pulse;

pulsegen CC_p   (.sig(IF_CC_rdy), .rst(IF_rst), .clk(IF_clk), .pulse(IF_CC_pulse));

// change address each data transmission 
always @ (posedge IF_clk)
begin
  if (IF_rst)
    CC_address <= #IF_TPD 1'b0;
  else if (IF_CC_pulse) // occurs at each rising edge of IF_CC_rdy
  begin
    if (CC_address == 4'd4)
      CC_address <= 1'b0; // Penny = 0
    else
      CC_address <= #IF_TPD CC_address + 1'b1; // 1 <= Mercury <= 4
  end
  multiRx_mode = IF_last_chan[2:0];  //get number of receivers being requested by the PC  
end


assign IF_xmit_data = {IF_DFS1,IF_DFS0,PTT_out,CC_address,IF_frequency[CC_address],IF_clock_s,IF_OC,IF_mode,IF_PREAMP,
                       IF_DITHER, IF_RAND, IF_ATTEN, IF_TX_relay, IF_Rout, IF_RX_relay, IF_Alex[CC_address], IF_Drive_Level, 
							  multiRx_mode, RxN_preamps, common_Merc_freq};

NWire_xmit  #(.DATA_BITS(91), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SEND_FREQ(10000)) 
      CCxmit (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
              .xdata(IF_xmit_data), .xreq(1'b1), .xrdy(IF_CC_rdy), .xack(), .dout(CC));


              
///////////////////////////////////////////////////////
//
//                      NWire Penelope data
//
///////////////////////////////////////////////////////

// Gets current software serial # as an 8 bit value
// format plus Penny(Lane) power out and FWD and REV power as 12 bit values

wire [43:0] IF_pd;
wire        IF_pd_rdy;
wire [11:0] FWD;		// FWD power from AIN4 on Penny(Lane)
wire [11:0] REV;		// REV power from AIN3 on Penny(Lane)

always @(posedge IF_clk)
begin
  if (IF_pd_rdy)
    {Penny_serialno, Penny_ALC, FWD, REV} <= #IF_TPD IF_pd;
end

NWire_rcv  #(.DATA_BITS(44), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(500)) 
      p_ser (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xrcv_data(IF_pd), .xrcv_rdy(IF_pd_rdy), .xrcv_ack(IF_pd_rdy), .din(ATLAS_A5));
             
             
///////////////////////////////////////////////////////
//
//                      NWire Mercury data
//
///////////////////////////////////////////////////////

wire  [8:0] IF_md;
wire  [8:0] IF_md2;
wire  [8:0] IF_md3;
wire  [8:0] IF_md4;

wire        IF_md_rdy;
wire        IF_md_rdy2;
wire        IF_md_rdy3;
wire        IF_md_rdy4;

always @(posedge IF_clk)
begin
  if (IF_md_rdy)
    {Merc_serialno, ADC_OVERLOAD} <= #IF_TPD IF_md;
  if (IF_md_rdy2)
    {Merc2_version, ADC_OVERLOAD2} <= #IF_TPD IF_md2;
  if (IF_md_rdy3)
    {Merc3_version, ADC_OVERLOAD3} <= #IF_TPD IF_md3;
  if (IF_md_rdy4)
    {Merc4_version, ADC_OVERLOAD4} <= #IF_TPD IF_md4;    
end

NWire_rcv  #(.DATA_BITS(9), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(500)) 
      m_ser (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xrcv_data(IF_md), .xrcv_rdy(IF_md_rdy), .xrcv_ack(IF_md_rdy), .din(ATLAS_A6));
             
NWire_rcv  #(.DATA_BITS(9), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(500)) 
      m_ver2 (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xrcv_data(IF_md2), .xrcv_rdy(IF_md_rdy2), .xrcv_ack(IF_md_rdy2), .din(ATLAS_A4));
             

NWire_rcv  #(.DATA_BITS(9), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(500)) 
      m_ver3 (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xrcv_data(IF_md3), .xrcv_rdy(IF_md_rdy3), .xrcv_ack(IF_md_rdy3), .din(ATLAS_A3));
             
NWire_rcv  #(.DATA_BITS(9), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(500)) 
      m_ver4 (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xrcv_data(IF_md4), .xrcv_rdy(IF_md_rdy4), .xrcv_ack(IF_md_rdy4), .din(ATLAS_A2));             
             
//-----------------------------------------------------------
//  Debounce PTT from Atlas bus OR pin 6 of DB9 (active low)
//-----------------------------------------------------------

debounce de_PTT(.clean_pb(clean_PTT_in), .pb(PTT_in || ~PTT_n), .clk(IF_clk));


//-----------------------------------------------------------
//  Debounce dot key - active low
//-----------------------------------------------------------


debounce de_dot(.clean_pb(clean_dot), .pb(~dot_n), .clk(IF_clk));

//-----------------------------------------------------------
//  Debounce dash key - active low
//-----------------------------------------------------------

debounce de_dash(.clean_pb(clean_dash), .pb(~dash_n), .clk(IF_clk));


// User outputs 
assign USEROUT0 = IF_OD[0];	// open drain user output
assign USEROUT1 = IF_OD[1];	// open drain user output
assign USEROUT2 = IF_OD[2];	// 3.3V TTL user output
assign USEROUT3 = IF_OD[3];	// 3.3V TTL user output


//-----------------------------------------------------------
//  LED Control  
//-----------------------------------------------------------

/*
	LEDs  - LED[0] is located at the top of the board
	
	LED[0]  	- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	LED[1]		- Lights when the PHY receives Ethernet traffic
	LED[2]  	- Lights when the PHY transmits Ethernet traffic
	LED[3]  	- Lights when an Ethernet broadcast is detected
	LED[4]  	- Lights when traffic to the boards MAC address is detected
	LED[5]  	- Lights when an ARP request is received
	LED[6]  	- Displays state of DHCP negotiations or static IP - on if ACK, slow flash if NAK, fast flash if time out 
				  and long then short flash if static IP
	LED[7]  	- Lights when a ping request is received
	LED[8]  	- Lights when a Metis discovery packet is received
	LED[9]  	- Lights when a Metis discovery packet reply is sent
	LED[10]  	- Lights when 0x7F7F7F received from PC
	
	
	HEART_BEAT  - Flashes once per second
	
	A LED is flashed for the selected period on the positive edge of the signal.
	IF the signal period is greater than the LED period the LED will remain on.
	
	LEDS are connected to the RAM address lines e.g. LED0 = RAM_A0 etc and are active low.

*/

parameter half_second = 10000000; // at 48MHz clock rate

// flash LED1 for ~ 0.2 second whenever the PHY gets data
Led_flash Flash_LED1(.clock(IF_clk), .signal(PHY_DV), .LED(RAM_A1), .period(half_second)); 	

// flash LED2 for ~ 0.2 second whenever the PHY sends data
Led_flash Flash_LED2(.clock(IF_clk), .signal(PHY_TX_EN), .LED(RAM_A2), .period(half_second)); 	

// flash LED3 for ~0.2 seconds whenever we detect a broadcast
Led_flash Flash_LED3(.clock(IF_clk), .signal(broadcast), .LED(RAM_A3), .period(half_second));

// flash LED4 for ~0.2 seconds whenever we detect a packet addressed to this MAC address
Led_flash Flash_LED4(.clock(IF_clk), .signal(this_MAC), .LED(RAM_A4), .period(half_second));

// flash LED5 for ~0.2 seconds whenever we detect an ARP request
Led_flash Flash_LED5(.clock(IF_clk), .signal(ARP_request), .LED(RAM_A5), .period(half_second));

// flash LED7 for ~0.2 seconds whenever we detect a ping request
Led_flash Flash_LED7(.clock(IF_clk), .signal(ping_request), .LED(RAM_A7), .period(half_second));

// flash LED8 for ~0.2 seconds whenever we detect a Metis discovery request
Led_flash Flash_LED8(.clock(IF_clk), .signal(METIS_discovery), .LED(RAM_A10), .period(half_second));

// flash LED9 for ~0.2 seconds whenever we detect a Metis discovery reply
Led_flash Flash_LED9(.clock(IF_clk), .signal(METIS_discover_sent), .LED(RAM_A11), .period(half_second));

// flash LED10 for ~0.2 seconds when we have detected sync 
Led_flash Flash_LED10(.clock(IF_clk), .signal(IF_SYNC_state == SYNC_RX_1_2), .LED(RAM_A12), .period(half_second));

// flash LED11 for ~0.2 seconds when we have detected a received sequence error or ASMI is busy
Led_flash Flash_LED11(.clock(IF_clk), .signal(seq_error || busy), .LED(RAM_A13), .period(half_second));  




//Flash Heart beat LED
reg [26:0]HB_counter;
always @(posedge PHY_CLK125) HB_counter = HB_counter + 1'b1;
assign HEART_BEAT = HB_counter[25];  // Blink

//------------------------------------------------------------
//   Multi-state LED Control   - code in Led_control is for active LOW LEDs
//------------------------------------------------------------

parameter clock_speed = 25000000; // 25MHz clock 

// display state of PHY negotiations  - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
// and swap between fast and slow flash if not full duplex
Led_control #(clock_speed) Control_LED0(.clock(Tx_clock), .on(speed_1000T), .fast_flash(~speed_100T || ~speed_1000T),
										.slow_flash(speed_100T), .vary(!duplex), .LED(RAM_A0));
										
// display state of DHCP negotiations - on if ACK, slow flash if NAK, fast flash if time out and swap between fast and slow 
// if using a static IP address
Led_control # (clock_speed) Control_LED1(.clock(Tx_clock), .on(DHCP_ACK), .slow_flash(DHCP_NAK),
										.fast_flash(time_out), .vary(Assigned_IP_valid), .LED(RAM_A6));	


function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule

