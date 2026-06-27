/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// encoder.c
// this file holds the code to manage the rotary encoders
// it needs two technologies:
// interrupt driven code for optical VFO encoder (bounce free)
// polled code for very bouncy mechanical encoders for other controls
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"
#include "mechencoder2.h"
#include "opticalencoder.h"

#include "types.h"
#include "encoders.h"
#include "iopins.h"
#include "configdata.h"
#include "cathandler.h"
#include "tiger.h"
#include "button.h"
#include "led.h"
#include <Wire.h>


#define VVFOCYCLECOUNT 10                                // check every 10 ticks                                 
byte GMechEncoderDivisor;                                // number of edge events per declared click
byte GVFOCycleCount;                                     // remaining ticks until we test the VFO encoder 


//
// note encoder numbering:
// in the software switches are numbered 0-20, and encoders 0-7. The VFO encoder is treated separately.
// these correspond to the control of Kjell's PCB as follows:
//
// encoder numbering:
//    PCB   software
//    VFO    (treated separately)
//    1      0
//    2      1
//    3      2
//    4      3
//    5      4
//    6      5
//    7      6
//    8      7
//    9      8
//    10     9
//    11     10
//    12     11


//
// global variables
//

//
// 13 encoders: one VFO (fast) encoder and 12 "normal" ones 
//
//Encoder VFOEncoder(VPINVFOENCODERA, VPINVFOENCODERB);

long old_ct;

struct  EncoderData                           // holds data for one slow encoder
{
  NoClickEncoder2* Ptr;                          // ptr to class
  int16_t LastPosition;                       // previous position
};

  EncoderData EncoderList[VMAXENCODERS];


  
//
// defines for the MCP23017 address and registers within it
// MCP23017 operated with IOCON.BANK=0
//
#define VMCPENCODERADDR 0x20
#define VGPIOAADDR 0x12             // A and B read as 16 bit register; 4x4 encoder pins
#define VGPPUA 0x0C                 // pullup control for GPIO A
#define VGPPUB 0x0D                 // pullup control for GPIO B


//
// function to read 16 bit input from MCP23017
// returns GPIOB (top 8 bits) GPIO A (bottom 8 bits)
// (GPIOB7....GPIOB0)(GPIOA7....GPIOA0)
unsigned int ReadEncoderMCP(void)
{
  byte Input;                                   // becomes the new bit sequence for an input
  byte Input2;
  unsigned int Input23017;                      // 16 bit value
  Wire.beginTransmission(VMCPENCODERADDR);
  Wire.write(VGPIOAADDR);                                     // point to GPIOA
  Wire.endTransmission();
  Wire.requestFrom(VMCPENCODERADDR, 2);                            // read 2 bytes
  Input=Wire.read();                                    // GPIOA
  Input2 = Wire.read();                                 // GPIOB
  Input23017 = (Input2 << 8) | Input;
  return Input23017;
}


//
// function to read encoders 9-12
// returns encoder 12:(bits 7:6) 11:(bits 5:4) 10:(bits 3:2) 9:(bits 1:0) 
//
byte ReadDirectWiredEncoders(void)
{
  byte Result = 0;
  if(digitalRead(VPINENCODER9B))
    Result |= 0b1;
  if(digitalRead(VPINENCODER9A))
    Result |= 0b10;
  if(digitalRead(VPINENCODER10B))
    Result |= 0b100;
  if(digitalRead(VPINENCODER10A))
    Result |= 0b1000;
  if(digitalRead(VPINENCODER11B))
    Result |= 0b10000;
  if(digitalRead(VPINENCODER11A))
    Result |= 0b100000;
  if(digitalRead(VPINENCODER12B))
    Result |= 0b1000000;
  if(digitalRead(VPINENCODER12A))
    Result |= 0b10000000;

  return Result;
}



