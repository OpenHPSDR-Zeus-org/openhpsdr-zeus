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
#include "led.h"
#include "iopins.h"

byte I2CLEDBits;                  // 3 bits data for LEDs, in bits 2:0 (init to zero by button.cpp)
bool LEDTestComplete;             // true if tests complete
byte TestLED;                     // LED number to test
byte LEDLitTime;                  // time count (in ticks) each LED lit for
unsigned int GLEDTestWord;        // LED bits during test
unsigned int GLEDExtWord;         // LED bits from external messages


#define VLEDBITMASK 0b11110000    // bit mask for LED bits that are allowed to be set

//
// struct to hold an LED definition
// holds a pin number, or indicates which I2C bit
//
struct LEDType
{
  byte PinNumber;                 // I/O pin number or bit number
  bool IsI2C;                     // true if I2C connected
};

//
// array of I/O pins
//
LEDType LEDPinList[] = 
{
  {7, true},
  {6, true},
  {5, true},
  {4, true},
  {VPININDICATOR5, false},
  {VPININDICATOR6, false},
  {VPININDICATOR7, false},
  {VPININDICATOR8, false},
  {VPININDICATOR9, false},
  {VPININDICATOR10, false},
  {VPININDICATOR11, false}
};




//
// set LED from external message
// LED state written to a word,that is then written to LEDs periodically
// note LEDs numbered 0-(N-1) here!
//
void SetLED(byte LEDNumber, bool State)
{
  unsigned int BitPosition;

  BitPosition = 1 << LEDNumber;
  if(State)
    GLEDExtWord |= BitPosition;
  else
    GLEDExtWord &= ~BitPosition;              // clear the required bit
}

//
// note LEDs numbered 0-(N-1) here!
// write an individual LED off or on
void WriteLED(byte LEDNumber, bool State)
{
  byte IOPin;
  byte BitPosition;
  
  if (LEDNumber < VMAXINDICATORS)
  {
    if(LEDPinList[LEDNumber].IsI2C== false)                 // if it is a GPIO pin
    {
      IOPin = LEDPinList[LEDNumber].PinNumber;
      if (State == true)
        digitalWrite(IOPin, HIGH);
      else
        digitalWrite(IOPin, LOW);
    }
    else                                                    // if it is connected to I2C
    {
      BitPosition = 1 << LEDPinList[LEDNumber].PinNumber;
      if (State == true)
        I2CLEDBits |= BitPosition;                          // set LED bit
      else
        I2CLEDBits &= ~BitPosition;                         // cancel LED bit

      I2CLEDBits &= VLEDBITMASK;                            // double check no others set
    }
  }
}


//
// clear all LEDs
//
void ClearLEDs(void)
{
  byte Cntr;

  for (Cntr = 0; Cntr < VMAXINDICATORS; Cntr++)
    WriteLED(Cntr, false);
}


byte LEDTestOrder[] = {0, 1, 2, 4, 3, 8, 7, 6, 5, 9, 10};
bool GLEDsExtinguishing;
#define VTESTTIMEPERLED 50       // 100ms

//
// LEDTick
// called after power up to test all LEDs; 
// cycled through and lights each in turn until finished.
// then write to LEDs as needed
//
void LEDTick(void)
{
  unsigned int Mask;
  if(!LEDTestComplete)
  {
    if(LEDLitTime == 0)                 // if timed out - move on to next LED
    {
      if((TestLED == VMAXINDICATORS) && (GLEDsExtinguishing==false))     // if we have lit the last one
      {
        LEDLitTime = VTESTTIMEPERLED;
        GLEDsExtinguishing = true;
        TestLED=0;
      }
      if((TestLED == VMAXINDICATORS) && (GLEDsExtinguishing))           // if we have turned off the last one
      {
        LEDTestComplete = true;
        ClearLEDs();
      }
      else                              // increment LED & re-start count
      {
        LEDLitTime = VTESTTIMEPERLED;
        Mask = 1 << LEDTestOrder[TestLED];
        if(GLEDsExtinguishing == true)
          GLEDTestWord &= ~ Mask;
        else
          GLEDTestWord |= Mask;
        TestLED++;                      // and light new one
      }
    }
    else
      LEDLitTime--;
  }
//
// now write all LEDs
//
  byte Cntr;
  unsigned int LEDWord;

  if(LEDTestComplete)                                       // get the word to shift
    LEDWord = GLEDExtWord;
  else
    LEDWord = GLEDTestWord;

  for(Cntr=0; Cntr < VMAXINDICATORS; Cntr++)
  {
    WriteLED(Cntr, (bool)(LEDWord & 1));
    LEDWord = LEDWord >> 1;
  }
}
