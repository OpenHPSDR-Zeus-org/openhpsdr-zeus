/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
// spidata.h
// header for SPI data I/O
/////////////////////////////////////////////////////////////////////////

#ifndef __SPIDATA_H
#define __SPIDATA_H

// MCP23S17 Registers (requires IOCON.bank=0)
#define IODIRA 0x00
#define IODIRB 0x01
#define IPOLA 0x02
#define IPOLB 0x03
#define GPINTENA 0x04
#define GPINTENB 0x05
#define DEFVALA 0x06
#define DEFVALB 0x07
#define INTCONA 0x08
#define INTCONB 0x09
#define IOCON 0x0A
// note IOCON also appears at address 0x0B
#define GPPUA 0x0C
#define GPPUB 0x0D
#define INTFA 0x0E
#define INTFB 0x0F
#define INTCAPA 0x10
#define INTCAPB 0x11
#define GPIOA 0x12
#define GPIOB 0x13
#define OLATA 0x14
#define OLATB 0x15


#define VMCPENCODERADDR 0             // 1st 23S17: 16 bit encoder input
#define VMCPMATRIXADDR 1              // 2nd 23S17: sw matrix column output & row input


//
// function to write 8 bit value to MCP23017
// chipadress =  0 or 1; CS worked out automatically
//
void WriteMCPRegister(byte ChipAddress, byte RegAddress, byte Value);


///
// function to read 8 bit value from MCP23017
// returns register value
// chipadress =  0x0 or 0x1; CS worked out automatically
//
byte ReadMCPRegister(byte ChipAddress, byte RegAddress);


//
// function to read 16 bit value from MCP23017 consecutive addresses
// returns register value
// chipadress =  0x0 or 0x1; CS worked out automatically
//
unsigned int ReadMCPRegister16(byte ChipAddress, byte RegAddress);




//
// function to initialise SPI driver and two MCP23S17 devices
//
void InitSPI(void);

#endif      // ifndef