/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// types.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __TYPES_H
#define __TYPES_H

//
// define the numbers of controls available
//
#ifdef V3HARDWARE                   // Andromeda 2nd prototype
#define VMAXINDICATORS 12
#define VMAXENCODERS 10             // configurable, not including VFO
#define VMAXGPIOBUTTONS 20          // attached to I/O pins
#define VMAXBUTTONS 36

#elif defined V2HARDWARE
#define VMAXINDICATORS 9
#define VMAXENCODERS 14             // configurable, not including VFO
#define VMAXGPIOBUTTONS 18          // attached to I/O pins
#define VMAXBUTTONS 34

#else                               // original Odin
#define VMAXINDICATORS 7
#define VMAXENCODERS 8              // configurable, not including VFO
#define VMAXBUTTONS 21
#define VMAXGPIOBUTTONS 22          // attached to I/O pins
#endif
//
// this type enumerates the Nextion display pages:
//
enum EDisplayPage
{
  eFrontPage,                               // "normal" front page display
  eIOTestPage,                               // I/O debug page
  eAboutPage,
  eFreqEntryPage,
  eBandPage,
  eModePage,
  eNRPage,
  eRFPage,
  eSettingsPage,
  eConfigurePage,
  eSplashPage
};

//
// types etc for editing data:
//
typedef enum
{
  eEncoders,
  ePushbuttons,
  eIndicators
} EControlTypes;

//
// this enum type lists all the functions that can be assigned to configurable encoders
//
typedef enum
{
  eENNoAction,
  eENMasterGain,
  eENAFGain,
  eENRX1AFGain,
  eENRX2AFGain,
  eENAGCLevel,
  eENRX1AGCLevel,
  eENRX2AGCLevel,
  eENStepAtten,
  eENRX1StepAtten,
  eENRX2StepAtten,
  eENFilterHigh,
  eENFilterLow,
  eENDrive,
  eENMicGain,
  eENVFOATune,
  eENVFOBTune,
  eENVOXGain,
  eENVOXDelay,
  eENCWSidetone,
  eENCWSpeed,
  eENSquelch,
  eENDiversityGain,
  eENDiversityPhase,
  eENCompanderThreshold,
  eENRIT,
  eENDisplayPan,
  eENDisplayZoom,
  eENRxMultiAFGain,
  eENRXMultiStereoBalance,
  eENRX1StereoBalance,
  eENRX2StereoBalance,
  eENMulti                      // multifunction
} EEncoderActions;
#define VNUMENCODERACTIONS 33

//
// this enum type lists all the functions that can be assigned to LED indicators
//
typedef enum
{
  eINMOX,
  eINTune,
  eINRIT,
  eINSplit,
  eINCTune,
  eINLock,
  eINNB,
  eINNR,
  eINSNB,
  eINANF,
  eINSquelch,
  eINVFOAB,
  eINCompanderEnabled,
  eINPuresignalEnabled,
  eINXIT,
  eINVFOSync,
  eINEncoder2nd,
  eINNone
} EIndicatorActions;
#define VNUMINDICATORACTIONS 18

//
// this enum type lists all the functions that can be assigned to configurable pushbuttons
// note this includes the "click" switch on an encoder
//
typedef enum
{
  ePBNone,                            // no assigned function
  ePBEncoderClick,                    // for dual fn encoders
  ePBABVfo,
  ePBMox,
  ePBTune,
  ePBChanAFMute,
  ePBRX1AFMute,
  ePBRX2AFMute,
  ePBFilterReset,
  ePBBandUp,
  ePBBandDown,
  ePBModeUp,
  ePBModeDown,
  ePBAGCSpeed,
  ePBNBStep,
  ePBNRStep,
  ePBSNB,
  ePBANF,
  ePBRIT,
  ePBRITPlus,
  ePBRITMinus,
  ePBAtoB,
  ePBBtoA,
  ePBSwap,
  ePBSplit,
  ePBCTune,
  ePBLock,
  ePBStartStop,
  ePBSquelch,
  ePBAtten,
  ePBVoxOnOff,
  ePBDiversityFastSlow,
  ePBCompanderEnable,
  ePBPuresignalEnable,
  ePBPuresignal2Tone,
  ePBPuresignalSingleCal,
  ePBMonEnable,
  ePBDiversityEnable,
  ePBVFOSync,
  ePBClearRIT,
  ePBFilterUp,
  ePBFilterDown,
  ePBVAC1OnOff,
  ePBVAC2OnOff,
  ePBDisplayCentre,
  ePBSoftKey1,                // F1-F8 below screen
  ePBSoftKey2,
  ePBSoftKey3,
  ePBSoftKey4,
  ePBSoftKey5,
  ePBSoftKey6,
  ePBSoftKey7,
  ePBSoftKey8

} EButtonActions;
#define VNUMBUTTONACTIONS 45

//
// this enum defines legal baud rates for serial
//
typedef enum
{
  eBaud9600,
  eBaud38400,
  eBaud115200
} EBaudRates;

//
// type to describe how encoders change between 2 functions
//
typedef enum
{
  eDualFnClick,
  eDualFnPress
} EDualFnEncoders;


//
// type to encode a band selection
//
typedef enum
{
  e160,
  e80,
  e60,
  e40,
  e30,
  e20,
  e17,
  e15,
  e12,
  e10,
  e6,
  eGen,
  e2,                       // be aware the order here different from CAT list!
  eWWV
} EBand;


//
// type to encode a mode
//
typedef enum
{
  eLSB,
  eUSB,
  eDSB,
  eCWL,
  eCWU,
  eFM,
  eAM,
  eDIGU,
  eSPEC,
  eDIGL,
  eSAM,
  eDRM
} EMode;



//
// type to endoe noise reduction setting
//
typedef enum
{
  eNROff,
  eNR1,
  eNR2
} ENRState;


//
// type to endoe noise blanking setting
//
typedef enum
{
  eNBOff,
  eNB1,
  eNB2
} ENBState;


//
// type to encode AGC speed
//
typedef enum
{
  eFixed,
  eLong,
  eSlow,
  eMedium,
  eFast,
  eCustom
} EAGCSpeed;


//
// type to encode RX attenuation
//
typedef enum
{
  e0dB,
  e10dB,
  e20dB,
  e30dB
} EAtten;


#endif //not defined
