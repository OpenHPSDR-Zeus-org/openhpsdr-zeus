/////////////////////////////////////////////////////////////////////////
//
// opticalencoder.cpp
// this is an interrupt driven optical encoder driver written for
// the new Arduino Nano Every processor ATMEGA4809. 
// copyright (c) Laurence Barker G8NJJ 2019
//
// See "iom4809.h" for register definitions, bit maps etc
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
byte GPinState;
byte GDivisor;                              // number of edge events per declared click


//#define VENCODERPINS 0b00110000             // bitmap to select the two encoder inputs when on D0, D1
#define VENCODERPINS 0b00001100             // bitmap to select the two encoder inputs when on A4, A5
#define VENCODERDIRPIN 0b00001000           // pin3 gives direction


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
// initialise optical encoder.
// attach interrupt handler; set input pin modes; read initial state
//
void InitOpticalEncoder(void)
{
  pinMode(VPINVFOENCODERA, INPUT_PULLUP);               // VFO encoder
  pinMode(VPINVFOENCODERB, INPUT_PULLUP);               // VFO encoder
  delayMicroseconds(1000);                              // allow pins to settle
//  GPinState = (PORTC.IN & VENCODERPINS) >> 2;         // move to bits 3:2
  GPinState = (PORTA.IN & VENCODERPINS);                // bits 3:2
//
// now do interrupts differently depending on encoder type
// for high res encoders, get an interrupt on one input rising edge
// for low res (Broadcom type) encoders, get an interrupt on every edge
//  PORTC.PIN4CTRL = (PORT_PULLUPEN_bm | PORT_ISC_BOTHEDGES_gc);            // pullup, both edges interrupt (D1, D0)
//  PORTC.PIN5CTRL = (PORT_PULLUPEN_bm | PORT_ISC_BOTHEDGES_gc);            // pullup, both edges interrupt (D1, D0)
  PORTA.PIN2CTRL = (PORT_PULLUPEN_bm | PORT_ISC_BOTHEDGES_gc);            // pullup, both edges interrupt (A4, A5)
  PORTA.PIN3CTRL = (PORT_PULLUPEN_bm | PORT_ISC_BOTHEDGES_gc);            // pullup, both edges interrupt (A4, A5)
}



//
// VFO encoder pin interrupt handler
// this works one of two ways depending on the attached encoder
// for a broadcom type encoder - use both edges; find 4 bits from 2 bits current state and 2 bits previous state, and look up
// for a high res encoder at just one interrupt per pulse - use int on one edge and use the sense of the other to set direction.
//
ISR(PORTA_PORT_vect)
{
  byte InputValue;
  signed char Increment;

//  InputValue = (PORTC.IN & VENCODERPINS) >> 2;          // move to bits 3:2
  InputValue = (PORTA.IN & VENCODERPINS);                 // bits 3:2
//  PORTC.INTFLAGS = 0b00110000;                            // clear interrupt flags
  PORTA.INTFLAGS = 0b00001100;                            // clear interrupt flags
  GPinState = (GPinState >> 2) | InputValue;              // now have new bits in 3:2, old bits in 1:0
  Increment = StepsLookup[GPinState];


#ifdef VSWAPDIRECTION
  GDeltaCount -= Increment;
#else
  GDeltaCount += Increment;
#endif


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
