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


// declare extern variables
extern byte I2CLEDBits;                  // 3 bits data for LEDs, in bits 2:0
extern byte GDisplayBrightness;          // brightness value (0 to 255)


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
// PWM initialise: load setting from EEPROM to output pin
//
void PWMInitialise(void);


//
// PWMTick: see if we need to write new value to EEPROM
//
void PWMTick(void);


//
// PWM brightness update
// change current PWM brightness by the number of steps passed in
//
void PWMUpdate(signed char Steps);


#endif //#ifndef
