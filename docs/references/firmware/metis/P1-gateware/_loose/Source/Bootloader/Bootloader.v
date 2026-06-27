//
//  HPSDR - High Performance Software Defined Radio
//
//  BootLoader code. 
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


// Bootloader  V1.0 - copyright 2010, 2011 Phil Harman VK6APH 




/* 8 Sept  2010 - Boot Loader Programmer code for  Metis board - Phil Harman VK6APH 

 In Altera terms this code is the 'Factory' code and the Metis code the 'Application'.

 fmax for RemoteUpdate using a Cyclone III is 40MHz

 When using Active Serial the Factory program is located in the EPCS16 starting at address 0x00000000
 If MODE1 is grounded then stay in Factory mode, if high then load Application code at address 0x00100000. 

 Need to select Settings > Device > Device & Pin Options > Configuration > Configuration Mode = Remote.

 The factory code can be tested by itself by just loading Factory.pof rather than output_file.pof which contains both 
 Factory and Application code. 

 If either the Factory or Application code changes then the output_file.pof must be regenerated. The settings for 
 output_file.pof can be saved in a *.cof file using the Convert Programming Files utility. The utility can then be used to read 
 the *.cof file and set up the files and parametes to create the output_file.pof.


 								***** IMPORTANT **** 
 								
 Once the EPCS has been write protected then it can't be programmed using the AS port.
 Use the JTAG port to remove the write protection. 
 If you write protect a sector then you must remove the write protection, using JTAG, before the bulk erase 
 can be used.
 
 Since the EPSC16 is a flash device it is only possible to convert a 1 to a 0. In which case it is necessary
 to erase a memory location before writing to it.
 
 The MSEL jumpers must be set to AS so that the FPGAs ASMI is always enabled.


	EPCS16 
	
	Bytes 				= 2M
	Sectors				= 32
	Bytes per sector 	= 65k
	Pages per sector 	= 256
	Number of pages  	= 8192
	Bytes per page   	= 256
	
	Address Range (Byte Addresses in HEX)
	Sector	Start    End
	31 		H'1F0000 H'1FFFFF
	30 		H'1E0000 H'1EFFFF
	29 		H'1D0000 H'1DFFFF
	28 		H'1C0000 H'1CFFFF
	27 		H'1B0000 H'1BFFFF
	26 		H'1A0000 H'1AFFFF
	25 		H'190000 H'19FFFF
	24 		H'180000 H'18FFFF
	23 		H'170000 H'17FFFF
	22 		H'160000 H'16FFFF
	21 		H'150000 H'15FFFF
	20 		H'140000 H'14FFFF
	19 		H'130000 H'13FFFF
	18 		H'120000 H'12FFFF
	17 		H'110000 H'11FFFF
	16 		H'100000 H'10FFFF
	15 		H'0F0000 H'0FFFFF
	14 		H'0E0000 H'0EFFFF
	13 		H'0D0000 H'0DFFFF
	12 		H'0C0000 H'0CFFFF
	11 		H'0B0000 H'0BFFFF
	10 		H'0A0000 H'0AFFFF
	 9 		H'090000 H'09FFFF
	 8 		H'080000 H'08FFFF
	 7 		H'070000 H'07FFFF
	 6 		H'060000 H'06FFFF
	 5 		H'050000 H'05FFFF
	 4 		H'040000 H'04FFFF
	 3 		H'030000 H'03FFFF
	 2 		H'020000 H'02FFFF
	 1 		H'010000 H'01FFFF
	 0 		H'000000 H'00FFFF		
		
Each Sector holds 256 Pages each of 256 bytes
		

When writing to a memory location and then reading it via the USB Blaster the value is reversed
e.g. if we write 0xD1 (1101 0001) to memory address 0x1FFFFF then read it via the USB Blaster
it returns 0x8B (1000 1011) since it reads LSB first .

Writing a value to memory and reading it back gives the same value.

Altera recommends checking the busy signal before sending a new command. When
the busy signal is deasserted, allow two clock cycles before sending a new signal.
This delay allows the circuit to reset itself before executing the next command.

 From BSD file

Penelope:
ftp://ftp.altera.com/outgoing/download/bsdl/EP2C8Q208.BSD

"CONFIG_IO (0000001101)"; 


attribute IDCODE_REGISTER of EP2C8Q208 : entity is
  "0000"&               --4-bit Version
  "0010000010110010"&   --16-bit Part Number (hex 20B2)
  "00001101110"&        --11-bit Manufacturer's Identity
  "1";                  --Mandatory LSB


For Penny I get 0x7609A080 so looks like data is in reverse order with unwanted leading zero and end bit (1) missing.

Mercury:
ftp://ftp.altera.com/outgoing/download/bsdl/EP3C25Q240.BSD

"ACTIVE_DISENGAGE (1011010000), 
 "& "CONFIG_IO (0000001101)"; 

attribute IDCODE_REGISTER of EP3C25Q240 : entity is
  "0000"&               --4-bit Version
  "0010000011110011"&   --16-bit Part Number (hex 20F3)
  "00001101110"&        --11-bit Manufacturer's Identity
  "1";                  --Mandatory LSB

For Mercury I get 0x7619E080 

Get Device ID
	
	Sequence is as follows:
	
	Reset JTAG chain 		TMS = 1,1,1,1,1
	Goto Shift-DR 			TMS = 0,1,0,0
	Toggle TCK 32 times and read TDO into a shift register after the negative edge of TCK



FPGA program code. 

	Sequence is as follows:
	
	Reset JTAG chain 		TMS = 1,1,1,1,1
	Goto Shift-IR			TMS = 0,1,1,0,0
	remove AS 				TDI = ACTIVE_DISENGAGE* (10 bits, LSB first), set TMS high on 10th bit
	Goto Update-IR 			TMS = 1
	
	Reset JTAG chain 		TMS = 1,1,1,1,1
	Goto Shift-IR			TMS = 0,1,1,0,0
	Select program mode     TDI = 0x002  (10 bits, LSB first), set TMS high on 10th bit
	Goto Update-IR			TMS = 1
	Set TDI high 			TDI = 1
	Goto Shift_DR 			TMS = 1,0,0
	Shift in 3181 1's       TDI = 1
	Clock in file to TDI 	TDI (LSB first)  
	At end of file clock in 128 1's  (16 x 0xFF) on TDI line  (PC adds these to the file) 
	Goto Update-DR			TMS = 1,1
	Goto Run-Test/Idle		TMS = 0
	Reset JTAG chain 		TMS = 1,1,1,1,1				
	Goto Shift-IR			TMS = 0,1,1,0,0
	Select Startup mode     TDI = 0x003  (10 bits, LSB first), set TMS high on 10th bit
	Goto Update-IR			TMS = 1
	Run - Test/Idle 		TMS = 0
	Send 200 clocks
	Reset JTAG chain 		TMS = 1,1,1,1,1
	
	*NOTE: for Cyclone III, use CONFIG_IO for Cyclone II
			    
    For Get Device        <11:22:33:44:55:66 ><From MAC address> 0xEFFE, 0x03, 0x06, <46 bytes of 0x00 > 
    For Program Mercury   <11:22:33:44:55:66 ><From MAC address> 0xEFFE, 0x03, 0x07, <# of bytes><256 bytes of data> 
    For Program Penelope  <11:22:33:44:55:66 ><From MAC address> 0xEFFE, 0x03, 0x08, <# of bytes><256 bytes of data> 
    For Erase flash       <11:22:33:44:55:66 ><From MAC address> 0xEFFE, 0x03, 0x09, <46 bytes of 0x00 > 
    For Program flash     <11:22:33:44:55:66 ><From MAC address> 0xEFFE, 0x03, 0x0A, <# of blocks><256 bytes of data>
	
	Metis will reply to the MAC address of this PC with Raw Ethernet frames as follows:
   
       <To MAC address><11:22:33:44:55:66 > 0xEFFE, 03, <reply> <IDCODES> <46 bytes of 0x00 > 
       
       where reply means:
  
       0x01 = command completed successfully
       0x02 = ready for next 256 bytes of data
       
       and IDCODES is the silicon ID of the FPGA in the JTAG chain
    


	In order to get this code to compile without timing errors under
	Quartus V9.1 sp2  I needed to use the following settings:
	
	- Analysis and Synthesis Settings\Power Up Dont Care [not checked]


Command Format:
	
	The PC sends a Raw Ethernet frame as follows
   
       For Program Flash on Metis     <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x01, < 4 bytes of 0x00 ><256 bytes of data> 
       For Erase  Flash on Metis      <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x02, <46 bytes of 0x00 > 
       For Read MAC of Metis          <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x03, <46 bytes of 0x00 > 
       For Read IP of Metis           <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x04, <46 bytes of 0x00 > 
       For Write IP to Metis flash    <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x05, <IP Address><46 bytes of 0x00 > 
       For Get Device ID on JTAG      <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x06, <46 bytes of 0x00 > 
       For Program Mercury            <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x07, <# of bytes><256 bytes of data> 
       For Program Penelope           <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x08, <# of bytes><256 bytes of data> 
       For Erase flash                <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x09, <46 bytes of 0x00 > 
       For Program flash              <11:22:33:44:55:66 ><From PC MAC address> 0xEFFE, 0x03, 0x0A, <# of blocks><256 bytes of data>
 
  For Boot Loader Mode the MAC address of a Metis board is fixed at 11:22:33:44:55:66 
 
 
 
 NOTE: You must erase the EPCS16 before programming it - the code does this.
 
 Metis will reply to the MAC address of this PC with Raw Ethernet frames as follows:
  
      <To MAC address><11:22:33:44:55:66 > 0xEFFE, 03, <Return code> <46 bytes of 0x00 >  or
      <To MAC address><11:22:33:44:55:66 > 0xEFFE, 03, <Return code> <IDCODES> <42 bytes of 0x00 > or
      <To MAC address><11:22:33:44:55:66 > 0xEFFE, 03, <0x03> <Metis MAC Address> <42 bytes of 0x00 > or
      <To MAC address><11:22:33:44:55:66 > 0xEFFE, 03, <0x04> <Metis IP Address> <42 bytes of 0x00 >

  where Return code means:
      
 Return code       Meaning
  0x00              Invalid command
  0x01              Erase done
  0x02              Send more - ready for next 256 bytes
  0x03              Have MAC address
  0x04              Have IP address
  0x05              FPGA ID
      
  and IDCODES is the silicon ID of the FPGA in the JTAG chain

   Speed is forced to 100T
   
   NOTE: Once in the Program state the code does not exit but relies on the PC to stop sending data.
		 In which case need to cycle power to the board in order to run more commands. 
   
   Change Log:   
               
		            
	********* For this code to run you need the boot jumper set !!!! *******
	    	      
       
       LEDs
       
       LED[0]	- 	PHY_RX
       LED[1]	-	MAC address match 	
       LED[2]	- 	Erase Command	
       LED[3]	- 	Program Command
       LED[4]   -   Connect speed - slow flash for 100T, fast for no network found.	
       LED[5]	- 	PHY_TX
       LED[6]   -   NCONFIG
       LED[7]   -   Remote Config Busy       
       LED[8]   -   PHY  Rx fifo empty
       LED[9]   -   Send more data ACK
       LED[10]  -   Send more data
  
       LED[13]  -   ASMI busy
       
	   HEART_BEAT 
	
		     
	**** do not forget to set the four ASMI pins (FLASH_nCE/nCSO, Data[1]/ASDO, DCLK and Data[0]) to "Reset" in the Dual-purpose pins tab. ****
    **** make sure AS is set so can connect USB Blaster without resetting the FPGA i.e. 'Use Configuration Device - EPCS16'****
    **** make sure Configuration Mode is set to "Remote" ****
	
	see http://www.grigaitis.eu/?p=514 to use NIOS and a serial port for programing EPCS16

*/

