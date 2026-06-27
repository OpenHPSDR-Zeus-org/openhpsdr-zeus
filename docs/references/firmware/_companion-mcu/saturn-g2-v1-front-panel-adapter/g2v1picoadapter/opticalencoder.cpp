/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// the encoder is attached to A4 and A5 (PA2, PA3)
// and PORTA pin change will need to be enabled. 
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"
#include "opticalencoder.h"
#include "iopins.h"


//#define VSWAPDIRECTION 1                    // if set, reverses direction


// global variables

signed char GDeltaCount;                    // count stored since last retrieved
byte GDivisor;                              // number of edge events per declared click




//
// lookup table to get number of steps from 4 bits:
// bit 1,0 = old position
// bits 3:2 = new position
//
signed char StepsLookup[] = 
{
  0, 1, -1, 2,
  -1, 0, -2, 1,
  1, -2, 0, -1,
  2, -1, 1, 0
};


//
// set divisor
// this sets whether events are generated every 1, 2 or 4 edge events
// legal parameters are 1, 2 or 4 and this MUST be called!
//
void SetOpticalEncoderDivisor(byte EncoderDivisor)
{
  GDivisor = EncoderDivisor;
}



//
// VFO encoder pin interrupt handler
// for a high res encoder at just one interrupt per pulse - use int on one edge and use the sense of the other to set direction.
//
void EncoderISR()
{
  signed char Increment;

  if(digitalRead(VPINVFOENCODERB) == HIGH)
    Increment = 1;
  else
    Increment = -1;


#ifdef VSWAPDIRECTION
  GDeltaCount -= Increment;
#else
  GDeltaCount += Increment;
#endif
}



//
// initialise optical encoder.
// attach interrupt handler; set input pin modes
// Input A does interrupt; state of B sets direction.
//
void InitOpticalEncoder(void)
{
  pinMode(VPINVFOENCODERA, INPUT_PULLUP);               // VFO encoder
  pinMode(VPINVFOENCODERB, INPUT_PULLUP);               // VFO encoder
  SetOpticalEncoderDivisor(1);
  attachInterrupt(digitalPinToInterrupt(VPINVFOENCODERA), EncoderISR, RISING);
}





//
// read the optical encoder. Return the number of steps turned since last called.
// read back the count since last asked, then zero it for the next time
// if Divisor is above 1: leave behind the residue
//
signed char ReadOpticalEncoder(void)
{
  signed char Result;

  Result = GDeltaCount / GDivisor;                         // get count value
  GDeltaCount = GDeltaCount % GDivisor;                    // remaining residue for next time
  return Result;
}
