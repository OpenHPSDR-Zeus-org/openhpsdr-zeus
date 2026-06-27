/////////////////////////////////////////////////////////////////////////
//
// opticalencoder.cpp
// this is an interrupt driven optical encoder driver written for
// the new Arduino Nano Every processor
// copyright (c) Laurence Barker G8NJJ 2019
//
// the encoder is attached to D0 and D1 (PC04, PC05)
// and PORTC pin change will need to be enabled. 
/////////////////////////////////////////////////////////////////////////

#ifndef __OPTICALENCODER_H
#define __OPTICALENCODER_H

//
// initialise optical encoder.
// attach interrupt handler
//
void InitOpticalEncoder(void);

//
// read the optical encoder. Return the number of steps turned since last called.
//
signed char ReadOpticalEncoder(void);

//
// set divisor
// this sets whether events are generated every 1, 2 or 4 edge events
// legal parameters are 1, 2 or 4 and this MUST be called!
//
void SetOpticalEncoderDivisor(byte EncoderDivisor);





#endif