module Bootloader (
			  ATLAS_A13,ATLAS_A14,ATLAS_A15,ATLAS_A16,ATLAS_A17,
			  RAM_A0,RAM_A1,RAM_A2,RAM_A3,RAM_A4, RAM_A5, RAM_A6, RAM_A7, RAM_A10, RAM_A11, RAM_A12, RAM_A13, HEART_BEAT,
			  PHY_TX,PHY_RX,PHY_DV,PHY_TX_CLOCK,PHY_TX_EN, NCONFIG, MODE1,
			  PHY_RX_CLOCK,PHY_CLK125,PHY_MDIO,PHY_MDC, PHY_RESET_N, SCK, SI,CONFIG, NODE_ADDR_CS,
			  ATLAS_A23, ATLAS_A24, ATLAS_A25, ATLAS_A27, ATLAS_C5, ATLAS_C6
			  );


// test pins
output  wire     ATLAS_A13;
output  wire     ATLAS_A14;
output  wire     ATLAS_A15; 
output  wire     ATLAS_A16;   
output  wire     ATLAS_A17;

// user IO

assign ATLAS_A13 = PHY_DV;
assign ATLAS_A14 = prog; 
assign ATLAS_A15 = data_match; //rdreq;
assign ATLAS_A16 = PHY_TX_EN; //send_more; 
assign ATLAS_A17 = send_more_ACK;

// RAM and LEDs
output	wire     RAM_A0;		
output	wire     RAM_A1;
output	wire     RAM_A2;
output	wire     RAM_A3;
output	wire     RAM_A4;
output  wire     RAM_A5;
output  wire     RAM_A6;
output  wire     RAM_A7;
output	wire     RAM_A10;
output	wire     RAM_A11;
output	wire     RAM_A12;
output	wire     RAM_A13;

// PHY
output  reg  [3:0]PHY_TX;
input   wire [3:0]PHY_RX;		   
input	wire     PHY_DV;			// PHY has data flag
output	wire     PHY_TX_CLOCK;		// PHY Tx data clock
output	reg 	 PHY_TX_EN;			// PHY Tx enable
input	wire	 PHY_RX_CLOCK;      // PHY Rx data clock
input	wire     PHY_CLK125;		// 125MHz clock from PHY PLL
inout	wire     PHY_MDIO;			// data line to PHY MDIO
output	wire 	 PHY_MDC;			// 2.5MHz clock to PHY MDIO
output	wire     PHY_RESET_N;

// EEPROM
output wire		SCK; 					// clock on MAC EEPROM
output wire		SI;						// serial in on MAC EEPROM
input  wire 	CONFIG; 				// SO on MAC EEPROM
output wire 	NODE_ADDR_CS;			// CS on MAC EEPROM
wire SO;	assign SO = CONFIG;			// serial out on MAC EEPROM
wire CS;	assign NODE_ADDR_CS = CS; 	// Chip select on MAC EEPROM


// Reload
output  reg      NCONFIG = 0;

// image select 
input   wire     MODE1;

// Heart beat LED
output wire 	  HEART_BEAT;		// LED, flashes, runs off PHY 125MHz clock.

// JTAG connections
input  wire ATLAS_A25;
output wire ATLAS_A23;
output wire ATLAS_A24;
output wire ATLAS_A27;

wire TDO; assign TDO = ATLAS_A25;
reg  TDI; assign ATLAS_A27 = TDI;   // **** NB can't read this off Atlas bus - use a pin
reg  TMS; assign ATLAS_A23 = TMS;
reg  TCK; assign ATLAS_A24 = TCK;

// Atlas bus
input   wire 	ATLAS_C5; 			// erase done from or send more from target 
output  wire  	ATLAS_C6; 			// Rx_fifo wrclk


localparam ACTIVE_DISENGAGE  = 10'b1011010000;
localparam CONFIG_IO = 10'b0000001101;


//--------------------------------------------------------
// 					Clocks
//--------------------------------------------------------

wire C125_clk; 	assign C125_clk = PHY_CLK125;	// use PHY 125MHz clock for system clock
wire Tx_clock;
wire Tx_clock_2;
wire C125_locked; 			// high when PLL locked
wire PHY_data_clock;
wire PHY_speed;				// 0 = 100T, 1 = 1000T	
wire PHY_MDIO_clk;  		// divide 25MHz clock by 10 for PHY MDIO clock 	

// use PLL to generate 25MHz, 12.5MHz and 2.5MHz from 125MHz.
// C0 = , C1 = 25MHz, C2 = 12.5MHz, C3 = 2.5MHz

PLL_clocks PLL_inst(.areset(), .inclk0(C125_clk), .c0(), .c1(Tx_clock), .c2(Tx_clock_2), .c3(PHY_MDIO_clk), .locked(C125_locked));

// generate PHY_RX_CLOCK/2 for 100T 
reg PHY_RX_CLOCK_2;
always @ (posedge PHY_RX_CLOCK) PHY_RX_CLOCK_2 <= ~PHY_RX_CLOCK_2; 

assign PHY_TX_CLOCK = ~Tx_clock;

assign PHY_speed = 0;  // Force 100T

// select data clock speed 
assign PHY_data_clock = PHY_RX_CLOCK_2;

// assign clock for Remote Config code
wire clock;
assign clock = Tx_clock;

wire TCK_clock;
assign TCK_clock = PHY_MDIO_clk;   //Divide clock to give 2.5MHz TCK_clock

//------------------------------------------------------------------------------
//  Remote Update
//------------------------------------------------------------------------------

// BootAddress is the address in the EPCS16 to load the Application image from. 
// If control = 0 then load the Factory image, if = 1 load the Application.

localparam CLOCK_FREQ = 25;  // clock speed we are using for Reconfigure module

