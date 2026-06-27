/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// eventqueue.h
// output event queue operations
/////////////////////////////////////////////////////////////////////////


#include "eventqueue.h"
#include <wire.h>
#include "led.h"
#include "iopins.h"
#include "globalinclude.h"
#include "button.h"

#define VI2CSLAVEADDR 0x15                    // Arduino slave address
#define VLEDADDR 0x0A                         // LED register address
#define VEVENTADDR 0x0B                       // event register address
#define VIDADDR 0x0C                          // ID/version register address
#define VHWADDR 0x0D                          // HW version register

//
// the event queue is a simple circular buffer implemented using an array.
// entries are held as 16 bit integers; event data = LSB
//
byte WritePtr;                                // entry number to write at
byte ReadPtr;                                 // entry number to read from 
unsigned int EventQ[VQUEUESIZE];              // declare event queue
unsigned int CommandWord;                     // new 2 byte command word from pi
bool GSendVersion;                            // true if commanded to send ot version not data.
unsigned int GLEDWord = 0;                    // LED states; top bit is PBX
unsigned int GAddressRegister;                // I2C slave address requested


//
// function to get the number of entries in the queue
// dont's call from inside an interrupt handler!
//
byte GetQEntries()
{
  signed int Entries;                         // no. entries held 

  noInterrupts();
  Entries = WritePtr - ReadPtr;               // if not wrapped
  if(Entries < 0)                             //
    Entries += VQUEUESIZE;
  interrupts();
  
  return (byte)Entries;
}



//
// function to get an event from the queue
// return true if successful; entry returned into passed param
//
bool GetEventFromQ(unsigned int *Event)
{
  unsigned int Entry;                 // entry pulled from queue
  bool Success = false;
  if (GetQEntries() >= 0)
  {
    Success = true;
    *Event = EventQ[ReadPtr];
//
// update pointers; disable interrupts while testing
//
    noInterrupts();
    if(++ReadPtr >= VQUEUESIZE)
      ReadPtr = 0;
    interrupts();

  }
}



//
// function to add an event to the output queue
//
void AddEvent2Q(EEventType Event, byte EventData)
{
  unsigned int Entry;         // entry to add to queue
  byte Count;

  Entry = (unsigned int)EventData;
  Entry |= (((byte)Event) << 8);
//
// add entry to queue if there is space
//
  Count = GetQEntries();
  if(Count < VMAXQUEUEENTRIES)
  {
    EventQ[WritePtr] = Entry;
//
// update write pointer
//
    noInterrupts();
    if(++WritePtr >= VQUEUESIZE)
      WritePtr = 0;
    interrupts();
//
// set interrupt out, by driving pin to 0 
//
    digitalWrite(VPINPIINTERRUPT, LOW);                   // assert interrupt output
  }
}

//
// note that I2C transfers are not simple, and begine wth a register address WRITE to Arduino.WritePtr
// Slave write: 3 byte to Arduino (receiveEvent)
// Slave read: 1 byte to Arduino (receivEvent) followed by 2 bytes from Arduino (requestEvent)
//



//
// interrupt handler when data requested from I2C slave read
// response is 16 bits: either an event queue entry + length; or 0
// note the code to access the queue is coded directly in here, to avoid issues with turning back on interrupts by accident. 
//
void requestEvent() 
{
  bool Success;
  signed int Entries;
  unsigned int Response = 0;                  // response code to I2C

  if(GAddressRegister == VIDADDR)
  {
     Response = (PRODUCTID << 8) | SWVERSION;
     digitalWrite(VPINPIINTERRUPT, HIGH);                   // clear interrupt output
  }
  else if(GAddressRegister == VHWADDR)
  {
     Response = HWVERSION;
     digitalWrite(VPINPIINTERRUPT, HIGH);                   // clear interrupt output
  }
  else if (GAddressRegister == VLEDADDR)
    Response = GLEDWord;
  else if (GAddressRegister == VEVENTADDR)
  {
    // find count of entries available in the queue
    Entries = WritePtr - ReadPtr;               // if not wrapped
    if(Entries < 0)                             //
      Entries += VQUEUESIZE;

    if(Entries)                                 // if there are queue entries
    {
// read a queue location, then update read pointer
      Response = EventQ[ReadPtr];
      if(++ReadPtr >= VQUEUESIZE)
        ReadPtr = 0;
      Response |= ((Entries & 0xF)<<12);
    }
  }
  Wire.write(Response & 0xFF);            // respond with message of 2 bytes, low byte 1st
  Wire.write((Response >> 8) & 0xFF);     // respond with message of 2 bytes high byte
//
// clear interrupt out if last one read
//
  if(Entries <= 1)
  digitalWrite(VPINPIINTERRUPT, HIGH);                   // clear interrupt output
}


//
// interrupt handler for for I2C data sent to Arduino
// this could be a 3 byte write, or a 1 byte address for a read
// simply store new command word for processing in normal sequence
// GSendVersion set if command is to send out versino information
// GShiftOverride set if all 11 LEDs should be set by the software
// commands are stored here; processed by 2ms tick code. 
//
void receiveEvent(int Count)
{
  byte Data;
  byte Cntr = 0;
  
//  Serial.print("receiveEvent: bytes=");
//  Serial.print(Count);
//  Serial.print(": ");
  GAddressRegister = Wire.read(); // receive address register
  Count--;

  if(Count != 0)
  {
//    while(Wire.available()) // loop through all but the last
    while(Count != 0) // loop through all but the last
    {
      Data = Wire.read(); // receive byte as a character
      Count--;
      if(Cntr++ == 0)
        CommandWord = Data;
      else
        CommandWord = (CommandWord & 0xFF) | (Data << 8);
    }
    if(GAddressRegister == VLEDADDR)
      GLEDWord = CommandWord;
  }
}

//
// initialise I2C slave
//
void InitialiseI2CSlave(void)
{
  Wire.begin(VI2CSLAVEADDR);                // join i2c bus with address 0x15
  Wire.onRequest(requestEvent);             // register event
  Wire.onReceive(receiveEvent);             // register slave event handler
}


//
// eventqueue Tick
// process any command word provided
// simply set LEDs to required state from CommandWord, when not in test mode
//
void EventQueueTick(void)
{
  byte LED;
  bool State;
  unsigned int Word;
  byte LEDCount = VMAXINDICATORS-2;

  if(LEDTestComplete)
  {
// if override bit not set, we don't set the two highest LED bits
//(these are controlled locally by the "shift" buttons)
//
    if(GLEDWord & 0x8000)                  // if override set
      LEDCount += 2;
    Word = GLEDWord;                       // get LED settings
    for(LED=0; LED < LEDCount; LED++)
    {
      State = (bool)(Word &1);                // get LED state
      SetLED(LED, State);
      Word = Word >> 1;
    }
  }
}

