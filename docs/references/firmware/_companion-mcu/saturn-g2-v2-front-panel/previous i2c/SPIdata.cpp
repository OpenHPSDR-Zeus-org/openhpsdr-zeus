/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
// spidata.cpp
// SPI data I/O
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "spidata.h"
#include "iopins.h"
#include <SPI.h>

//
//MCP23017 #0:
// PORTA= encoder inputs; needs pullup
// PORTB= encoder inputs, needs pullup
//MCP23017 #1:
// PORTA=  LED (4:7) & sw matrix row (3:0) outputs. 
// only one of the switch matrix row outputs should be enabled as an output, with data = 0
// PORTB= sw matrix row inputs; needs pullup


//
// use SPI mode 0, MSB first, suggest 1MHz bit rate
//


SPISettings myspiSettings(1000000, MSBFIRST, SPI_MODE0);

//
// function to write 8 bit value to MCP23017
// chipadress =  0 or 1; CS worked out automatically
//
void WriteMCPRegister(byte ChipAddress, byte RegAddress, byte Value)
{
  byte Opcode = 0x40;

  if(ChipAddress == 1)
    Opcode += 2;

  if(ChipAddress == 0)                                  // assert the correct chip select
    digitalWrite(VPINMCPCS0, LOW);
  else
    digitalWrite(VPINMCPCS1, LOW);

  SPI.beginTransaction(myspiSettings);
  SPI.transfer(Opcode);                                 // point to register
  SPI.transfer(RegAddress);                             // write its address
  SPI.transfer(Value);                                  // write its data
  
  SPI.endTransaction();
  if(ChipAddress == 0)                                  // deassert chip select
    digitalWrite(VPINMCPCS0, HIGH);
  else
    digitalWrite(VPINMCPCS1, HIGH);
}


//
// function to read 8 bit value from MCP23017
// returns register value
// chipadress =  0x0 or 0x1; CS worked out automatically
//
byte ReadMCPRegister(byte ChipAddress, byte RegAddress)
{
  byte Opcode = 0x41;                                   // read bit set
  byte Value;                                           // return value

  if(ChipAddress == 1)
    Opcode += 2;

  if(ChipAddress == 0)                                  // assert the correct chip select
    digitalWrite(VPINMCPCS0, LOW);
  else
    digitalWrite(VPINMCPCS1, LOW);

  SPI.beginTransaction(myspiSettings);
  SPI.transfer(Opcode);                                 // point to register
  SPI.transfer(RegAddress);                             // write its address
  Value = SPI.transfer(0x00);                           // write (null) to read back data
  SPI.endTransaction();
  if(ChipAddress == 0)                                  // deassert chip select
    digitalWrite(VPINMCPCS0, HIGH);
  else
    digitalWrite(VPINMCPCS1, HIGH);
  return Value;
}


//
// function to read 16 bit value from MCP23017 consecutive addresses
// returns register value GPIOB (top 8 bits) GPIO A (bottom 8 bits)
// (GPIOB7....GPIOB0)(GPIOA7....GPIOA0)
// chipadress =  0x0 or 0x1; CS worked out automatically
//
unsigned int ReadMCPRegister16(byte ChipAddress, byte RegAddress)
{
  byte Opcode = 0x41;                                   // read bit set
  byte Read1, Read2;
  unsigned int Data;
  if(ChipAddress == 1)
    Opcode += 2;

  if(ChipAddress == 0)                                  // assert the correct chip select
    digitalWrite(VPINMCPCS0, LOW);
  else
    digitalWrite(VPINMCPCS1, LOW);

  SPI.beginTransaction(myspiSettings);
  SPI.transfer(Opcode);                                 // point to register
  SPI.transfer(RegAddress);                             // write its address
  Read1 = SPI.transfer(0x0);                            // write (null) to read back data
  Read2 = SPI.transfer(0x0);                            // write (null) to read back data
  SPI.endTransaction();
  if(ChipAddress == 0)                                  // deassert chip select
    digitalWrite(VPINMCPCS0, HIGH);
  else
    digitalWrite(VPINMCPCS1, HIGH);
  Data = (Read2 << 8) | Read1;
  return Data;
}



//
// function to initialise SPI driver and two MCP23S17 devices
//
void InitSPI(void)
{
  SPI.begin();
//
// initialise pullup resistors on the MCP23017 inputs
//
  WriteMCPRegister(VMCPENCODERADDR, IODIRA, 0xFF);                    // make Direction register A = FF (all input)
  WriteMCPRegister(VMCPENCODERADDR, IODIRB, 0xFF);                    // make Direction register B = FF (all input)
  WriteMCPRegister(VMCPENCODERADDR, GPPUA, 0xFF);                     // make row inputs have pullup resistors
  WriteMCPRegister(VMCPENCODERADDR, GPPUB, 0xFF);                     // make row inputs have pullup resistors

  WriteMCPRegister(VMCPMATRIXADDR, IODIRA, 0xFF);                     // make Direction register A = FF (all input) (changed dynamically)
  WriteMCPRegister(VMCPMATRIXADDR, IODIRB, 0xFF);                     // make Direction register B = FF (all input)
  WriteMCPRegister(VMCPMATRIXADDR, GPIOA, 0b11110000);                // make GPIO register A assert LEDS to 1, columns to 0
  WriteMCPRegister(VMCPMATRIXADDR, GPPUB, 0xFF);                      // make row inputs have pullup resistors
}