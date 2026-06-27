/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// configdata.c
// this file holds the code to save and load settings to/from FLASH
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"

#include "types.h"
#include <DueFlashStorage.h>
DueFlashStorage dueFlashStorage;                 // instance of flash reader

#define VFLASHINITPATTERN 0x6D                    // addr 0 set to this if configured

//
// note Odin (original hardware) switch and encoder numbering:
// in the software switches are numbered 0-20, and encoders 0-7. The VFO encoder is treated separately.
// these correspond to the control of Kjell's PCB as follows:
//
// encoder numbering:
//    PCB   software
//    VFO    (treated separately)
//    2A      0
//    2B      1
//    3A      2
//    3B      3
//    4A      4
//    4B      5
//    5A      6
//    5B      7
//
// switch numbering:
//    PCB   software
//    SW1                 0
//    SW2                 1
//    SW3                 2
//    SW4                 3
//    SW5                 4
//    SW6                 5
//    SW7                 6
//    SW8                 7
//    SW9                 8
//    SW10                9
//    SW11               10
//    SW12               11
//    SW13               12
//    SW14               13
//    SW15               14
//    SW16               15
//    SW17               16
//    encoder 2 push     17
//    encoder 3 push     18
//    encoder 4 push     19
//    encoder 5 push     20
//



#ifdef V3HARDWARE                             // Andromeda 2nd prototype
//
// array of Andromeda factory encoder settings. User can reconfigure from this.
//
const EEncoderActions GFactoryEncoderActions[] =
{
  eENRX1AFGain,           // 2A: RX1 AF gain
  eENRX1AGCLevel,         // 2B: RX1 AGC
  eENFilterHigh,          // 3A: filter cut
  eENFilterLow,           // 3B: filter cut
  eENDiversityGain,       // 4A: diversity gain
  eENDiversityPhase,      // 4B: diversity phase
  eENRX2AFGain,           // 6A: RX2 AF gain
  eENRX2AGCLevel,         // 6B: RX2 AGC
  eENMulti,               // 5A: multifunction
  eENRIT                  // 7A: RIT or XIT
};


//
// array of Andromeda factory indicator settings. User can reconfigure from this.
//
const EIndicatorActions GFactoryIndicatorActions[] =
{
  eINLock,                    // 0: LOCK
  eINPuresignalEnabled,       // 1: Puresignal enabled
  eINMOX,                     // 2: TX/RX
  eINTune,                    // 3: TUNE enabled
  eINCTune,                   // 4: Click Tune enabled
  eINVFOSync,                 // 5: VFO Sync enabled
  eINNB,                      // 6: NB
  eINRIT,                     // 7: RIT on
  eINTune,                    // 8: TUNE (this should be ATU ready, but not a PowerSDR function)
  eINNR,                      // 9: NR
  eINSNB,                     // 10: SNB
  eINXIT                      // 11: XIT
};



//
// array of Andromeda factory pushbutton settings. User can reconfigure from this.
//
const EButtonActions GFactoryButtonActions[] =
{
  ePBRIT,                             // 0: encoder 7 click
  ePBSoftKey1,                        // 1: SW58 (F1 menu button)
  ePBSoftKey2,                        // 2: SW59 (F2 menu button)
  ePBSoftKey3,                        // 3: SW63 (F3 menu button)
  ePBSoftKey4,                        // 4: SW50 (F4 menu button)
  ePBSoftKey5,                        // 5: SW46 (F5 menu button)
  ePBRX1AFMute,                       // 6: encoder 2 click
  ePBSoftKey6,                        // 7: SW17 (F6 menu button)
  ePBDiversityFastSlow,               // 8: encoder 4 click
  ePBEncoderClick,                    // 9: encoder 5 click
  ePBRITPlus,                         // 10: SW11
  ePBBandDown,                        // 11: SW12
  ePBModeDown,                        // 12: SW13
  ePBStartStop,                       // 13: SW14
  ePBBandUp,                          // 14: SW15
  ePBModeUp,                          // 15: SW16
  ePBSoftKey7,                        // 16: SW18 (F7 menu button)
  ePBFilterReset,                     // 17: encoder 3 click
  ePBFilterDown,                      // 18: SW49
  ePBFilterUp,                        // 19: SW48
  ePBRIT,                             // 20: SW9
  ePBABVfo,                           // 21: SW1
  ePBMox,                             // 22: SW2
  ePBTune,                            // 23: SW3
  ePBCTune,                           // 24: SW4
  ePBLock,                            // 25: SW5
  ePBAtoB,                            // 26: SW6
  ePBRX2AFMute,                       // 27: encoder 6 click
  ePBVFOSync,                         // 28: SW56
  ePBRITMinus,                        // 29: SW10
  ePBSplit,                           // 30: SW8
  ePBBtoA,                            // 31: SW7
  ePBPuresignalEnable,                // 32: SW21
  ePBNone,                            // 33: now unused
  ePBPuresignal2Tone,                 // 34: SW20
  ePBSoftKey8                         // 35: SW22 (F8 menu button)
};



