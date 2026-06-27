/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// button.h
// this file holds the code to debounce pushbutton inputs
//
/////////////////////////////////////////////////////////////////////////

#ifndef __BUTTON_H
#define __BUTTON_H
#include <Arduino.h>

//
// accessible variables
//
extern bool GShiftOverride;                            // true if shift buttons are to be treated as normal buttons
extern bool GEncoderShiftActive;                       // true if encoder shift is active



//
// initialise
// simply set all the debounce inputs to 0xFF (button released)
//
void GButtonInitialise(void);


//
// Tick
// read each pin intp the LSB of its debounce register; then look for press or release pattern
//
void ButtonTick(void);


//
// function to drive new column output
// this should be at the END of the code to allow settling time
//
void AssertMatrixColumn(void);


#endif //not defined
