/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// "main" file with setup() and loop()
/////////////////////////////////////////////////////////////////////////
//
#include <Arduino.h>
#include <Wire.h>
#include "globalinclude.h"
#include "iopins.h"
#include "configdata.h"
#include "encoders.h"
#include "SPIdata.h"
#include "button.h"
#include "eventqueue.h"
#include "led.h"


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



//
// initialisation of processor and peripherals
//
void setup() 
{
  Serial.begin(9600);                 // PC communication
  Wire.setClock(400000);

  delay(1000);
//
// configure I/O pins
//
  ConfigIOPins();
  InitSPI();
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
  InitialiseI2CSlave();
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
byte Counter = 0;                           // tick counter for LED on period or off period


//
// 2 ms event loop
// this is triggered by GTickTriggered being set by a timer interrupt
// the loop simply waits until released by the timer handler
//
void loop()
{
  while (GTickTriggered)
  {
    GTickTriggered = false;
// heartbeat LED
    if (Counter == 0)
    {
      Counter=249;
      ledOn = !ledOn;
      if (ledOn)
        digitalWrite(VPINBLINKLED, HIGH); // Led on, off, on, off...
       else
        digitalWrite(VPINBLINKLED, LOW);
    }
    else
      Counter--;

//
// 2ms tick code here:
//
  EventQueueTick();                             // update LEDs
  EncoderTick();                                // update encoder inputs
  ButtonTick();                                 // update the pushbutton sequencer
  LEDSelfTest();                                // selftest of LEDs at startup
// 
// last action - drive the new switch matrix column output
//
  AssertMatrixColumn();
  }
}




//
// initialise states of Arduino IO pins
//
void ConfigIOPins(void)
{

  pinMode(VPININDICATOR5, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR6, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR7, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR8, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR9, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR10, OUTPUT);                     // LED indicator
  pinMode(VPININDICATOR11, OUTPUT);                     // LED indicator
  
  pinMode(VPINMCPCS0, OUTPUT);                          // chip select output
  pinMode(VPINMCPCS1, OUTPUT);                          // chip select output
  pinMode(VPINBLINKLED, OUTPUT);
  
  pinMode(VPINENCODER9A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER9B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER10A, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER10B, INPUT_PULLUP);                // normal encoder

  digitalWrite(VPININDICATOR5, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR6, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR7, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR8, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR9, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR10, LOW);                   // LED indicator
  digitalWrite(VPININDICATOR11, LOW);                   // LED indicator
  
  digitalWrite(VPINMCPCS0, HIGH);                       // chip select output
  digitalWrite(VPINMCPCS1, HIGH);                       // chip select output
  digitalWrite(VPINBLINKLED, LOW);                      // debug LED output

//
// finally setup interrupt output: active low output
//
  digitalWrite(VPINPIINTERRUPT, HIGH);                  // interrupt output
  pinMode(VPINPIINTERRUPT, OUTPUT);                     // interrupt output

}