#elif defined V2HARDWARE                      // Andromed 1st prototype

//
// array of Andromeda factory encoder settings. User can reconfigure from this.
//
const EEncoderActions GFactoryEncoderActions[] =
{
  eENRX1AFGain,           // 2A: RX1 AF gain
  eENRX1AGCLevel,         // 2B: RX1 AGC
  eENFilterHigh,          // 3A: filter cut
  eENFilterLow,           // 3B: filter cut
  eENDiversityGain,       // 4A: diversity gain
  eENDiversityPhase,      // 4B: diversity phase
  eENMicGain,             // 5A: mic gain
  eENDrive,               // 5B: drive
  eENRX2AFGain,           // 6A: RX2 AF gain
  eENRX2AGCLevel,         // 6B: RX2 AGC
  eENCompanderThreshold,  // 7A: Compander
  eENMasterGain,          // 7B: master gain (also used for sidetone level)
  eENMulti,               // 8A: multifunction
  eENSquelch              // 8B: squelch
};


//
// array of Andromeda factory indicator settings. User can reconfigure from this.
//
const EIndicatorActions GFactoryIndicatorActions[] =
{
  eINLock,                    // 0: LOCK
  eINPuresignalEnabled,       // 1: Puresignal enabled
  eINMOX,                     // 2: TX/RX
  eINTune,                    // 3: TUNE enabled
  eINCTune,                   // 4: Click Tune enabled
  eINCompanderEnabled,        // 5: COMP enabled
  eINSquelch,                 // 6: Squelch
  eINRIT,                     // 7: RIT on
  eINTune                     // 8: TUNE (this should be ATU ready, but not a PowerSDR function)
};



//
// array of Andromeda factory pushbutton settings. User can reconfigure from this.
// 1-7 are encoder 1-7 "click" settings; 8-23 are pushbuttons 1-16
//
const EButtonActions GFactoryButtonActions[] =
{
  ePBRITMinus,                        // 0: SW10
  ePBSoftKey1,                        // 1: SW46 (softkey F1)
  ePBSoftKey2,                        // 2: SW47 (softkey F2)
  ePBSoftKey3,                        // 3: SW48 (softkey F3)
  ePBSoftKey4,                        // 4: SW49 (softkey F4)
  ePBSoftKey5,                        // 5: SW50 (softkey F5)
  ePBRX1AFMute,                       // 6: encoder 2 click
  ePBNRStep,                          // 7: SW17
  ePBDiversityFastSlow,               // 8: encoder 4 click
  ePBNone,                            // 9: encoder 5 click
  ePBRITPlus,                         // 10: SW11
  ePBBandDown,                        // 11: SW12
  ePBModeDown,                        // 12: SW13
  ePBStartStop,                       // 13: SW14
  ePBBandUp,                          // 14: SW15
  ePBModeUp,                          // 15: SW16
  ePBNBStep,                          // 16: SW18
  ePBFilterReset,                     // 17: encoder 3 click
  ePBRIT,                             // 18: SW9
  ePBABVfo,                           // 19: SW1
  ePBMox,                             // 20: SW2
  ePBTune,                            // 21: SW3
  ePBCTune,                           // 22: SW4
  ePBLock,                            // 23: SW5
  ePBAtoB,                            // 24: SW6
  ePBRX2AFMute,                       // 25: encoder 6 click
  ePBCompanderEnable,                 // 26: encoder 7 click
  ePBEncoderClick,                    // 27: encoder 8 click
  ePBSplit,                           // 28: SW8
  ePBBtoA,                            // 29: SW7
  ePBSoftKey6,                        // 30: SW21
  ePBSoftKey8,                        // 31: SW19
  ePBSoftKey7,                        // 32: SW20
  ePBSNB                              // 33: SW22
};

