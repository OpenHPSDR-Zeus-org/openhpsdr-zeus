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

#define VLEDBANDSHIFT 9                   // (external number = 10)
#define VLEDENCODERSHIFT 10                // (external number = 11)

// declare extern variables
extern byte I2CLEDBits;                  // 3 bits data for LEDs, in bits 2:0
extern bool LEDTestComplete;             // true if tests complete


//
// set an LED to a particular state
// LED number 0 to (N-1)
//
void SetLED(byte LEDNumber, bool State);


//
// clear all LEDs
//
void ClearLEDs(void);


//
// LEDSelfTest
// called after power up to test all LEDs; 
// cycled through and lights each in turn until finished.
//
void LEDSelfTest(void);


#endif //#ifndef
