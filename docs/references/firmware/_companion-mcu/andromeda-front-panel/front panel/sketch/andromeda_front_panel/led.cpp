/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// LED.c
// this file holds the code to control 8 LED indicators
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"
#include "mechencoder.h"
#include "led.h"
#include "iopins.h"
#include "types.h"



//
// array of I/O pins
//
#ifdef V3HARDWARE               // andromeda 2nd prototype
int LEDPinList[] = 
{
  VPININDICATOR1,
  VPININDICATOR2,
  VPININDICATOR3,
  VPININDICATOR4,
  VPININDICATOR5,
  VPININDICATOR6,
  VPININDICATOR7,
  VPININDICATOR8,
  VPININDICATOR9,
  VPININDICATOR10,
  VPININDICATOR11,
  VPININDICATOR12
};


#elif defined V2HARDWARE        // andromeda 1st prototype
int LEDPinList[] = 
{
  VPININDICATOR1,
  VPININDICATOR2,
  VPININDICATOR3,
  VPININDICATOR4,
  VPININDICATOR5,
  VPININDICATOR6,
  VPININDICATOR7,
  VPININDICATOR8,
  VPININDICATOR9
};
#endif



//
// note LEDs numbered 0-(N-1) here!
//
void SetLED(unsigned int LEDNumber, bool State)
{
  if (LEDNumber < VMAXINDICATORS)
  {
    if (State == true)
      digitalWrite(LEDPinList[LEDNumber], HIGH);
    else
      digitalWrite(LEDPinList[LEDNumber], LOW);
  }
}


//
// clear all LEDs
//
void ClearLEDs(void)
{
  int Cntr;

  for (Cntr = 0; Cntr < VMAXINDICATORS; Cntr++)
    SetLED(Cntr, false);
}