#else
//
// defines for original Odin PCB
//
//
// array of Odin factory encoder settings. User can reconfigure from this.
//
const EEncoderActions GFactoryEncoderActions[] =
{
  eENAFGain,
  eENAGCLevel,
  eENFilterHigh,
  eENFilterLow,
  eENDiversityGain,
  eENDiversityPhase,
  eENMulti,
  eENDrive  
};


//
// array of Odin factory indicator settings. User can reconfigure from this.
//
const EIndicatorActions GFactoryIndicatorActions[] =
{
  eINVFOAB,
  eINMOX,
  eINTune,
  eINCTune,
  eINLock,
  eINRIT,
  eINNR 
};



//
// array of Odin factory pushbutton settings. User can reconfigure from this.
// 1-7 are encoder 1-7 "click" settings; 8-23 are pushbuttons 1-16
//
const EButtonActions GFactoryButtonActions[] =
{
  ePBABVfo,                           // SW1
  ePBMox,                             // SW2
  ePBTune,                            // SW3
  ePBCTune,                           // SW4
  ePBLock,                            // SW5
  ePBAtoB,                            // SW6
  ePBBtoA,                            // SW7
  ePBSplit,                           // SW8
  ePBRIT,                             // SW9
  ePBRITMinus,                        // SW10
  ePBRITPlus,                         // SW11
  ePBBandDown,                        // SW12
  ePBModeDown,                        // SW13
  ePBStartStop,                       // SW14
  ePBBandUp,                          // SW15
  ePBModeUp,                          // SW16
  ePBNRStep,                          // SW17
  ePBChanAFMute,                      // encoder 2 click
  ePBFilterReset,                     // encoder 3 click
  ePBDiversityFastSlow,               // encoder 4 click
  ePBEncoderClick                     // encoder 5 click
};

#endif



//
// RAM storage of loaded settings
// these are loaded from FLASH after boot up
//
EBaudRates GUSBBaudRate;
EDualFnEncoders GEncoderOperation;                   // global for all encoders
EEncoderActions GEncoderMainActions[VMAXENCODERS];
EEncoderActions GEncoder2ndActions[VMAXENCODERS];
EIndicatorActions GIndicatorActions[VMAXINDICATORS];
EButtonActions GButtonActions[VMAXBUTTONS];
bool GEncoderReversed[VMAXENCODERS];

bool GBottomEncoderStrings;                           // true to have legends at the display bottom
bool GSideEncoderStrings;                             // true to display legends at the side
byte GEncoderDivisor;                                // number of edge events per declared click
byte GVFOEncoderDivisor;                             // number of edge events per declared click



//
// function to copy all config settings to flash
// this copies the current RAM vaiables to the persistent storage
//
void CopySettingsToFlash(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;
  
//
// first set that we have initialised the FLASH
//
  dueFlashStorage.write(0, VFLASHINITPATTERN);
//
// now copy settings from RAM data
//
  Setting = (byte)GUSBBaudRate;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GEncoderOperation;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GBottomEncoderStrings;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GSideEncoderStrings;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GEncoderDivisor;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GVFOEncoderDivisor;
  dueFlashStorage.write(Addr++, Setting);
// write encoders  
  for (Cntr=0; Cntr < VMAXENCODERS;   Cntr++)
  {
    Setting = (byte)GEncoderMainActions[Cntr];
    dueFlashStorage.write(Addr++, Setting);
    Setting = (byte)GEncoderReversed[Cntr];
    dueFlashStorage.write(Addr++, Setting);
  }
// write indicators  
  for (Cntr=0; Cntr < VMAXINDICATORS;   Cntr++)
  {
    Setting = (byte)GIndicatorActions[Cntr];
    dueFlashStorage.write(Addr++, Setting);
  }
// write pushbuttons  
  for (Cntr=0; Cntr < VMAXBUTTONS;   Cntr++)
  {
    Setting = (byte)GButtonActions[Cntr];
    dueFlashStorage.write(Addr++, Setting);
  }
}



