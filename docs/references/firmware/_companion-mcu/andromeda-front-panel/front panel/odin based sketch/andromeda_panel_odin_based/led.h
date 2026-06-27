/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// LED.h
// this file holds the code to control 8 LED indicators
/////////////////////////////////////////////////////////////////////////

#ifndef __LED_H
#define __LED_H
#include <Arduino.h>

//
// set an LED to a particular state
// LED number 0-7
//
void SetLED(unsigned int LEDNumber, bool State);


//
// clear all LEDs
//
void ClearLEDs(void);


#endif //#ifndef
