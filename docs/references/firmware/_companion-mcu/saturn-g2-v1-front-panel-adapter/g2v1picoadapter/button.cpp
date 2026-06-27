/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// button.cpp
// this file holds the code to debounce pushbutton inputs scanned from a matrix
/////////////////////////////////////////////////////////////////////////

#include <Wire.h>
#include "globalinclude.h"
#include "button.h"
#include "iopins.h"
#include "cathandler.h"



//
// defines for the MCP23017 address and registers within it
// MCP23017 operated with IOCON.BANK=0
//
#define VMCP23017ADDR 0x20
#define VGPIOAADDR 0x12             // GPIO A (column and LED out)
#define VGPIOBADDR 0x13             // GPIO B (row input)
#define VIODIRAADDR 0x0             // direction A
#define VGPPUA 0x0C                 // pullup control for GPIO A
#define VGPPUB 0x0D                 // pullup control for GPIO B




//
// array to look up the report code from the software scan code
// s/w scan code begins 0 and this table must have the full 20 entries
// reported code see documentation
// note reported button numbers begin at "1" as per ZZZP cat documentation
// these are Andromeda-like functions assignments
//
byte ReportCodeLookup[] = 
{
  47, 50, 45, 44, 
  31, 32, 30, 34, 
  35, 33, 36, 37, 
  38, 21, 42, 43, 
  11, 1, 5, 9
};

#define VBUTTONTICKS 5
#define VLONGPRESSCOUNT 200        // 2s
byte GButtonTickCounter;
unsigned int PBPinShifts[VMAXBUTTONS];      // sequential pin states for each pin
unsigned int PBLongCount[VMAXBUTTONS];      // time count for long press










//
// function to write 8 bit value to MCP23017
// returns register value
//
void WriteMCPRegister(byte ChipAddress, byte Address, byte Value)
{
  byte res1=0;
  byte res2=0;
  byte res3=0;

//  Wire1.beginTransmission(ChipAddress);
//  res3 = Wire1.endTransmission();
//  Serial.printf("writeregister: res3=%d\n", res3);

  Wire1.beginTransmission(ChipAddress);
  res1=Wire1.write(Address);                                  // point to register
  res2=Wire1.write(Value);                                    // write its data
  res3 = Wire1.endTransmission();
}


//
// function to read 16 bit input from MCP23017
// returns GPIOB (top 8 bits) GPIO A (bottom 8 bits)
unsigned int ReadPushbuttonMCP(void)
{
  byte Input;                                   // becomes the new bit sequence for an input
  byte Input2;
  byte res3;
  unsigned int Input23017;                      // 16 bit value

//  Wire1.beginTransmission(VMCP23017ADDR);
//  res3 = Wire1.endTransmission();
//  Serial.printf("readpushbutton: res3=%d    ", res3);

      Wire1.beginTransmission(0x20);
      Wire1.endTransmission();


  Wire1.beginTransmission(VMCP23017ADDR);
  Wire1.write(VGPIOAADDR);                                     // point to GPIOA
  Wire1.endTransmission();
  Wire1.requestFrom(VMCP23017ADDR, 2);                            // read 2 bytes
  Input=Wire1.read();                                    // GPIOA
  Input2 = Wire1.read();                                 // GPIOB
  Input23017 = (Input2 << 8) | Input;
  return Input23017;
}





//
// initialise
// init all scanning variables, and assert 1st column
//
void GButtonInitialise(void)
{
  byte PinCntr;

  Wire1.setSDA(VPINSDA);
  Wire1.setSCL(VPINSCL);
  Wire1.begin();                       // I2C
  Wire1.setClock(400000);
//
// initialise pullup resistors on the MCP23017 inputs
//
  WriteMCPRegister(VMCP23017ADDR, VGPPUA, 0xFF);                     // make row inputs have pullup resistors
  WriteMCPRegister(VMCP23017ADDR, VGPPUB, 0xFF);                     // make row inputs have pullup resistors
  GButtonTickCounter = VBUTTONTICKS;
  for(PinCntr=0; PinCntr < VMAXBUTTONS; PinCntr++)
    PBPinShifts[PinCntr] = 0b11111111;
}




//
// Tick()
// only executed every N ticks (aiming for 10ms)
// read 20 bit input vector then process each
//
void ButtonTick(void)
{
  unsigned long PBWord;                      // I2C word
  byte PinCntr;
  byte ScanCode;

  if (--GButtonTickCounter == 0)
  {
    GButtonTickCounter = VBUTTONTICKS;
    //
    // read input vector
    //
    PBWord = ReadPushbuttonMCP();             // buttons [15:0]
    if(digitalRead(VPINENCODER1PB) == HIGH)
      PBWord |= (1<<16);
    if(digitalRead(VPINENCODER2PB) == HIGH)
      PBWord |= (1<<17);
    if(digitalRead(VPINENCODER3PB) == HIGH)
      PBWord |= (1<<18);
    if(digitalRead(VPINENCODER4PB) == HIGH)
      PBWord |= (1<<19);
//
// now process the input vector
//
    for(PinCntr=0; PinCntr < VMAXBUTTONS; PinCntr++)
    {
      ScanCode = ReportCodeLookup[PinCntr];
      PBPinShifts[PinCntr] = ((PBPinShifts[PinCntr] << 1) | (PBWord & 1)) & 0b00000111;           // most recent 3 samples
      PBWord = PBWord >> 1;
      if(PBPinShifts[PinCntr] == 0b00000100)                      // button press detected
      {
        CATHandlePushbutton(ScanCode, true, false);
        PBLongCount[PinCntr] = VLONGPRESSCOUNT;                 // set long press count
      }
      else if (PBPinShifts[PinCntr] == 0b00000011)                // button release detected
      {
        CATHandlePushbutton(ScanCode, false, false);
        PBLongCount[PinCntr] = 0;                               // clear long press count
      }
      else if(PBLongCount[PinCntr] != 0)                          // if button pressed, and long press not yet declared
      {
        if(--PBLongCount[PinCntr] == 0)
        {
          CATHandlePushbutton(ScanCode, false, true);
        }
      }
    }



  }
}