//
// function to copy initial settings to FLASH
// this sets the factory defaults
// the settings here should match the fornt panel legend!
//
void InitialiseFlash(void)
{
  int Cntr;
  
  GUSBBaudRate = eBaud115200;
  GEncoderOperation = eDualFnClick;
  GBottomEncoderStrings = true;
  GSideEncoderStrings = true;
  GEncoderDivisor = 2;
  GVFOEncoderDivisor = 4;

  
// initialise encoders
  for(Cntr=0; Cntr < VMAXENCODERS; Cntr++)
  {
    GEncoderMainActions[Cntr] = GFactoryEncoderActions[Cntr];
    GEncoderReversed[Cntr] = false;
  }
// initialise indicators
  for(Cntr=0; Cntr < VMAXINDICATORS; Cntr++)
    GIndicatorActions[Cntr] = GFactoryIndicatorActions[Cntr];
// initialise indicators
  for(Cntr=0; Cntr < VMAXBUTTONS; Cntr++)
    GButtonActions[Cntr] = GFactoryButtonActions[Cntr];

// now copy them to FLASH
  CopySettingsToFlash();
}



//
// function to load config settings from flash
//
void LoadSettingsFromFlash(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;

//
// first see if we have initialised the FLASH previously
// if not, copy settings to it
//
  Setting = dueFlashStorage.read(0);
  if (Setting != VFLASHINITPATTERN)
    InitialiseFlash();
//
// now copy out settings to RAM data
//
  GUSBBaudRate = (EBaudRates)dueFlashStorage.read(Addr++);
  GEncoderOperation = (EDualFnEncoders)dueFlashStorage.read(Addr++);
  GBottomEncoderStrings = (bool)dueFlashStorage.read(Addr++);
  GSideEncoderStrings = (bool)dueFlashStorage.read(Addr++);
  GEncoderDivisor = (byte)dueFlashStorage.read(Addr++);
  GVFOEncoderDivisor = (byte)dueFlashStorage.read(Addr++);
// read encoders  
  for (Cntr=0; Cntr < VMAXENCODERS;   Cntr++)
  {
    GEncoderMainActions[Cntr] = (EEncoderActions)dueFlashStorage.read(Addr++);
    GEncoderReversed[Cntr] = (bool)dueFlashStorage.read(Addr++);
  }
// read indicators  
  for (Cntr=0; Cntr < VMAXINDICATORS;   Cntr++)
    GIndicatorActions[Cntr] = (EIndicatorActions)dueFlashStorage.read(Addr++);
// read pushbuttons  
  for (Cntr=0; Cntr < VMAXBUTTONS;   Cntr++)
    GButtonActions[Cntr] = (EButtonActions)dueFlashStorage.read(Addr++);
}


//
// functions to retrieve assigned button, indicator and encoder actions
// for encoder: 
//
EEncoderActions GetEncoderAction(unsigned int Encoder, bool Is2ndFunction)
{
  EEncoderActions Action1;
  EEncoderActions Result;

  Action1 = GEncoderMainActions[Encoder];           //  main
  
  if (Action1 == eENMulti)
    Result = eENMulti;
  else if (Is2ndFunction)
  {
    if (Encoder < (VMAXENCODERS-1))                    // if not last encoder
      Result = GEncoderMainActions[Encoder+1];        //  2nd function is assigned to the next encoder number
    else
      Result = Action1;
  }
  else
    Result = Action1;
  
  return Result;   
}


EIndicatorActions GetIndicatorAction(unsigned int Indicator)
{
  return GIndicatorActions[Indicator];
}


EButtonActions GetButtonAction(unsigned int Button)
{
  return GButtonActions[Button];
}


bool GetEncoderReversed(unsigned int Encoder)
{
  return GEncoderReversed[Encoder];
}

//
// functions to store assigned button, indicator and encoder actions
// this is a precursor to doing a "save to flash"
//
void SetEncoderAction(unsigned int Encoder, EEncoderActions Setting)
{
  GEncoderMainActions[Encoder] = Setting;
}

void SetIndicatorAction(unsigned int Indicator, EIndicatorActions Setting)
{
  GIndicatorActions[Indicator] = Setting;
}

void SetButtonAction(unsigned int Button, EButtonActions Setting)
{
  GButtonActions[Button] = Setting;
}


void SetEncoderReversed(unsigned int Encoder, bool IsReversed)
{
  GEncoderReversed[Encoder] = IsReversed;
}
