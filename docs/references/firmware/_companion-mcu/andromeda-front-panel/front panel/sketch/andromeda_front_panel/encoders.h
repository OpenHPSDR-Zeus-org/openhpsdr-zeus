/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
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
#include "types.h"
#include "iopins.h"

//
// initialise - set up pins & construct data
//
void InitEncoders(void);

//
// encoder 1ms tick
// 
void EncoderFastTick(void);

//
// encoder 10ms tick
// 
void EncoderSlowTick(void);



#endif // not defined