//
// initialise - set up pins & construct data
// these are constructed now because otherwise the configdata settings wouldn't be available yet.
// read initial inputs first, to be able to pass the data to the constructor
//
void InitEncoders(void)
{
  unsigned int EncoderValues;                   // encoder 1-8 values
  byte Encoder9_12;
  byte BitState;                                // 2 bits setting of one encoder                 

  GVFOCycleCount = VVFOCYCLECOUNT;              // tick count
//
// initialise pullup resistors on the MCP23017 inputs
//
  WriteMCPRegister(VMCPENCODERADDR, VGPPUA, 0xFF);                     // make row inputs have pullup resistors
  WriteMCPRegister(VMCPENCODERADDR, VGPPUB, 0xFF);                     // make row inputs have pullup resistors

  Encoder9_12 = ReadDirectWiredEncoders();      // read encoders that are direct wired
  EncoderValues = ReadEncoderMCP();             // read 16 bit encoder values
  
  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[3].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[2].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[1].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[0].Ptr = new NoClickEncoder2(GEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[7].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[6].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderValues = EncoderValues >> 2;            // ready for next encoder
  EncoderList[5].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = (byte)(EncoderValues & 0b11);      // take bottom 2 bits
  EncoderList[4].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  BitState = Encoder9_12 & 0b11;
  EncoderList[8].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  Encoder9_12 = Encoder9_12 >> 2;
  BitState = Encoder9_12 & 0b11;
  EncoderList[9].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  Encoder9_12 = Encoder9_12 >> 2;
  BitState = Encoder9_12 & 0b11;
  EncoderList[10].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  Encoder9_12 = Encoder9_12 >> 2;
  BitState = Encoder9_12 & 0b11;
  EncoderList[11].Ptr = new NoClickEncoder2(GMechEncoderDivisor, BitState, true);

  InitOpticalEncoder();
}



//
// encoder 2ms tick
// these are all now serviced at this rate, with a total time used of around 35 microseconds
// 
void EncoderTick(void)
{
  unsigned int EncoderValues;
  byte Encoder9_12;
  
  EncoderValues = ReadEncoderMCP();             // read 16 bit encoder values
  Encoder9_12 = ReadDirectWiredEncoders();      // read encoders that are direct wired
  
  EncoderList[3].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;
  
  EncoderList[2].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;

  EncoderList[1].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;

  EncoderList[0].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;

  EncoderList[7].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;

  EncoderList[6].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;

  EncoderList[5].Ptr->service((byte)(EncoderValues & 0b11));
  EncoderValues = EncoderValues >> 2;
 
  EncoderList[4].Ptr->service((byte)(EncoderValues & 0b11));

  EncoderList[8].Ptr->service(Encoder9_12 & 0b11);      // feed encoder 9 with bottom bits

  Encoder9_12 = Encoder9_12 >> 2;
  EncoderList[9].Ptr->service(Encoder9_12 & 0b11);      // feed encoder 10 with bits 2,3

  Encoder9_12 = Encoder9_12 >> 2;
  EncoderList[10].Ptr->service(Encoder9_12 & 0b11);     // feed encoder 11 with bits 4,5

  Encoder9_12 = Encoder9_12 >> 2;
  EncoderList[11].Ptr->service(Encoder9_12 & 0b11);     // feed encoder 12 with bits 6,7

  int16_t Movement;                                         // normal encoder movement since last update
  byte Cntr;                                                // count encoders
  
  for (Cntr=0; Cntr < VMAXENCODERS; Cntr++)
  {
    Movement = EncoderList[Cntr].Ptr->getValue();
    if (Movement != 0) 
    {
      EncoderList[Cntr].LastPosition += Movement;
      CATHandleEncoder(Cntr, Movement);
      if (Cntr == VPWMENCODER)                              // if encoder for PWM change
        PWMUpdate(Movement);
    }
  }

//
//read the VFO encoder; divide by N to get the desired step count
// we only process it every 10 ticks (20ms) to allow several ticks to build up to minimise CAT command rate
//
  if (--GVFOCycleCount == 0)
  {
    GVFOCycleCount = VVFOCYCLECOUNT;
    signed char ct = ReadOpticalEncoder();
    if (ct != 0)
      CATHandleVFOEncoder(ct);
  }
}


//
// set divisors
// this sets whether events are generated every 1, 2 or 4 edge events
//
void SetEncoderDivisors(byte EncoderDivisor, byte VFOEncoderDivisor)
{
  GMechEncoderDivisor = EncoderDivisor;
  SetOpticalEncoderDivisor(VFOEncoderDivisor);
}
