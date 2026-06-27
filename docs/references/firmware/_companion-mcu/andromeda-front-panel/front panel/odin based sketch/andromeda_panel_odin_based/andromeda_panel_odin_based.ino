/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel sketch based on Odin Code by Laurence Barker G8NJJ
// this is an interim front panel, using full CAT commands
//
// this sketch provides a knob and switch interface through USB and CAT
// the code is written for an Arduino Due module
//
// "main" file with setup() and loop()
/////////////////////////////////////////////////////////////////////////
//
#include <efc.h>
#include <flash_efc.h>
#include <Arduino.h>
#include "globalinclude.h"
#include "types.h"
#include "configdata.h"
#include "iopins.h"
#include "button.h"
#include "encoders.h"
#include "tiger.h"
#include "cathandler.h"
#include "display.h"
#include <DueTimer.h>

//
// global variables
//
#define VNUMSUBTICKS 10               // number of 1ms ticks per "master" tick
boolean GTickTriggered;               // true if a 16ms tick has been triggered
byte GTickCount;                      // 1ms sub-tick counter


void setup() 
{
  // put your setup code here, to run once:
  Serial.begin(9600);
  delay(1000);
//
// configure I/O pins
//
  ConfigIOPins();
  GButtonInitialise();
//
// check that the flash is programmed, then load to RAM
//  
  LoadSettingsFromFlash();

//
// initialise timer to give 1ms tick interrupt
//
  Timer3.attachInterrupt(timerIsr);
  Timer3.start(1000); // Calls every 1ms
//
// encoder
//
  InitEncoders();
//
// CAT
//
  InitCAT();
  InitCATHandler();
//
// display
//
  DisplayInit();
}



//
// 1ms sub-tick handler.
// updates the encoder, ands counts down to a "main" 10ms tick
void timerIsr()
{
  if (GTickCount == 0)
  {
    GTickTriggered = true;                // initiate master tick event
    GTickCount = VNUMSUBTICKS - 1;        // restart count
  }
  else
    GTickCount--;

  EncoderFastTick();
}


// for heartbeat LED:
bool ledOn = false;
int Counter = 0;



//
// 10 ms event loop
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
      Counter=49;
      ledOn = !ledOn;
      if (ledOn)
        digitalWrite(LED_BUILTIN, HIGH); // Led on, off, on, off...
       else
        digitalWrite(LED_BUILTIN, LOW);

    }
    else
      Counter--;

    EncoderSlowTick();
//
// look for any CAT commands in the serial input buffer and process them
//    
    ScanParseSerial();
    CATHandlerTick();
//
// update the display
//
    DisplayTick();
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
  digitalWrite(VPININDICATOR1, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR2, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR3, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR4, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR5, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR6, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR7, LOW);                    // LED indicator
#ifdef V3HARDWARE                                                       // Andromeda 2nd prototype
  pinMode(VPININDICATOR8, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR9, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR10, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR11, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR12, OUTPUT);                      // LED indicator
  digitalWrite(VPININDICATOR8, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR9, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR10, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR11, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR12, LOW);                    // LED indicator
#elif defined V2HARDWARE                                                // Andromeda 1st prototype
  pinMode(VPININDICATOR8, OUTPUT);                      // LED indicator
  pinMode(VPININDICATOR9, OUTPUT);                      // LED indicator
  digitalWrite(VPININDICATOR8, LOW);                    // LED indicator
  digitalWrite(VPININDICATOR9, LOW);                    // LED indicator
#endif



//  pinMode(VPINVFOENCODERA, INPUT_PULLUP);               // VFO encoder
//  pinMode(VPINVFOENCODERB, INPUT_PULLUP);               // VFO encoder


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

#ifdef V3HARDWARE                           // Andromeda 2nd prototype h/w
  pinMode(VPINENCODER9A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER9B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER10A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER10B, INPUT_PULLUP);                 // normal encoder


#elif defined V2HARDWARE                    // Andromeda 1st prototype h/w
  pinMode(VPINENCODER9A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER9B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER10A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER10B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER11A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER11B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER12A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER12B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER13A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER13B, INPUT_PULLUP);                 // normal encoder

  pinMode(VPINENCODER14A, INPUT_PULLUP);                 // normal encoder
  pinMode(VPINENCODER14B, INPUT_PULLUP);                 // normal encoder
#endif




  pinMode(VPINBUTTON1, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON2, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON3, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON4, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON5, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON6, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON7, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON8, INPUT_PULLUP);                   // normal pushbutton
  
  pinMode(VPINBUTTON9, INPUT_PULLUP);                   // normal pushbutton
  pinMode(VPINBUTTON10, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON11, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON12, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON13, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON14, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON15, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON16, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON17, INPUT_PULLUP);                  // normal pushbutton
#ifdef V3HARDWARE                           // Andromeda 2nd prototype h/w
  pinMode(VPINBUTTON18, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON19, INPUT_PULLUP);                  // normal pushbutton
  pinMode(VPINBUTTON20, INPUT_PULLUP);                  // normal pushbutton


#elif defined V2HARDWARE                    // Andromeda 1st prototype h/w
  pinMode(VPINBUTTON18, INPUT_PULLUP);                  // normal pushbutton
#else                                       // Odin hardware
//  pinMode(52, OUTPUT);                      // temp for debug
  pinMode(52, INPUT_PULLUP);                            // ext PTT input
  pinMode(VPINENCODER1SW, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER3SW, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER5SW, INPUT_PULLUP);                // normal encoder
  pinMode(VPINENCODER7SW, INPUT_PULLUP);                // normal encoder
#endif

}
