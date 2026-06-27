/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
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
extern EEncoderActions GMultiAction;                       // assigned action for "multifunction"

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


//
// handler for a button event where button is set to be the encoder control button
// (for dual function encoders)
//
void EncoderHandleButton(unsigned int ButtonNum, bool IsPress);


//
// get a bool to say if 2nd action selected or not
//
bool GetEncoderMain2ndAction(unsigned int Encoder);



#endif // not defined
