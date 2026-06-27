/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// encoders.h
// this file holds the code to manage the rotary encoders
// it looks like it needs two technologies:
// interrupt driven code for optical VFO encoder (apparenrtly largely bounce free)
// polled code for very bouncy mechanicsl encoders for other controls
/////////////////////////////////////////////////////////////////////////

#ifndef __ENCODERS_H
#define __ENCODERS_H
#include <Arduino.h>
#include "iopins.h"

//
// initialise - set up pins & construct data
//
void InitEncoders(void);

//
// encoder 2ms tick
// 
void EncoderTick(void);

//
// set divisors
// this sets whether events are generated every 1, 2 or 4 edge events
// legal parameters are 1, 2 or 4 and this MUST be called!
//
void SetEncoderDivisors(byte EncoderDivisor, byte VFOEncoderDivisor);


#endif // not defined
