/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// eventqueue.h
// header for output event queue operations
/////////////////////////////////////////////////////////////////////////
#ifndef __eventqueue_h
#define __eventqueue_h

#include <Arduino.h>


#define VQUEUESIZE 16
#define VMAXQUEUEENTRIES 15

//
// enum for the event types
//
enum EEventType 
{
  eNoEvent,                           // no event present
  eEvVFOStep,                           // VFO encoder steps
  eEvEncoderStep,                       // ordinary encoder steps
  eEvButtonPress,                       // pushbutton press
  eEvButtonLongpress,                   // pushbutton long press
  eEvButtonRelease                      // pushbutton release
};

//
// function to get the number of entries in the queue
//
byte GetQEntries();


//
// function to add an event to the output queue
//
void AddEvent2Q(EEventType Event, byte EventData);

//
// function to get an event from the queue
// return true if successful; entry returned into passed param
//
bool GetEventFromQ(unsigned int *Event);

//
// initialise I2C slave
//
void InitialiseI2CSlave(void);


//
// eventqueue Tick
// simply set LEDs to required state
//
void EventQueueTick(void);

#endif