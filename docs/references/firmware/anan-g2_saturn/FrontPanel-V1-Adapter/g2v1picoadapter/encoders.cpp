/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

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
#include "cathandler.h"

#include "encoders.h"
#include "iopins.h"


#define VVFOCYCLECOUNT 10                                // check every 10 ticks                                 
byte GMechEncoderDivisor;                                // number of edge events per declared click
byte GVFOCycleCount;                                     // remaining ticks until we test the VFO encoder 

byte EncoderPins[] =
{
  VPINENCODER1A, VPINENCODER1B, 
  VPINENCODER2A, VPINENCODER2B, 
  VPINENCODER3A, VPINENCODER3B, 
  VPINENCODER4A, VPINENCODER4B, 
  VPINENCODER5A, VPINENCODER5B, 
  VPINENCODER6A, VPINENCODER6B, 
  VPINENCODER7A, VPINENCODER7B, 
  VPINENCODER8A, VPINENCODER8B 
};


//
// scan coide lookup to match p2app behaviour with G2V1 panel
// these give "Andromeda like" control assignments
// these arethe reported numbers in the ZZZE message
//
byte EncoderScanCode[] =
{
  11, 12, 1, 2, 5, 6, 9, 10
};

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
// initialise - set up pins & construct data
// these are constructed now because otherwise the configdata settings wouldn't be available yet.
// read initial inputs first, to be able to pass the data to the constructor
//
void InitEncoders(void)
{
  byte EncoderIn;                               // 2 bits BA
  byte EventData = 0;                           // o/p data
  byte Encoder;


  GMechEncoderDivisor = 2;
  GVFOCycleCount = VVFOCYCLECOUNT;              // tick count
  
  for(Encoder=0; Encoder < VMAXENCODERS; Encoder++)
  {
    EncoderIn = 0;
    if(digitalRead(EncoderPins[2*Encoder]) == HIGH)
      EncoderIn |= 2;
    if(digitalRead(EncoderPins[2*Encoder+1]) == HIGH)
      EncoderIn |= 1;
    EncoderList[Encoder].Ptr = new NoClickEncoder2(GMechEncoderDivisor, EncoderIn, true);
  }
  InitOpticalEncoder();
}



//
// encoder 2ms tick
// these are all now serviced at this rate, with a total time used of around 35 microseconds
// 
void EncoderTick(void)
{
  byte EncoderIn;                               // 2 bits BA
  byte EventData = 0;                           // o/p data
  byte Encoder;
  int Movement;
  
  for(Encoder=0; Encoder < VMAXENCODERS; Encoder++)
  {
    EncoderIn = 0;
    if(digitalRead(EncoderPins[2*Encoder]) == HIGH)
      EncoderIn |= 2;
    if(digitalRead(EncoderPins[2*Encoder+1]) == HIGH)
      EncoderIn |= 1;
    EncoderList[Encoder].Ptr->service(EncoderIn);
    
    Movement = EncoderList[Encoder].Ptr->getValue();
    if (Movement != 0) 
    {
      EncoderList[Encoder].LastPosition += Movement;
      CATHandleEncoder(EncoderScanCode[Encoder], Movement);
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
      CATHandleVFOEncoder(ct);
  }
}



