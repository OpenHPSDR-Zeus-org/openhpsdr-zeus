/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// LED.h
// this file holds the code to control 8 LED indicators
/////////////////////////////////////////////////////////////////////////

#ifndef __LED_H
#define __LED_H
#include <Arduino.h>

//
// set an LED to a particular state
// LED number 0 to (N-1)
//
void SetLED(unsigned int LEDNumber, bool State);


//
// clear all LEDs
//
void ClearLEDs(void);


#endif //#ifndef
