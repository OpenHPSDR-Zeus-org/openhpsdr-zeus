/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module
//
// "main" file with setup() and loop()
/////////////////////////////////////////////////////////////////////////
//
#include <Arduino.h>
#include <Wire.h>
#include "RPi_Pico_TimerInterrupt.h"
#include "globalinclude.h"
#include "iopins.h"
#include "cathandler.h"
#include "encoders.h"
#include "button.h"


//
// global variables
//
bool GTickTriggered;                  // true if a 2ms tick has been triggered
RPI_PICO_Timer ITimer0(0);            // Init RPI_PICO_Timer, can use any from 0-15 pseudo-hardware timers
bool ledOn = false;                   // for heartbeat LED
byte Counter = 0;                     // tick counter for LED on period or off period



//
// tick interrupt handler
// just set the bool "tick happened" flag
//
bool TimerHandler0(struct repeating_timer *t)
{ 
  GTickTriggered = true;
  return true;
}
#define TIMER0_INTERVAL_MS        2




//
// initialisation of processor and peripherals
//
void setup() 
{
  CATSERIAL.begin(115200);                 // PC communication
  delay(1000);
//
// configure I/O pins
//
  ConfigIOPins();
  GButtonInitialise();

//
// initialise timer to give 2ms tick interrupt
//
  // setup tick timer: period is in microsecs
  ITimer0.attachInterruptInterval(TIMER0_INTERVAL_MS * 1000, TimerHandler0);
//
// encoder
//
  InitEncoders();
  //
// CAT
//
  InitCAT();
}





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
    EncoderTick();                                // update encoder inputs
    ButtonTick();                                 // update the pushbutton sequencer
  //
// look for any CAT commands in the serial input buffer and process them
//    
    ScanParseSerial();
  }
}




//
// initialise states of Arduino IO pins
//
void ConfigIOPins(void)
{

  pinMode(VPINBLINKLED, OUTPUT);
  
  pinMode(VPINENCODER1A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER1B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER2A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER2B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER3A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER3B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER4A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER4B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER5A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER5B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER6A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER6B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER7A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER7B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER8A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER8B, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER1PB, INPUT_PULLUP);                // encoder PB
  pinMode(VPINENCODER2PB, INPUT_PULLUP);                // encoder PB
  pinMode(VPINENCODER3PB, INPUT_PULLUP);                // encoder PB
  pinMode(VPINENCODER4PB, INPUT_PULLUP);                // encoder PB

  digitalWrite(VPINBLINKLED, LOW);                      // debug LED output


}

