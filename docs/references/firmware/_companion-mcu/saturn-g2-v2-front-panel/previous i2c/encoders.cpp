/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// encoders.cpp
// this file holds the code to manage the rotary encoders
// it needs two technologies:
// interrupt driven code for optical VFO encoder (bounce free)
// polled code for very bouncy mechanical encoders for other controls
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"
#include "mechencoder2.h"
#include "opticalencoder.h"

#include "encoders.h"
#include "iopins.h"
#include "configdata.h"
#include "eventqueue.h"
#include "button.h"
#include "led.h"
#include "SPIdata.h"


#define VVFOCYCLECOUNT 10                                // check every 10 ticks                                 
byte GMechEncoderDivisor;                                // number of edge events per declared click
byte GVFOCycleCount;                                     // remaining ticks until we test the VFO encoder 


//
// note encoder numbering:
// in the software switches are numbered 0-20, and encoders 0-7. The VFO encoder is treated separately.
// these correspond to the controls of Abhi's PCB as follows:
//
// encoder numbering:
//    PCB   software
//    VFO    (treated separately)
//    1U     0
//    1L     1
//    2U     2
//    2L     3
//    3U     4
//    3L     5
//    4U     6
//    4L     7
//    5U     8
//    5L     9
// encoder 6 upper, lower are encoded as numbers 10, 11 BUT the encoder is not on the PCB
// if encoder shift NOT active, encoder 5 generates events for s/w numbers 8,9
// if encoder shift IS active, encoder 5 generates events for s/w numbers 10,11
//


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
// function to read encoders 9-12
// returns encoder 10:(bits 3:2) 9:(bits 1:0) 
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

  Encoder9_12 = ReadDirectWiredEncoders();      // read encoders that are direct wired
  EncoderValues = ReadMCPRegister16(0, GPIOA);             // read 16 bit encoder values
  
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

  InitOpticalEncoder();
}



//
// encoder 2ms tick
// these are all now serviced at this rate, with a total time used of around 35 microseconds
// 
void EncoderTick(void)
{
  unsigned int EncoderValues;                   // 4 dual encoder pins
  byte Encoder9_12;                             // 1 dual encoder pins
  byte EventData = 0;                           // o/p data
  
  EncoderValues = ReadMCPRegister16(0, GPIOA);             // read 16 bit encoder values
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

  int16_t Movement;                                         // normal encoder movement since last update
  byte Cntr;                                                // count encoders
  
  for (Cntr=0; Cntr < VMAXENCODERS; Cntr++)
  {
    Movement = EncoderList[Cntr].Ptr->getValue();
    if (Movement != 0) 
    {
      EncoderList[Cntr].LastPosition += Movement;
      if(GEncoderShiftActive && (Cntr >= 8))
        EventData = (Movement & 0x07) | ((Cntr+2) << 3);
      else
        EventData = (Movement & 0x07) | (Cntr << 3);
      AddEvent2Q(eEvEncoderStep, EventData);
    }
  }

//
//read the VFO encoder; divide by N to get the desired step count
// we only process it every 10 ticks (20ms) to allow several ticks to build up to minimise CAT command rate
// at 4 turns per second, get 2000 steps/s ie ~40 steps per 20ms, which is enough
// clip to 7 signed bits
//
  if (--GVFOCycleCount == 0)
  {
    GVFOCycleCount = VVFOCYCLECOUNT;

    signed char ct = ReadOpticalEncoder();
    if (ct != 0)
    {
      if(ct > 63)
        ct=63;
      else if(ct<-63)
        ct=-63;
      EventData = (byte)ct;
      AddEvent2Q(eEvVFOStep, EventData);
    }
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
