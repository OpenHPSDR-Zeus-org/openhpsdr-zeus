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
#include "types.h"
#include "configdata.h"
#include "button.h"

byte I2CLEDBits;                  // 3 bits data for LEDs, in bits 2:0 (init to zero by button.cpp)
#define VLEDBITMASK 0b00000111    // bit mask for LED bits that are allowed to be set
#define VBRIGHTNESSWRITEBACKCOUNT 2500          // 5 seconds (2500 x 2ms ticks)

//
// PWM output for PC LCD brightness control
//
byte GDisplayBrightness;                                // brightness value (0 to 255)
int GBrightCounter;                                     // timer for EEPROM writeback
//int GCurrentBrightness;                                 // current brightness during initialisation


//
// enum for the states of the PWM startup sequencer
// start on constant 1; then ramp brightness down to required brightness.
//
enum EPWMInitStates
{
  eBegin,                         // PWM at maximum
  eRamp,                          // ramping down
  eNormal                         // brightness settled
};

EPWMInitStates GPWMState;         // PWM init sequencer
unsigned int GPWMTimer;           // time count (2ms period)
#define VFULLBRIGHTNESSCOUNT 500  // ticks at full brightness
#define VRAMPTICKS 20             // 40ms per ramp step

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
// NOT THE RIGHT VALUES YET!
LEDType LEDPinList[] = 
{
  {VPININDICATOR1, false},
  {VPININDICATOR2, false},
  {VPININDICATOR3, false},
  {VPININDICATOR4, false},
  {VPININDICATOR5, false},
  {VPININDICATOR6, false},
  {VPININDICATOR7, false},
  {VPININDICATOR8, false},
  {0, true},
  {1, true},
  {2, true}
};



//
// note LEDs numbered 0-(N-1) here!
//
void SetLED(byte LEDNumber, bool State)
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
    SetLED(Cntr, false);
}




//
// PWMTick: see if we need to write new value to EEPROM
// and advance the initial state sequence
//
void PWMTick(void)
{
//
// write back check
//
  if (GBrightCounter!= 0)                 // only do anything if counting down
    if(--GBrightCounter == 0)
      EEWriteBrightness(GDisplayBrightness);

//
// now see if the initial brightness sequencer needs to be updated
//
//  switch(GPWMState)
//  {
//    case eBegin:                          // PWM at maximum
//      if (--GPWMTimer == 0)
//      {
//        GPWMTimer = 1;                                          // decrement brightness next tick
//        GPWMState = eRamp;                                      // go to ramp state
//      }
//      break;
      
//    case eRamp:                           // ramping down
//      if (--GPWMTimer == 0)
//      {
//        GCurrentBrightness -= 4;                                // and set next brightness
//        if (GCurrentBrightness <= GDisplayBrightness)           // if we have reached the required setting
//        {
//          analogWrite(VPINDISPLAYPWM, GDisplayBrightness);      // set final setting
//          GPWMState = eNormal;                                  // go to end state
//        }
//        else
//        {
//          GPWMTimer = VRAMPTICKS;                               // ramp time per tick
//          analogWrite(VPINDISPLAYPWM, GCurrentBrightness);
//        }
//      }
//      break;
      
//    case eNormal:                                               // brightness settled - nothing to do
//      break;
//  }
}


//
// PWM initialise: load setting from EEPROM to output pin
// begin on maximum brightness
//
void PWMInitialise(void)
{
//  GCurrentBrightness = 255;
//  analogWrite(VPINDISPLAYPWM, GCurrentBrightness);
  analogWrite(VPINDISPLAYPWM, GDisplayBrightness);
//  GPWMTimer = VFULLBRIGHTNESSCOUNT;       // ticks at full brightness
}


//
// PWM brightness update
// change current PWM brightness by the number of steps passed in
// called if the correct encoder is turned; check the right button pressed too!
// the lower limit for brightness is to avoid the display LED driver going into EasyScale mode
//
// note PWM now driven by a FET, which inverts the PWM signal. To get max brightness now need minimum PWM output.
// note there is a limit on min brightness, to make sure the display driver doesn't enter "EasyScale" mode.
//
void PWMUpdate(signed char Steps)
{
  int Brightness;
  if(IsPWMButtonPressed())
  {
    Brightness = (int)GDisplayBrightness;
    Brightness -= 8 * Steps;                                // get new value; smaller is brighter
    Brightness = constrain(Brightness, 10, 210);
    GDisplayBrightness = Brightness;
    analogWrite(VPINDISPLAYPWM, GDisplayBrightness);
    GBrightCounter = VBRIGHTNESSWRITEBACKCOUNT;             // delay till we write it to EEPROM
  }
}
