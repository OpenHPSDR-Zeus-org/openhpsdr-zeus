/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// the code is written for an Arduino Nano Every module
//
// "main" file with setup() and loop()
/////////////////////////////////////////////////////////////////////////
//
#include <Arduino.h>
#include <Wire.h>
#include "globalinclude.h"
#include "types.h"
#include "configdata.h"
#include "iopins.h"
#include "button.h"
#include "encoders.h"
#include "tiger.h"
#include "led.h"
#include "cathandler.h"


//
// global variables
//
bool GTickTriggered;                  // true if a 2ms tick has been triggered


//
// counter clocked by CK/8 (0.5us)
// note this is faster than I've used in other sketches because timer 8 set to run 8x faster
void SetupTimerForInterrupt(int Milliseconds)
{
  int Count;

  Count = Milliseconds * 2000;
  TCB0.CTRLB = TCB_CNTMODE_INT_gc; // Use timer compare mode  
  TCB0.CCMP = Count; // Value to compare with. This is 1/5th of the tick rate, so 5 Hz
  TCB0.INTCTRL = TCB_CAPT_bm; // Enable the interrupt
  TCB0.CTRLA = TCB_CLKSEL_CLKTCA_gc | TCB_ENABLE_bm; // Use Timer A as clock, enable timer

  // setup timer A for 8x faster than normal clock, so we get 8KHz PRF
  // this will cause ny use of delay() millis() etc to be wrong
  TCA0.SINGLE.CTRLA = (TCA_SINGLE_CLKSEL_DIV8_gc) | (TCA_SINGLE_ENABLE_bm);
}


void setup() 
{
  // put your setup code here, to run once:
  Serial.begin(9600);                 // PC communication
  Wire.begin();                       // I2C
  Wire.setClock(400000);

  delay(1000);
//
// configure I/O pins
//
  ConfigIOPins();
  GButtonInitialise();
//
// check that the flash is programmed, then load to RAM
//  
  LoadSettingsFromEEprom();

//
// initialise timer to give 2ms tick interrupt
//
  SetupTimerForInterrupt(2);
//
// encoder
//
  InitEncoders();
//
// CAT
//
  InitCAT();
//
// display brightness
//
  PWMInitialise();
}



//
// 2ms tick handler.
//
ISR(TCB0_INT_vect)
{
//  digitalWrite(12, HIGH);                 // debug to measure tick period
  GTickTriggered = true;
   // Clear interrupt flag
  TCB0.INTFLAGS = TCB_CAPT_bm;
//  digitalWrite(12, LOW);                  // debug to measure tick period
}



// for heartbeat LED:
bool ledOn = false;
byte Counter = 0;



//
// 2 ms event loop
// this is triggered by GTickTriggered being set by a timer interrupt
// the loop simply waits until released by the timer handler
void loop()
{
  while (GTickTriggered)
  {
    GTickTriggered = false;
    ButtonTick();
// heartbeat LED
    if (Counter == 0)
    {
      Counter=249;
      ledOn = !ledOn;
      if (ledOn)
        digitalWrite(LED_BUILTIN, HIGH); // Led on, off, on, off...
       else
        digitalWrite(LED_BUILTIN, LOW);
    }
    else
      Counter--;

    EncoderTick();
    ButtonTick();                                 // update the pushbutton sequencer
//
// look for any CAT commands in the serial input buffer and process them
//    
    ScanParseSerial();
//
// check if we need to write brightness value back to EEPROM
//
    PWMTick();                                    // display brightness checks
    
// 
// last action - drive the new switch matrix column output
//
    AssertMatrixColumn();
  }
}


void ConfigIOPins(void)
{
  pinMode(LED_BUILTIN, OUTPUT);

  pinMode(VPININDICATOR1, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR2, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR3, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR4, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR5, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR6, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR7, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR8, OUTPUT);                      // LED indicator
  pinMode(VPINDISPLAYPWM, OUTPUT);                      // display brightness
  digitalWrite(VPININDICATOR1, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR2, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR3, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR4, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR5, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR6, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR7, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR8, LOW);                    // LED indicator
  digitalWrite(VPINDISPLAYPWM, LOW);                    // display brightness

  pinMode(VPINENCODER9A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER9B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER10A, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER10B, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER11A, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER11B, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER12A, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER12B, INPUT_PULLUP);                // normal encoder
}
