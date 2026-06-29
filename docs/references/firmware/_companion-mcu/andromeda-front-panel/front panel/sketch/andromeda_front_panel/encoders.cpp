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
#include "mechencoder.h"

#include <Encoder.h>
#include "types.h"
#include "encoders.h"
#include "iopins.h"
#include "configdata.h"
#include "cathandler.h"
#include "tiger.h"


//
// note switch and encoder numbering:
// in the software switches are numbered 0-20, and encoders 0-7. The VFO encoder is treated separately.
// these correspond to the control of Kjell's PCB as follows:
//
// encoder numbering:
//    PCB   software
//    VFO    (treated separately)
//    2A      0
//    2B      1
//    3A      2
//    3B      3
//    4A      4
//    4B      5
//    5A      6
//    5B      7


//
// global variables
//
byte EncoderGroup;                                  // sets which ones are updated every ms

//
// 8 encoders: one VFO (fast) encoder and 7 "normal" ones 
//
Encoder VFOEncoder(VPINVFOENCODERA, VPINVFOENCODERB);

long old_ct;

struct  EncoderData                           // holds data for one slow encoder
{
  NoClickEncoder* Ptr;                          // ptr to class
  int16_t LastPosition;                       // previous position
};

  EncoderData EncoderList[VMAXENCODERS];

//
// variables to say whether encoders have main action or 2nd action, if dual function
// false if main function, true if 2nd function
//
bool Is2ndAction[VMAXENCODERS];


  
//
// initialise - set up pins & construct data
// these are constructed now because otherwise the configdata settings wouldn't be available yet.
//
void InitEncoders(void)
{
  EncoderList[0].Ptr = new NoClickEncoder(VPINENCODER1A, VPINENCODER1B, GEncoderDivisor, true);
  EncoderList[1].Ptr = new NoClickEncoder(VPINENCODER2A, VPINENCODER2B, GEncoderDivisor, true);
  EncoderList[2].Ptr = new NoClickEncoder(VPINENCODER3A, VPINENCODER3B, GEncoderDivisor, true);
  EncoderList[3].Ptr = new NoClickEncoder(VPINENCODER4A, VPINENCODER4B, GEncoderDivisor, true);
  EncoderList[4].Ptr = new NoClickEncoder(VPINENCODER5A, VPINENCODER5B, GEncoderDivisor, true);
  EncoderList[5].Ptr = new NoClickEncoder(VPINENCODER6A, VPINENCODER6B, GEncoderDivisor, true);
  EncoderList[6].Ptr = new NoClickEncoder(VPINENCODER7A, VPINENCODER7B, GEncoderDivisor, true);
  EncoderList[7].Ptr = new NoClickEncoder(VPINENCODER8A, VPINENCODER8B, GEncoderDivisor, true);

#ifdef V3HARDWARE                                     // Andromeda 2nd prototype hardware
  EncoderList[8].Ptr = new NoClickEncoder(VPINENCODER9A, VPINENCODER9B, GEncoderDivisor, true);
  EncoderList[9].Ptr = new NoClickEncoder(VPINENCODER10A, VPINENCODER10B, GEncoderDivisor, true);



#elif defined V2HARDWARE                              // Andromeda 1st prototype hardware
  EncoderList[8].Ptr = new NoClickEncoder(VPINENCODER9A, VPINENCODER9B, GEncoderDivisor, true);
  EncoderList[9].Ptr = new NoClickEncoder(VPINENCODER10A, VPINENCODER10B, GEncoderDivisor, true);
  EncoderList[10].Ptr = new NoClickEncoder(VPINENCODER11A, VPINENCODER11B, GEncoderDivisor, true);
  EncoderList[11].Ptr = new NoClickEncoder(VPINENCODER12A, VPINENCODER12B, GEncoderDivisor, true);
  EncoderList[12].Ptr = new NoClickEncoder(VPINENCODER13A, VPINENCODER13B, GEncoderDivisor, true);
  EncoderList[13].Ptr = new NoClickEncoder(VPINENCODER14A, VPINENCODER14B, GEncoderDivisor, true);
#endif
}




//
// encoder 1ms tick
// these are all now serviced at this rate, with a total time used of around 35 microseconds
// 
void EncoderFastTick(void)
{
  EncoderList[0].Ptr->service();
  EncoderList[1].Ptr->service();
  EncoderList[2].Ptr->service();
  EncoderList[3].Ptr->service();
  EncoderList[4].Ptr->service();
  EncoderList[5].Ptr->service();
  EncoderList[6].Ptr->service();
  EncoderList[7].Ptr->service();
#ifdef V3HARDWARE
  EncoderList[8].Ptr->service();
  EncoderList[9].Ptr->service();
#elif defined V2HARDWARE
  EncoderList[8].Ptr->service();
  EncoderList[9].Ptr->service();
  EncoderList[10].Ptr->service();
  EncoderList[11].Ptr->service();
  EncoderList[12].Ptr->service();
  EncoderList[13].Ptr->service();
#endif
}


//
// encoder 10ms tick
// detect and handle encoders that have moved
// if SENSORDEBUG, just send a display string
// 
void EncoderSlowTick(void)
{
  int16_t Movement;                                         // normal encoder movement since last update
  byte Cntr;                                                // count encoders
  
  for (Cntr=0; Cntr < VMAXENCODERS; Cntr++)
  {
    Movement = EncoderList[Cntr].Ptr->getValue();
    if (Movement != 0) 
    {
      EncoderList[Cntr].LastPosition += Movement;
      CATHandleEncoder(Cntr, Movement);
    }
  }

//
//read the VFO encoder; divide by N to get the desired step count
//
long ct = (VFOEncoder.read())/GVFOEncoderDivisor;
  if (ct != old_ct)
  {
    CATHandleVFOEncoder(ct-old_ct);
    old_ct=ct;
  }
}
