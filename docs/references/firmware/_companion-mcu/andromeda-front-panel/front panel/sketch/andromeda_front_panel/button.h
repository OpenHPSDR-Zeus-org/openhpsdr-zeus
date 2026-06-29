/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// button.c
// this file holds the code to debounce pushbutton inputs
//
/////////////////////////////////////////////////////////////////////////

#ifndef __BUTTON_H
#define __BUTTON_H
#include <Arduino.h>
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


#endif //not defined