Reconfigure Recon_inst(.reset(reset), .clock(clock), .clk_freq(CLOCK_FREQ),.BootAddress(24'h100000),
					   .control(MODE1), .CRC_error(), .done(RAM_A7)); 


//------------------------------------------------------------
//  Reset and initialisation
//------------------------------------------------------------

/* 
	Hold the code in reset whilst we do the following:
	
	Setup the PHY registers and read from the PHY until it indicates it has 
	negotiated a speed.  Read connection speed and that we are running full duplex.
	
	LED0 incates PHY status - on = connected at 1000T, slow flash is 100T and fast flash = not connected
	
	Then enable rest of code.
	
*/

reg reset;
reg [4:0]start_up;
wire [47:0]This_MAC; 	// holds the MAC address of this PC
reg read_EEPROM; 
reg DHCP_start;
wire MAC_ready;
wire IP_ready;
reg [24:0]delay;
reg duplex;				// set when we are connected full duplex
reg speed_100T;			// set when we are connected at 100MHz
reg speed_1000T;		// set when we are connected at 1GHz
reg [9:0]reset_count;	// hold reset to ensure Rx fifo is empty

assign PHY_RESET_N = 1'b1;  // Allow PYH to run for now
assign This_MAC = 48'h11_22_33_44_55_66;  // fixed MAC address when in boot mode

always @ (posedge Tx_clock_2)
begin
	case (start_up)

0:	begin
	reset_count <= 0;
		if(C125_locked) begin 			// wait until PLL is locked
			reset <= 1'b1;
			start_up <= start_up + 1'b1;
		end 
	end
	// set up the PHY
1:  begin
		write_PHY <= 1'b1;				// set write to PHY flag
		start_up <= start_up + 1'b1;
	end
		
	// set up the PHY MDIO registers
2: begin
	   if (write_done) begin
			write_PHY <= 0;						// clear write PHY flag so it does not run again
			duplex <= 0;						// clear duplex and speed flags
			speed_100T <= 0;
			speed_1000T <= 0; 
			read_PHY <= 1'b1;					// set read from PHY flag
			start_up <= start_up + 1'b1;
		end 
		else start_up <= 2;						// loop here till write is done
	end 
	
	// loop reading PHY Register 31 bits [3],[5] & [6] to determine if final connection is full duplex at 100T or 1000T.
	// Set speed and duplex bits.	

3: begin
		if (read_done  && (register_data[5] || register_data[6])) begin
			duplex <= register_data[3];			// get connection status and speed
			speed_100T  <= register_data[5];
			speed_1000T <= register_data[6];
			read_PHY <= 0;						// clear read PHY flag so it does not run again
			start_up <= start_up + 1'b1;
			end 
		else start_up <= 3;						// keep reading Register 1 until we have a valid speed		
   end 

	// start up is over so release reset and run 
4:  begin
		if (speed_1000T) start_up <= 1;			// make sure we not at 1000T,if so reset
		else begin
			if (reset_count == 1000)			// make sure that Rx_fifo is empty
				reset <= 0;
			else reset_count <= reset_count + 1'b1;  // release reset so rest of code can run
		start_up <= 4;
		end 
	end
	default: start_up <= 0;
    endcase
end 

//----------------------------------------------------------------------------------
// Read/Write the  PHY MDIO registers (NOTE: Max clock frequency is 2.5MHz)
//----------------------------------------------------------------------------------
wire write_done; 
reg write_PHY;
reg read_PHY;
wire PHY_clock;
wire read_done;
wire [15:0]register_data; 

localparam read_reg_address = 5'd31; 	// PHY register to read from - gives connect speed and duplex status

MDIO MDIO_inst(.clk(PHY_MDIO_clk), .write_PHY(write_PHY), .write_done(write_done), .read_PHY(read_PHY),
	  .clock(PHY_MDC), .MDIO_inout(PHY_MDIO), .read_done(read_done),
	  .read_reg_address(read_reg_address), .register_data(register_data),.speed(PHY_speed));

//---------------------------------------------------
//   PHY Receive Interface  
//---------------------------------------------------

reg [111:0] PHY_output;			// register to hold output from Micrel PHY chip 
wire [7:0] PHY_byte;
reg [4:0] PHY_Rx_state;
reg [9:0] left_shift;
reg [7:0] command;
reg erase;						// set when erase command received
reg READ_ID;					// set when read ID command received
reg READ_ID_ACK;
reg [47:0] PC_MAC;
reg PROGRAM;					// set when program command received   
reg PROGRAM2;					// set when we want to program Mercury 
reg prog;						// set when program command received
reg data_match;
reg Rx_enable;					// set when we need to write to Rx_fifo
reg [8:0]PHY_data_count;		// holds number of bytes we have sent to Rx_fifo
reg send_more; 					// set when the Rx fifo is empty and we need some more data from the PC
reg send_more_ACK;				// set when the request for more has been sent
reg ERASE_CHIP;
reg [31:0]num_blocks; 			// holds number of blocks that the PC will be sending
reg [31:0]block_counter;		// holds number of clocks received so far
reg [31:0]num_bytes;			// holds number of bytes that the PC will be sending
reg [11:0]shift_count;			// holds number of time to shift with TDI set to 1
reg read_MAC;					// set when we want to read the MAC address from the EEPROM
reg send_MAC;					// set when we want to sent the MAC address from the EEPROM to the PC
reg send_MAC_ACK;				// set when the MAC address has been sent to the PC
reg read_IP;					// set when we want to read the IP address from the EEPROM
reg send_IP;					// set when we want to sent the IP address to the PC
reg send_IP_ACK;				// set when the IP address has beed sent to the PC
reg [31:0]IP_to_write;			// holds IP address sent by PC
reg write_IP;					// set to write IP address to EEPROM


localparam	START 			= 0,
			GET_TYPE		= 1,
			SEND_TO_FIFO  	= 2,
			SEND_TO_J_FIFO  = 3,
			SEND_TO_J_FIFO2 = 4,			
			WAIT_ACK        = 5,
			GET_DEVICE		= 6,
			ERASE_FLASH		= 7,
			ERASE 			= 8,
			READMAC			= 9,
			READIP			= 10,
			WRITEIP			= 11;
			


// fifo based nibble to byte converter. PHY data goes to input and wrreq takes PHY_DV.
// Write clock is ~PHY_RX_CLOCK and read clock PHY_RX_CLOCK_2.
// rdempty is linked to rdreq so it always reading.

//-------------------------------------------
//   			PHY fifo
//-------------------------------------------

/*
					    PHY_fifo (32 bytes) 
					
					---------------------
             PHY_RX |data[3:0]	         | 
					|				     |
		  PHY_DV	|wrreq		         | 
					|					 |									    
	~PHY_RX_CLOCK	|>wrclk	 			 |
					---------------------								
	     !rdempty   |rdreq		  q[7:0] | PHY_byte
	        		|					 |					  			
			     	|   		  rdempty| rdempty 
			        |                    | 							
	PHY_data_clock	|>rdclk  			 | 	    
					---------------------								
					|                    |
	reset OR clear  | aclr				 |
					---------------------

 
*/

wire rdempty;

PHY_fifo PHY_fifo_inst (.data(PHY_RX), .q(PHY_byte), .wrreq(PHY_DV), .wrclk(~PHY_RX_CLOCK), .rdreq(!rdempty),
						.rdclk(PHY_data_clock), .rdempty(rdempty), .aclr(reset || clear));

// apply output of fifo to 112 bit shift register
always @ (posedge PHY_data_clock)
	 PHY_output <= {PHY_output[103:0], PHY_byte}; 	   
	 	
	
// process bytes from PHY	
always @ (negedge PHY_data_clock)  
begin								

case (PHY_Rx_state)

START:
	begin
	left_shift <= 0; 			// reset the shift counter
	READ_ID  <= 0; 				// reset GET_DEVICE command
	erase  <= 0; 				// reset erase command
	PHY_data_count <= 0;		// reset data count
	//prog <= 0;					// clear the prog flag				
	PROGRAM <= 0;				// clear the JTAG program flag
	PROGRAM2 <= 0;				// clear the flash program flag

	 // check for frame to Metis MAC address  - ignore first 6 x h55 since Rx PLL may not be locked 
		if (PHY_output[63:0] == {16'h55_D5, This_MAC}) begin  
			PHY_Rx_state <= GET_TYPE;  	// have preamble and this MAC so next state 
		end
		else begin
			data_match <= 1'b0;
			PHY_Rx_state <= START;		// no preamble and MAC so loop and look again
		end  				    
	end
	
// now shift left 14 times to get From_MAC, check HPSDR type and command plus Assign IP / number of bytes/frames  
// we get one left shift automatically when we go to this state
GET_TYPE:
	begin
	case (left_shift)	
	13: begin
			if (PHY_output[63:40] == 24'hEF_FE_03) begin   		// check this is a program request
				PC_MAC   <= PHY_output[111:64];					// MAC address of the sending PC
				data_match <= 1'b1;	
				case(PHY_output[39:32])							// determine what command has been sent
				1: 	begin																
					left_shift <= 0;
					PHY_Rx_state <= SEND_TO_FIFO;
					end 
				2:	begin 
					erase <= 1'b1;
					PHY_Rx_state <= ERASE;
					end
				3:	begin
					read_MAC <= 1'b1;
					PHY_Rx_state <= READMAC;
					end
				4:	begin
					read_IP <= 1'b1;
					PHY_Rx_state <= READIP;
					end
				5:	begin										// Write statis IP address to EEPROM
					IP_to_write <= PHY_output[31:0];
					write_IP <= 1'b1;
					PHY_Rx_state <= WRITEIP;
					end
				6:	begin										// JTAG-get ID of board on JTAG chain
					READ_ID <= 1'b1;
					PHY_Rx_state <= GET_DEVICE;
					end
				7:	begin										// JTAG-program Mercury						
					num_bytes <= PHY_output[31:0];				// save number of bytes the PC will be sending
					DISENGAGE <= ACTIVE_DISENGAGE;				// set for Cyclone III
					shift_count <= 12'd3181;
					left_shift <= 0;
					PHY_Rx_state <= SEND_TO_J_FIFO;
					end 
				8:	begin										// JTAG-program Penelope
					num_bytes <= PHY_output[31:0];				// save number of bytes the PC will be sending
					DISENGAGE <= CONFIG_IO;						// set for Cyclone II
					shift_count <= 12'd300;
					left_shift <= 0;
					PHY_Rx_state <= SEND_TO_J_FIFO;
					end 
				9:  begin 										// JTAG-erase flash memory on target board
					ERASE_CHIP <= 1'b1;
					PHY_Rx_state <= ERASE_FLASH;
					end
				10:	begin										// JTAG-program flash memory on target board
					num_blocks <= PHY_output[31:0];				// save number of blocks PC is sending
					left_shift <= 0;
					PHY_Rx_state <= SEND_TO_J_FIFO2;	
					end 
				default:  PHY_Rx_state <= START;  				// not a valid command so just exit
				endcase 
			end
		else PHY_Rx_state <= START;  					// not a valid command so just exit 
	end
		
	default: left_shift <= left_shift + 1'b1;
	endcase
	end 

	
//  Loop here until we have sent 256 bytes to the Rx FIFO. Then drop Rx_enable 
//  so that the CRC is not sent to FIFO. 
SEND_TO_FIFO:
	begin
	prog <= 1'b1;
	data_match <= 0;
		if (PHY_data_count == 9'd256) begin    		// have we sent 256 bytes ?
			PHY_data_count <= 0;					// reset data count 
			prog <= 1'b0;							// set prog flag
			Rx_enable <= 0;							// disable further writes to Rx_fifo
			PHY_Rx_state <= START;					// loop back for some more data.			
		end
		else begin
			Rx_enable <= 1'b1;				
			PHY_data_count <= PHY_data_count + 9'd1;
		end
	end	
	
//  Loop here until we have sent 256 bytes to the Rx FIFO. Then drop Rx_enable 
//  so that the CRC is not sent to FIFO. 
SEND_TO_J_FIFO:
	begin
	data_match <= 0;
		if (PHY_data_count == 9'd256) begin    		// have we sent 256 bytes ?
			PHY_data_count <= 0;					// reset data count 
			PROGRAM <= 1'b1;						// set PROGRAM flag
			J_Rx_enable <= 0;						// disable further writes to Rx_fifo
			PHY_Rx_state <= WAIT_ACK;
		end
		else begin
			J_Rx_enable <= 1'b1;				
			PHY_data_count <= PHY_data_count + 9'd1;
		end
	end		
	

SEND_TO_J_FIFO2:
	begin
	data_match <= 0;
		if (PHY_data_count == 9'd256) begin    		// have we sent 256 bytes ?
			PHY_data_count <= 0;					// reset data count 
			PROGRAM2 <= 1'b1;						// set prog Mercury flag now we have some data ready
			J_Rx_enable <= 0;						// disable further writes to Rx_fifo
			PHY_Rx_state <= WAIT_ACK;				// wait for next 256 bytes
		end
		else begin
			J_Rx_enable <= 1'b1;	
			PHY_data_count <= PHY_data_count + 9'd1;
		end
	end 

// loop here until we get an ACK from the JTAG code	
WAIT_ACK:
	begin
		if (PROGRAM_ACK) PHY_Rx_state <= START;
	end 

// wait until ASMI has seen the command 
ERASE:		
	begin
	data_match <= 0;
		if (erase_ACK) begin
			erase <= 1'b0;	
			PHY_Rx_state <= START;
		end 
	end
// Wait until MAC read from EEPROM is complete then send to PC	
READMAC:
	begin
		if (MAC_ready) begin
			read_MAC <= 0;			// reset read MAC flag
			send_MAC <= 1'b1; 		// set send MAC address to PC flag
		end
		if (send_MAC_ACK) begin		// loop here until the MAC address had been sent
			send_MAC <= 0; 
			PHY_Rx_state <= START;			
		end
	end
// Wait until IP read from EEPROM is complete then send to PC	
READIP:
	begin
		if (IP_ready) begin
			read_IP <= 0;			// reset read MAC flag
			send_IP <= 1'b1; 		// set send MAC address to PC flag
		end
		if (send_IP_ACK) begin		// loop here until the MAC address had been sent
			send_IP <= 0; 
			PHY_Rx_state <= START;			
		end
	end		
// wait until IP write is done then return
WRITEIP:	
	begin
		if (IP_write_done) begin
			write_IP <= 0;
			PHY_Rx_state <= START;	
		end
	end 

// wait until JTAG code has seen the command 
GET_DEVICE:		
	begin
	data_match <= 0;
			if (READ_ID_ACK) begin
				READ_ID <= 1'b0;	
				PHY_Rx_state <= START;
			end 
		//end
	end

// wait until we get and ERASE_ACK from the JTAG code
ERASE_FLASH:
	begin
		if (ERASE_ACK) begin 
			ERASE_CHIP <= 0;
			PHY_Rx_state <= START;
		end
	end 

default: PHY_Rx_state <= START;	
endcase
end 

//------------------------------------------------------------------
// 			EEPROM read/write contol
//------------------------------------------------------------------
wire [47:0]EEPROM_MAC;
wire [31:0]EEPROM_IP;
wire IP_write_done;

EEPROM EEPROM_inst(.clock(PHY_MDIO_clk), .read_MAC(read_MAC), .read_IP(read_IP), .write_IP(write_IP), .IP_to_write(IP_to_write),
					.CS(CS), .SCK(SCK), .SI(SI), .SO(SO), .This_MAC(EEPROM_MAC), .This_IP(EEPROM_IP),
					.MAC_ready(MAC_ready), .IP_ready(IP_ready), .IP_write_done(IP_write_done));


//------------------------------------------
// state machine to manage ASMI inface
//------------------------------------------

wire [7:0]ID;
wire [7:0]datain;
wire error;
wire busy;
wire data_valid;
wire [7:0]dataout;
reg [23:0]address; 		// address to read and write 
reg [3:0]state;
reg write_enable;
reg write; 
reg read_enable;
reg read;
reg [8:0]count2;
reg shift_bytes;
reg [8:0]page; 			// counts number of pages we are writing 
reg read_sid;
reg erase_done;
reg sector_erase;
reg [8:0]byte_count;	// holds number of bytes we have send to ASMI



// read 255 bytes from Rx fifo and then request more
always @ (negedge Tx_clock) // was negedge PHY_RX_CLOCK
begin 
case (state)

// see if we have 255 bytes, loop if not 
0:	begin 
		read_enable <= 0;
		read <= 0;
		read_sid <= 0;
		write_enable <= 0;
		write <= 0;
		erase_ACK <= 0;
		erase_done <= 0;
		sector_erase <= 0;
		address <= 24'h100000;		// set starting erase/program address to sector 16 (start of top 1MB) 
		byte_count <= 0;
		if (erase) 
			 state <= 1'd1;
		else if(IF_Rx_used > 254) 		
			state <= 5;		 
	end

// erase top 1MB of EPCS16	
1:	begin
		erase_ACK <= 1'b1; 	   	// let the Rx know we have seen the command
		write_enable <= 1'b1;
		sector_erase <= 1'b1;
		state <= 2;
	end 
// wait until erase has completed
2:	begin
		write_enable <= 0;
		sector_erase <= 0;
		if (busy) state <= 2;
		else if (address != 24'h1F0000) begin	// reached end of memory yet?
			address <= address + 24'h010000;	// increment address to next sector
			state <= 1;
		end 		
		else state <= 3;						// erase done, let user know
	end
// let user know that erase has completed
3:	begin
		erase_done <= 1'b1;
		state <= 4;
	end 
// wait for the Tx to ack then return, loop here otherwise
4:	begin
		if (erase_done_ACK) state <= 0;
	end
	
// program EPCS16 with data from PC
5: 	begin 
	rdreq <= 1'b1;					
	byte_count <= 0; 				// reset byte counter
	state <= state + 1'b1;
	end 
// loop until we have sent 256 bytes
6:	begin
		if (byte_count == 9'd256) begin
			byte_count <= 0;				// reset byte counter
			shift_bytes <= 0;
			rdreq <= 0; 					// stop reading from fifo
			state <= state + 1'b1;
		end 
		else begin
			write_enable <= 1'b1;			// enable write to the ASMI 
			shift_bytes <= 1'b1;			// enable loading data into the ASMI memory
			byte_count <= byte_count + 1'b1;
		end
	end 
// write the 256 bytes into the ASMI fifo and request more data
7:	begin
	write <= 1;
	state <= state + 1'b1;
	send_more <= 1'b1;		
	end 
// wait for request to be acknowledged and when we have 255 more bytes ready then increment address by 256
// and get some more data 
// Note: there is no exit from this loop - it relies on the PC not sending any more data.
8:	begin 
	write <= 0;
	write_enable <= 0;
		if (send_more_ACK) send_more <= 0;				// clear send flag once Tx has seen it
		if (!busy && IF_Rx_used > 254  && send_more == 0) begin
			address <= address + 24'd256;	// increment write address to point to next page boundry
			state <= 5;
		end
	end  
		
endcase
end 

//----------------------------
// 			ASMI  
//----------------------------

//  If you enable wren in the MegaFunction then it must be set to 1 in order to write, protect or erase.

ASMI  ASMI_inst(.addr(address),.bulk_erase(0), .busy(busy), .clkin(Tx_clock), .data_valid(data_valid),
								 .dataout(dataout), .illegal_erase(error), .rden(read_enable), .read(read), .read_sid(read_sid),
								 .epcs_id(ID), .wren(write_enable), .sector_protect(0),
								 .datain(datain), .sector_erase(sector_erase), .write(write), .shift_bytes(shift_bytes)); 

// reverse bit order into ASMI
assign datain = {IF_PHY_data[0],IF_PHY_data[1],IF_PHY_data[2],IF_PHY_data[3],IF_PHY_data[4],IF_PHY_data[5],IF_PHY_data[6],IF_PHY_data[7]};
		
//-------------------------------------------
//   Rx fifo (for Metis Prgramming)
//-------------------------------------------

/*
					    Rx_fifo (1k bytes) 
					
					---------------------
    PHY_output[7:0] |data[7:0]	  wrfull |
					|				     |
		Rx_enable	|wrreq		 wrempty | Rx_fifo_empty
					|					 |									    
	PHY_data_clock	|>wrclk	wrusedw[9:0] | Rx_fifo_used
					---------------------								
	        rdreq   |rdreq		  q[7:0] | IF_PHY_data ----> ASMI data input
					|					 |					  			
			     	|   		  rdempty| IF_PHY_rdempty 
			        |                    | 							
		Tx_clock	|>rdclk  rdusedw[9:0]| IF_Rx_used	   
					---------------------								
					|                    |
			IF_rst  |aclr                |				  			
					---------------------								

 
*/

wire IF_PHY_rdempty;
wire IF_PHY_drdy;
wire [7:0]IF_PHY_data;
wire [9:0]IF_Rx_used;
reg  rdreq;
wire Rx_fifo_empty;
wire [9:0]Rx_fifo_used;

Rx_fifo Rx_fifo_inst(.wrclk (PHY_data_clock),.rdreq (rdreq),.rdclk (Tx_clock),.wrreq(Rx_enable), 
                .data (PHY_output[7:0]),.q (IF_PHY_data), .wrusedw(), .rdempty(IF_PHY_rdempty), .rdusedw(IF_Rx_used),
                .wrfull(),.aclr(reset), .wrempty(Rx_fifo_empty));


//-------------------------------------------
//   Rx fifo JTAG (for JTAG Prgramming)
//-------------------------------------------

/*
					    Rx_fifo (1k bytes) 
					
					---------------------
	PHY_output[7:0] |data[7:0]	  wrfull |    
					|				     |
	 J_Rx_enable	|wrreq		 wrempty | 
					|					 |									    
	PHY_data_clock	|>wrclk	wrusedw[9:0] | 
					---------------------								
	      J_rdreq   |rdreq		  q[7:0] | J_PHY_data 
					|					 |					  			
			     	|   		  rdempty| J_PHY_rdempty 
			        |                    | 							
       TCK_clock	|>rdclk  rdusedw[9:0]| J_Rx_used	    
					---------------------								
					|                    |
	reset OR clear  |aclr                |							
					---------------------								

 
*/

wire J_PHY_rdempty;
wire [7:0]J_PHY_data;
wire [9:0]J_Rx_used;
reg  J_rdreq;
reg  J_Rx_enable;


Rx_fifo Rx_fifo_JTAG_inst(.wrclk (PHY_data_clock),.rdreq (J_rdreq),.rdclk (TCK_clock),.wrreq(J_Rx_enable), 
                .data (PHY_output[7:0]),.q (J_PHY_data), .wrusedw(), .rdempty(J_PHY_rdempty), .rdusedw(J_Rx_used),
                .wrfull(),.aclr(reset || clear), .wrempty(), .wrusedw());


//--------------------------------------------------------
// Respond to command passed by Rx code and action
//--------------------------------------------------------

reg [8:0] data_state;
reg JTAG_run;
reg JTAG_program;
reg [15:0]FPGA_data;
reg [31:0]IDCODES;
reg [5:0]ID_count;
reg wrreq;	
reg [15:0]data;
reg [4:0]prog_count;
wire [9:0]prog_data = 10'b0000000010;
wire [9:0]start_data = 10'b0000000011;
reg [11:0]one_count;
reg [5:0]bit_locate;
reg [33:0]TMS_data;
reg [5:0]TMS_data_bit; 
reg send_ID;
reg send_ID_ACK;
reg [9:0]clock_toggle;
reg [32:0]byte_counter; // holds number of bytes to read 
reg PROGRAM_ACK;
reg ERASE_ACK;
reg erase_confirm;
reg erase_confirm_ACK;
reg [31:0]block_number; // holds number of 256 byte blocks that PC has sent
reg [3:0] bit_counter;
reg [8:0] byte_counter2;
reg clear;					// clear the Rx_fifo after JTAG programming
reg [9:0]DISENGAGE;			// enables flash to be programmed when already blank.
reg J_send_more;			// set when more data required from PC


always @ (negedge TCK_clock)
begin
case (data_state)
// loop until we see a valid command 
0:	begin
	TCK <= 0;
	TMS <= 0;
	READ_ID_ACK <= 0;
	ID_count <= 0;
	bit_locate <= 0;
	prog_count <= 0;
	TMS_data_bit <= 0;
	one_count <= 0;
	clock_toggle <= 0;
	byte_counter <= 0;
	J_send_more <= 0;
	PROGRAM_ACK <= 0;
	ERASE_ACK <= 0;
	block_number <= 0;
	byte_counter2 <= 0;
	bit_counter <= 0;
	TDI <= 0;			
	TMS <= 0;
		if (READ_ID) begin
			READ_ID_ACK <= 1'b1;      // let Ethernet code know we have seen the request
			TMS_data <= 9'b0_0101_1111;
			data_state <= 5;
		end 
		else if (PROGRAM) begin
			PROGRAM_ACK <= 1'b1;
			TMS_data <= 34'b11_1110_1001_1011_1110_1100_1100_1101_1111;
			data_state <= 40;
		end
		else if (PROGRAM2)begin
			PROGRAM_ACK <= 1'b1;
			data_state <= 200;
		end				
		else if (ERASE_CHIP)begin
			ERASE_ACK <= 1'b1;
			data_state <= 100;
		end
	end 
 
//--------------------------------------------------------
// 			In FPGA Device ID mode
//--------------------------------------------------------	
	
// Get Device ID		
// reset JTAG chain and move to Shift-DR state, send 1,1,1,1,1,0,1,0,0 to TMS
5:	begin
	  if (TMS_data_bit == 9)
		data_state <= 8;
	  else begin
		TMS <= TMS_data[TMS_data_bit];
		data_state <= data_state + 1'b1;
	  end 
	end 
// toggle TCK	  
6:	begin
	 TCK <= 1'b1;
	 data_state <= data_state + 1'b1;
	end	
7:	begin 
	  TCK <= 0;
	  TMS_data_bit <= TMS_data_bit  + 1'b1;
	  data_state <= 5;
	end 
// now in Shift-DR state so read the IDCODES (32bits)
// TDI data is valid after posedge of TCK
8:  begin
		  if (ID_count != 32) begin
			  TCK <= 1'b1;
			  data_state <= data_state + 1'b1;
		  end 
		  else begin
				send_ID <= 1'b1;		// set flag for PHY_Tx
				data_state <= 11;  		// data read so display it
		  end
	end 
// TDO data is now valid so read it
9:	 begin 
	  IDCODES <= {TDO,IDCODES[31:1]};
	  data_state <= data_state + 1'b1;
	 end
// return TCK clock to 0 and repeat
10:  begin
	  ID_count <= ID_count + 1'b1;
	  TCK <= 0;
	  data_state <= 8;
	 end  

// wait for PHY_Tx to ack request to send IDCODES
11:	begin
		if (send_ID_ACK) begin
			send_ID <= 1'b0;	// reset send ID flag
			data_state <= 0;
		end 
	end 
	
	
//--------------------------------------------------------
// 			In Program FPGA mode
//--------------------------------------------------------	

// set DISENGAGE so we can program via JTAG with a blank flash memory
// reset JTAG chain then move to Shift_IR state, send 1,1,1,1,1,0,1,1,0,0 to TMS
40:	begin
	  if (TMS_data_bit == 10)
		data_state <= 43;
	  else begin
		TMS <= TMS_data[TMS_data_bit];
		data_state <= data_state + 1'b1;
	  end 
	end 
// toggle TCK	  
41:	begin
	 TCK <= 1'b1;
	 data_state <= data_state + 1'b1;
	end	
42:	begin 
	  TCK <= 0;
	  TMS_data_bit <= TMS_data_bit  + 1'b1;
	  data_state <= 40;
	end 
// select DISENGAGE 
43: begin
		if (prog_count == 9) TMS <= 1'b1;  // set TMS high on 10th bit
		if(prog_count != 10) begin
			TDI <= DISENGAGE [prog_count];
			data_state <= data_state + 1'b1;
		end 
		else begin
			prog_count <= 0;		// reset since we use later on
			data_state <= 46;
		end
	end
44:	begin
	prog_count <= prog_count + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
45: begin
	TCK <= 1'b0;
	data_state <= 43;
	end 
// goto Update-IR , send 1 to TMS (TMS is already high)
46: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
47: begin
	TCK <= 1'b0;
	TMS_data_bit <= 0; 		// reset data pointer
	data_state <= 50;
	end 




// reset JTAG chain then move to Shift_IR state, send 1,1,1,1,1,0,1,1,0,0 to TMS
50:	begin
	  if (TMS_data_bit == 10)
		data_state <= 53;
	  else begin
		TMS <= TMS_data[TMS_data_bit];
		data_state <= data_state + 1'b1;
	  end 
	end 
// toggle TCK	  
51:	begin
	 TCK <= 1'b1;
	 data_state <= data_state + 1'b1;
	end	
52:	begin 
	  TCK <= 0;
	  TMS_data_bit <= TMS_data_bit  + 1'b1;
	  data_state <= 50;
	end 
// select program mode, send 0x002 to TDI
53: begin
		if (prog_count == 9 ) TMS <= 1'b1;	// set TMS high on 10th bit
		if(prog_count != 10) begin
			TDI <= prog_data[prog_count];
			data_state <= data_state + 1'b1;
		end 
		else begin
			prog_count <= 0;		// reset since we use later on
			data_state <= 56;
		end
	end
54:	begin
	prog_count <= prog_count + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
55: begin
	TCK <= 1'b0;
	data_state <= 53;
	end 
// goto Update-IR , send 1 to TMS
56: begin
		if (TMS_data_bit == 11) begin
			TDI <= 1'b1;
			data_state <= 59;
		end
		else begin 
			TMS <= TMS_data[TMS_data_bit];
			data_state <= data_state + 1'b1;
		end 
	end 
		
57: begin
	TMS_data_bit <= TMS_data_bit + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
58: begin
	TCK <= 1'b0;
	data_state <= 56;
	end 
// with TDI high go to Shift_DR, send 1,0,0 to TMS
59: begin

		if (TMS_data_bit == 14)
			data_state <= 62;
		else begin 
			TMS <= TMS_data[TMS_data_bit];
			data_state <= data_state + 1'b1;
		end 
	end
60: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
61: begin
	TMS_data_bit <= TMS_data_bit + 1'b1;
	TCK <= 1'b0;
	data_state <= 59;
	end 
	
// 	Shift in shift_count  1's  with  TDI = 1 
62: begin
		if (one_count != shift_count) 
			data_state <= data_state + 1'b1;
		else if (J_Rx_used > 255) begin 
			J_rdreq <= 1'b1;				// get first byte from Rx_fifo
			data_state <= 65;
		end
	end
63: begin
	one_count <= one_count + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
64: begin
	TCK <= 1'b0;
	data_state <= 62;
	end 
	
// read file here LSB first, read bytes from the Rx_fifo	
65: begin
	J_rdreq <= 0;
		if (bit_locate != 8)begin
			TDI <= J_PHY_data [bit_locate];
			data_state <= data_state + 1'b1;		// toggle TCK
		end
		else begin
			byte_counter <= byte_counter + 1'b1;	// increment byte counter
			bit_locate <= 0;						// reset bit counter
			data_state <= 68;						// request more data from fifo 
		end 
	end
66: begin
	bit_locate <= bit_locate + 1'b1;					// point to next bit
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
67: begin
	TCK <= 1'b0;
	data_state <= 65;
	end 	
// use this state to get another word from the Rx_fifo, exit when done
68: begin
		if (byte_counter == num_bytes) begin   // 718585 
			J_send_more <= 1'b1;					// Since we send % 256 bytes we need to request the final frame
			data_state <= 70;					// we have read all the bytes so exit 
		end
		else if (!J_PHY_rdempty) begin			// loop here if no data in fifo
			J_rdreq <= 1'b1;
			data_state <= 65;
		end
		else begin								// no data so request some more
			J_send_more <= 1'b1;
			data_state <= 69;
		end
	end
// wait for data in Rx_fifo and when we  have 255 bytes continue
69:	begin
		if (send_more_ACK_sync)
			J_send_more <= 0;
		if (J_Rx_used > 255) begin
			J_rdreq <= 1'b1;
			data_state <= 65;
		end
	end
// wait until we get an ACK from the Tx code
70: begin
		if (send_more_ACK_sync) begin			// remove once we send # of bytes in packet
			J_send_more <= 0;
			data_state <= data_state + 1'b1;
		end 
	end 

// goto Update-DR, then Run-Test/Idle, send 1,1,0 to TMS
// Reset JTAG chain then go to Shift IR, send 1,1,1,1,1,0,1,1,0,0
71: begin
		if (TMS_data_bit == 27)  
			data_state <= 74;  
		else begin 
			TMS <= TMS_data[TMS_data_bit];
			data_state <= data_state + 1'b1;
		end 
end
	
72: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
73: begin
	TCK <= 1'b0;
	TMS_data_bit <= TMS_data_bit + 1'b1;
	data_state <= 71;
	end 
// select program mode, send 0x003 to TDI
74: begin
	    if(prog_count == 9) TMS <= 1'b1;   // set TMS high on 10th bit
		if(prog_count != 10) begin
			TDI <= start_data[prog_count];
			data_state <= data_state + 1'b1;
		end 
		else data_state <= 77;
	end
75:	begin
	prog_count <= prog_count + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
76: begin
	TCK <= 1'b0;
	data_state <= 74;
	end
// Go to Update-IR,then Run_Test/Idle, send 1,0
77: begin
		if (TMS_data_bit == 29)
			data_state <= 80;
		else begin 
			TMS <= TMS_data[TMS_data_bit];
			data_state <= data_state + 1'b1;
		end 
	end
78: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
79: begin
	TCK <= 1'b0;
	TMS_data_bit <= TMS_data_bit + 1'b1;
	data_state <= 77;
	end
// toggle the clock 200 times  *** PC code works without this
80: begin 
		if (clock_toggle == 200)
			data_state <= 83;
		else data_state <= data_state + 1'b1;
	end
81: begin
	clock_toggle <= clock_toggle + 1'b1;
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
82: begin
	clear <= 1'b1;							// clear any data left in Rx_fifo
	TCK <= 1'b0;
	data_state <= 80;
	end	
// reset JTAG chain,  send 1,1,1,1,1
83: begin
	clear <= 1'b0;
		if (TMS_data_bit == 34)
			data_state <= 0;					// all done so back to start
		else begin 
			TMS <= TMS_data[TMS_data_bit];
			data_state <= data_state + 1'b1;
		end 
	end
84: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
85: begin
	TCK <= 1'b0;
	TMS_data_bit <= TMS_data_bit + 1'b1;
	data_state <= 83;
	end
	
86: begin
		if (send_more_ACK_sync) begin 
			J_send_more <= 0;
			data_state <= 0;
		end 
	end
	
	
//--------------------------------------------------------
// 			Erase Flash on HPSDR board
//--------------------------------------------------------	

/*

	TCK 		- clock from Metis - 10MHz
	TMS 		- Rx_fifo write enable
	TDI 		- erase or data to Rx_fifo
	ATLAS_C5 	- either 'erased' OR 'empty', erased is a short pulse that indicates that the EPCS16 has been erased 	
	
*/	

// set TDI high to signal erase command then loop looking for ATLAS_C5 to go high which indicates erase is done
100: begin
	 TMS <= 1'b0;
	 TDI <= 1'b1;						// set erase 
		 if (ATLAS_C5) begin			// erase is done 
			TDI <= 0;
			erase_confirm <= 1'b1;
			data_state <= 103;	
		 end 
		 else data_state <= data_state + 1'b1;
	 end
// toggle the clock
101: begin
	TCK <= 1'b1;
	data_state <= data_state + 1'b1;
	end 
102: begin
	TCK <= 1'b0;
	TMS_data_bit <= TMS_data_bit + 1'b1;
	data_state <= 100;
	end  
// let the PC know the erase has been successful
103: begin 
		if (erase_confirm_ACK) begin 
			erase_confirm <= 0;
			data_state <= 0;				// return 
		end
	  end
	 
//--------------------------------------------------------
// 			Program Flash on HPSDR board
//--------------------------------------------------------	

/*

	ATLAS_C6    - clock from Metis - 10MHz
	TMS 		- Rx_fifo write enable
	TDI 		- erase or data to Rx_fifo
	ATLAS_C5 	- either 'erased' OR 'empty', erased is a short pulse that indicates that the EPCS16 has been erased 	
	
*/		 

// send data to the Rx_fifo on Mercury when empty	 
200: begin
		if (ATLAS_C5 && J_Rx_used > 254)  	// Was ATLAS_C9 Rx_fifo on Mercury is empty so send 256 bytes serially when we have enough data
			data_state <= 201; 
	 end 

201: begin 
	 J_rdreq <= 1'b1;						// get a byte from the Rx_fifo	
	 data_state <= data_state + 1'b1;
	 end 
	 
202: begin
	 J_rdreq <= 0; 
		if (byte_counter2 == 256) begin 
			byte_counter2 <= 0; 					// reset byte_counter
			block_number <= block_number + 1'b1;
			J_send_more <= 1'b1;
			//ATLAS_C7 <= 1'b0;						// disable writes to Mercury Rx_fifo
			TMS <= 1'b0;							// disable writes to Mercury Rx_fifo		
			data_state <= 205;						// get some more data from the PC
		end 
		else if (bit_counter == 8) begin			// we have sent this byte so get another one 
			bit_counter	 <= 1'b0;					// reset bit counter
			byte_counter2 <= byte_counter2 + 1'b1;	// increment byte counter
			//ATLAS_C7 <= 1'b0;						// disable writes to Mercury Rx_fifo
			TMS <= 1'b0;							// disable writes to Mercury Rx_fifo
			data_state <= 201; 						// get the next byte
		end 
		else begin
			//ATLAS_C7 <= 1'b1;							// enable Rx_fifo on Mercury
			TMS <= 1'b1;							// enable Rx_fifo on Mercury
			//ATLAS_C6 <= IF_PHY_data[bit_counter];		// get the data to send
			TDI <= J_PHY_data[bit_counter];		// get the data to send
			data_state <= data_state + 1'b1;
		end
	  end
// clock data into Mercury Rx_fifo
203: begin
	 //ATLAS_C7 <= 1'b0;							//disable write to Target fifo
	 TMS <= 1'b0;							//disable write to Target fifo
	 bit_counter <= bit_counter + 1'b1;			// increment bit count
	 data_state <= 202;
	 end 
	 
// if not done then request some more data from the PC
205: begin
		if (block_number == num_blocks) begin 
			block_number <= 0; 					// reset block counter
			//ATLAS_C7 <= 1'b0;					// remove write enable on Mercury Rx_fifo
			TMS <= 1'b0;					// remove write enable on Mercury Rx_fifo
			data_state <= 0; 					// all done!
		end 
	 	else if (send_more_ACK) begin 
			J_send_more <= 0;						// clear send flag once Tx has seen it
			data_state <= 200;
		end 
	 end 
	 
	  
default: data_state <= 0;
endcase
end

// clock Rx_fifo in Target on postive edge of this clock
assign ATLAS_C6 = TCK_clock; 


//  sync across clock domains
reg A,B,send_more_sync, send_more_ACK_sync;

always @ (posedge TCK_clock)
	{send_more_ACK_sync, A} <= {A, send_more_ACK};
	
always @ (posedge Tx_clock_2)
	{send_more_sync, B} <= {B, J_send_more};
	

//------------------------------------------
//   State Machine to send Tx data to PHY
//-------------------------------------------

/*
       In response to an erase_done or send_more command it sends the Raw Ethernet header followed by the response then 46
       bytes of 0x00. It enables the CRC32 generator when sending the header and adds to the end of the data.
       Code then loops to the start.
*/

// HPSDR specific			
parameter HPSDR_frame = 8'h03;  // HPSDR Frame type 
parameter Type_1 = 8'hEF;	    // Ethernet Frame type
parameter Type_2 = 8'hFE;

localparam	RESET 	= 0,
			SEND  	= 1,
			SENDMAC = 2,
			SENDIP	= 3,
			SENDID  = 4,
			CRC 	= 5;
			
wire [31:0] CRC32;				   	// holds 802.3 CRC result 
reg  [31:0] temp_CRC32 = 0; 

reg [6:0] state_Tx;             	
reg [10:0] data_count;
reg reset_CRC;
reg [7:0] Tx_data;
reg [4:0] gap_count;
reg [6:0] rdaddress;
reg [7:0] pkt_data;
reg erase_ACK;
reg erase_done_ACK;
reg [7:0] reply;  		// 0x01 if completed OK, 02 for send 256 more bytes
reg [5:0]interframe; 	// hold Ethernet interframe delay value
reg [4:0]MAC_count;		// holds shift count when sending MAC to PHY Tx fifo
reg [47:0]temp_MAC;		// holds MAC address whilst we are shifting
reg [4:0]IP_count;		// holds shift count when sending IP to PHY TX fifo
reg [4:0]ID_counter;	// holds shift count when sending IDCODE to PHY TX fifo
reg [31:0]temp_IP;		// holds IP address whilst we are shifting
reg [31:0]temp_ID;		// holds IDCODE whilst we are shifting


always @ * 
case(rdaddress)
// Ethernet preamble
  0 : pkt_data <= 8'h55;
  1 : pkt_data <= 8'h55;
  2 : pkt_data <= 8'h55;
  3 : pkt_data <= 8'h55;
  4 : pkt_data <= 8'h55;
  5 : pkt_data <= 8'h55;
  6 : pkt_data <= 8'h55;
  7 : pkt_data <= 8'hD5;
// Ethernet header
  8 : pkt_data <= PC_MAC[47:40];		// MAC address of PC we are sending to 
  9 : pkt_data <= PC_MAC[39:32];
  10: pkt_data <= PC_MAC[31:24];
  11: pkt_data <= PC_MAC[23:16];
  12: pkt_data <= PC_MAC[15:8];
  13: pkt_data <= PC_MAC[7:0];
  14: pkt_data <= This_MAC[47:40]; 		// MAC address of this OzyII Board
  15: pkt_data <= This_MAC[39:32]; 
  16: pkt_data <= This_MAC[31:24];
  17: pkt_data <= This_MAC[23:16]; 
  18: pkt_data <= This_MAC[15:8];  
  19: pkt_data <= This_MAC[7:0];   
// Start of Payload
  20: pkt_data <= Type_1;	    		// Ethernet Frame type 0xEFFE (HPSDR)
  21: pkt_data <= Type_2;
  22: pkt_data <= HPSDR_frame;			// HPSDR Frame type 
  23: pkt_data <= reply;				// command result
  // send data here then	
  // followed by 42 bytes of 0x00
  // then CRC32
  24: pkt_data <= CRC32[7:0];
  25: pkt_data <= temp_CRC32[15:8];
  26: pkt_data <= temp_CRC32[23:16];
  27: pkt_data <= temp_CRC32[31:24];

  default: pkt_data <= 0;
endcase


always @ (negedge Tx_clock_2)				// clock at half speed since we read bytes but write nibbles
begin
case(state_Tx)

RESET:
   begin
        sync_Tx_CTL <= 1'b0;
        data_count <= 0;
        reset_CRC <= 0;
        rdaddress <= 0;
        send_ID_ACK <= 1'b0;
        send_more_ACK <= 1'b0;
        erase_confirm_ACK <= 1'b0;
        erase_done_ACK <= 1'b0;
        reply <= 0; 
        send_more_ACK <= 0;
        send_MAC_ACK <= 0;
        MAC_count <= 0;
        send_IP_ACK <= 0;
        IP_count <= 0;
        ID_counter <= 0;			
        
        if (reset)
			state_Tx <= RESET;
		else if (erase_done || erase_confirm) begin					
				erase_done_ACK <= 1'b1; 								
				erase_confirm_ACK <= 1'b1;
				reply <= 8'h01;					   	// confirm erase was OK to PC
				state_Tx <= SEND;
		end
		else if (send_more || send_more_sync) begin							
				//  ***** send_more_ACK <= 1'b1 in JTAG.v
				reply <= 8'h02;						// tell the PC to send more data
				state_Tx <= SEND;
			end
		else if (send_MAC) begin					// send the MAC address held in the EEPROM
				reply <= 8'h3;
				send_MAC_ACK <= 1'b1;
				temp_MAC <= EEPROM_MAC;
				state_Tx = SENDMAC;
		end 
		else if (send_IP) begin						// send the MAC address held in the EEPROM
				reply <= 8'h4;
				send_IP_ACK <= 1'b1;
				temp_IP <= EEPROM_IP;
				state_Tx = SENDIP;
		end 
		else if (send_ID) begin						// send the IDCODE
				reply <= 8'h05;					    // confirm read ID  was OK to PC
				send_ID_ACK <= 1'b1; 				// ACK the send_ID request
				temp_ID <= IDCODES;
				state_Tx <= SENDID;
		end
		else state_Tx <= RESET;			
   end

// start sending Ethernet data 
SEND:	
    begin
		if (rdaddress != 24)						// keep sending until we reach the end of the fixed data 
		begin
			Tx_data <= pkt_data;
			sync_Tx_CTL <= 1'b1;					// enable write to Tx FIFO
			rdaddress <= rdaddress + 1'b1;
			state_Tx <= SEND;
		end 
		else 
		// send 46 bytes of 0x00   
		begin
			if (data_count != 46) begin
				Tx_data <= 0;  				
				data_count <= data_count + 1'b1;		// increment loop counter
				state_Tx <= SEND;
			end
			else begin
    			temp_CRC32 <= CRC32;					// grab the CRC data since it will change next clock pulse
				Tx_data <= pkt_data;					// send CRC32 to PHY
				rdaddress <= rdaddress + 1'b1; 
				state_Tx <= CRC;
			end 
		end  											// done, so now add the remainder of the CRC32 
		
		if (rdaddress == 7)
			reset_CRC <= 1'b1; 							// start CRC32 generation
		else 
			reset_CRC <= 1'b0;
    end   
    
SENDMAC:
    begin
		if (rdaddress != 24) begin						// keep sending until we reach the end of the fixed data 
			Tx_data <= pkt_data;
			sync_Tx_CTL <= 1'b1;					// enable write to Tx FIFO
			rdaddress <= rdaddress + 1'b1;
			state_Tx <= SENDMAC;
		end 
		// else send the MAC address
		else if (MAC_count != 7) begin
			Tx_data = temp_MAC[47:40];
			temp_MAC <= {temp_MAC[39:0],8'h00};
			MAC_count <= MAC_count + 1'b1;
			state_Tx <= SENDMAC;
		end 		
		// now send 40 bytes of 0x00 
		else if (data_count != 40) begin
			Tx_data <= 0;  				
			data_count <= data_count + 1'b1;		// increment loop counter
			state_Tx <= SENDMAC;
		end
		else begin
			temp_CRC32 <= CRC32;					// grab the CRC data since it will change next clock pulse
			Tx_data <= pkt_data;					// send CRC32 to PHY
			rdaddress <= rdaddress + 1'b1; 
			state_Tx <= CRC;						// done, so now add the remainder of the CRC32
		end 
 											 
		
		if (rdaddress == 7)
			reset_CRC <= 1'b1; 							// start CRC32 generation
		else 
			reset_CRC <= 1'b0;
    end 

SENDIP:
    begin
		if (rdaddress != 24) begin						// keep sending until we reach the end of the fixed data 
			Tx_data <= pkt_data;
			sync_Tx_CTL <= 1'b1;					// enable write to Tx FIFO
			rdaddress <= rdaddress + 1'b1;
			state_Tx <= SENDIP;
		end 
		// else send the IP address
		else if (IP_count != 5) begin
			Tx_data = temp_IP[31:24];
			temp_IP <= {temp_IP[23:0],8'h00};
			IP_count <= IP_count + 1'b1;
			state_Tx <= SENDIP;
		end 		
		// now send 42 bytes of 0x00 
		else if (data_count != 42) begin
			Tx_data <= 0;  				
			data_count <= data_count + 1'b1;		// increment loop counter
			state_Tx <= SENDIP;
		end
		else begin
			temp_CRC32 <= CRC32;					// grab the CRC data since it will change next clock pulse
			Tx_data <= pkt_data;					// send CRC32 to PHY
			rdaddress <= rdaddress + 1'b1; 
			state_Tx <= CRC;						// done, so now add the remainder of the CRC32
		end 
 											 
		
		if (rdaddress == 7)
			reset_CRC <= 1'b1; 						// start CRC32 generation
		else 
			reset_CRC <= 1'b0;
    end 
    
SENDID:
    begin
		if (rdaddress != 24) begin					// keep sending until we reach the end of the fixed data 
			Tx_data <= pkt_data;
			sync_Tx_CTL <= 1'b1;					// enable write to Tx FIFO
			rdaddress <= rdaddress + 1'b1;
			state_Tx <= SENDID;
		end 
		// else send the IDCODE
		else if (ID_counter != 5) begin
			Tx_data = temp_ID[31:24];
			temp_ID <= {temp_ID[23:0],8'h00};
			ID_counter <= ID_counter + 1'b1;
			state_Tx <= SENDID;
		end 		
		// now send 42 bytes of 0x00 
		else if (data_count != 42) begin
			Tx_data <= 0;  				
			data_count <= data_count + 1'b1;		// increment loop counter
			state_Tx <= SENDID;
		end
		else begin
			temp_CRC32 <= CRC32;					// grab the CRC data since it will change next clock pulse
			Tx_data <= pkt_data;					// send CRC32 to PHY
			rdaddress <= rdaddress + 1'b1; 
			state_Tx <= CRC;						// done, so now add the remainder of the CRC32
		end 
 											 
		
		if (rdaddress == 7)
			reset_CRC <= 1'b1; 							// start CRC32 generation
		else 
			reset_CRC <= 1'b0;
    end 
    
  
// add remainder of CRC32
CRC:
	begin
	send_more_ACK <= 1'b1;						
		if (rdaddress != 28) begin				
			Tx_data <= pkt_data;
			rdaddress <= rdaddress + 1'b1;
			state_Tx <= CRC;     
		end
		else begin
			sync_Tx_CTL <= 1'b0;						// disable PHY write
			if (interframe == 10) begin					// interframe delay as required by Ethernet specification
				interframe <= 0;
				state_Tx <= RESET; 						// send complete, loop back to start
			end
			else interframe <= interframe + 1'b1;							
		end
	end
default: state_Tx <= RESET;
endcase
end     



//----------------------------
//   802.3 CRC32 Calculation
//----------------------------

CRC32 CRC32_inst(.rst(reset_CRC),.clk(Tx_clock_2), .data(Tx_data), .crc(CRC32)); 


//---------------------------
//   Send data to PHY
//---------------------------

// Data to send is in Tx_data in bytes. 
// When sync_Tx_CTL is true we alternate sending low and high nibbles 


reg [4:0]PHY_state;
reg sync_Tx_CTL;
reg [3:0]sync_TD;


always @ (negedge Tx_clock)  
begin
case (PHY_state)
// send low nibble
0:	begin 
		if (sync_Tx_CTL) begin
			sync_TD <= Tx_data[3:0];
			PHY_state <= 1'b1;
		end	
	else PHY_state <= 0;
	end
// now send high nibble
1:	begin
	sync_TD <= Tx_data[7:4];
	PHY_state <= 0;
	end

endcase
end

// sync data to PHY on Tx clock, clock PHY on ~Tx_clock so we clock in middle of data

always @ (posedge Tx_clock)
begin
	 PHY_TX <= sync_TD;
	 PHY_TX_EN <= sync_Tx_CTL; 
end 

//-----------------------------------------------------------
//  		LED Control  
//-----------------------------------------------------------

// LEDS are connected to the RAM address lines e.g. LED0 = RAM_A0

parameter half_second = 5000000; // at 25MHz clock rate

// flash LED0 for ~ 0.2 second whenever the PHY gets data
Led_flash Flash_LED0(.clock(Tx_clock), .signal(PHY_DV), .LED(RAM_A0), .period(half_second)); 		

// flash LED1 for ~0.2 seconds whenever we gat data to this MAC address
Led_flash Flash_LED1(.clock(Tx_clock), .signal(data_match), .LED(RAM_A1), .period(half_second));

// flash LED2 for ~0.2 seconds whenever we get an erase command
Led_flash Flash_LED2(.clock(Tx_clock), .signal(prog), .LED(RAM_A2), .period(half_second)); 
// flash LED3 for ~0.2 seconds when we get a program command 
Led_flash Flash_LED3(.clock(Tx_clock), .signal(IP_write_done), .LED(RAM_A3), .period(half_second)); // *** READ_ID_ACK

// flash LED5 for ~0.2 seconds when we send data to PHY  
Led_flash Flash_LED5(.clock(Tx_clock), .signal(sync_Tx_CTL), .LED(RAM_A5), .period(half_second));

//Flash Heart beat LED
reg [26:0]HB_counter;
always @(posedge PHY_CLK125) HB_counter = HB_counter + 1'b1;
assign HEART_BEAT = HB_counter[25];  // Blink

//------------------------------------------------------------
//   Multi-state LED Control    
//------------------------------------------------------------

parameter clock_speed = 25000000; // 25MHz clock 

// display state of PHY negotiations  - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
Led_control #(clock_speed) Control_LED0(.clock(Tx_clock), .on(speed_1000T), .fast_flash(~speed_100T || ~speed_1000T),
										.slow_flash(speed_100T), .LED(RAM_A4));

assign RAM_A6  = 0;						// LED 6		
assign RAM_A10 = ~IF_PHY_rdempty;		// LED 8
assign RAM_A11 = ~send_more_ACK;		// LED 9
assign RAM_A12 = ~send_more;			// LED 10

assign RAM_A13 = ~busy; 				// turn on bottom LED when busy  

endmodule

