/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// CAT handler.h
// this file holds the CAT handling code
/////////////////////////////////////////////////////////////////////////
#ifndef __cathandler_h
#define __cathandler_h
#include <Arduino.h>
#include "types.h"
#include "tiger.h"


//
// initialise CAT handler
//
void InitCATHandler(void);


//
// periodic tick
//
void CATHandlerTick(void);


/////////////////////////////////////////////////////////////////////////////////////
//
// handlers to set new data from user (display/encoder/button) to CAT handler
// These are needed where the display sets something
// (pushbutton and encoder selection handled by the CAT code directly)
//
void CATSetFrequency(char* Frequency);         // string with MHz as ASCII
void CATSetBand(EBand Band);
void CATSetMode(EMode Mode);
void CATSetNRState(ENRState State);
void CATSetNBState(ENBState State);
void CATSetSNBState(bool SNBState);
void CATSetANFState(bool ANFState);
void CATSetAGCSpeed(EAGCSpeed Speed);
void CATSetAGCThreshold(int Threshold);       // called by display, needs to find out A or B
void CATSetRX1AGCThreshold(int Threshold);    // channel specific
void CATSetRX2AGCThreshold(int Threshold);    // channel specific
void CATSetAttenuation(EAtten AttenValue);
void CATSetSquelchOnOff(bool State);
void CATSetSplitOnOff(bool State);
void CATSetCTuneOnOff(bool State);
void CATSetVFOLock(bool State);


//
// set ext mox effect
// true if ext mox input active
//
void CATExtMox(bool IsActiveMOX);

//
// request VOX on/off state
// we don't use timeout as user can simply press again
//
void CATRequestVoxOnOff(void);

//
// send VOX on/off request to CAT
//
void CATSetVoxOnOff(bool IsMute);



//
// request RX1 AF MUTE state
//
void CATRequestRX1Mute(void);
//
// send RX1 MUTE request to CAT
//
void CATSetRX1Mute(bool IsMute);

//
// request RX2 AF MUTE state
//
void CATRequestRX2Mute(void);
//
// send RX2 MUTE request to CAT
//
void CATSetRX2Mute(bool IsMute);

//
// request Radio On/Off state
// we don't use timeout as user can simply press again
//
void CATRequestRadioOnOff(void);
//
// send Radio On/Off request to CAT
//
void CATSetRadioOnOff(bool IsStart);


//
// request the current band
// sends a request message, then sets timeout
//
void CATRequestBand(void);

//
// request Compander on/off state
// we don't use timeout as user can simply press again
//
void CATRequestCompanderOnOff(void);

//
// send Compander on/off request to CAT
//
void CATSetCompanderOnOff(bool IsMute);

//
// request Puresignal on/off state
// we don't use timeout as user can simply press again
//
void CATRequestPuresignalOnOff(void);

//
// send Puresignal on/off request to CAT
//
void CATSetPuresignalOnOff(bool IsMute);

//
// request TwoTone test on/off state
// we don't use timeout as user can simply press again
//
void CATRequestTwoToneOnOff(void);

//
// send TwoTone on/off request to CAT
//
void CATSetTwoToneOnOff(bool IsMute);

//
// request MON on/off state
// we don't use timeout as user can simply press again
//
void CATRequestMonOnOff(void);

//
// send MON on/off request to CAT
//
void CATSetMonOnOff(bool IsMute);

//
// request Diversity on/off state
// we don't use timeout as user can simply press again
//
void CATRequestDiversityOnOff(void);

//
// send Diversity on/off request to CAT
//
void CATSetDiversityOnOff(bool IsMute);

//
// request VAC1 on/off state
// we don't use timeout as user can simply press again
//
void CATRequestVAC1OnOff(void);

//
// send VAC1 on/off request to CAT
//
void CATSetVAC1OnOff(bool IsOn);

//
// request VAC2 on/off state
// we don't use timeout as user can simply press again
//
void CATRequestVAC2OnOff(void);

//
// send VAC2 on/off request to CAT
//
void CATSetVAC2OnOff(bool IsOn);

//
// request AGC threshold
// sends a request message and sets a timeout
//
void CATRequestRX1AGCThreshold(void);
void CATRequestRX2AGCThreshold(void);

//
// deal with an encoder turn: a CAT message has arrived so now we need to update the param and send back to CAT
// update & clip; send CAT; clear stored clicks; send to display
//
void SendRX1AGCThresholdClicks(void);
void SendRX2AGCThresholdClicks(void);

//
// request filter number up/down;
// filter up/down button clicks
//
void CATRequestFilterUpDown(void);
void SendFilterUpDownClicks(void);


//
// request filter low;
// filter low encoder clicks
//
void CATRequestFilterLow(void);
void SendFilterLowClicks(void);

//
// request filter high;
// filter high encoder clicks
//
void CATRequestFilterHigh(void);
void SendFilterHighClicks(void);

//
// request squelch;
// squelch encoder clicks
//
void CATRequestSquelchLevel(void);
void SendSquelchLevelClicks(void);

//
// request RX1, RX2 AF gain;
// RX1, RX2 AF gain encoder clicks
//
void CATRequestRX1AFGain(void);
void SendRX1AFGainClicks(void);
void CATRequestRX2AFGain(void);
void SendRX2AFGainClicks(void);

//
// request RX1, RX2 step atten (treated as RF gain);
// RX1, RX2 step atten encoder clicks
//
void CATRequestRX1StepAtten(void);
void SendRX1StepAttenClicks(void);
void CATRequestRX2StepAtten(void);
void SendRX2StepAttenClicks(void);

//
// request RX1, RX2 AGC threshold;
// RX1, RX2 AGC threshold encoder clicks
//
void CATRequestAGCThreshold(void);                // called by display
void CATRequestRX1AGCThreshold(void);
void SendRX1AGCThreshold(void);
void CATRequestRX2AGCThreshold(void);
void SendRX2AGCThreshold(void);


//
// request master AF gain;
// master AF gain encoder clicks
//
void CATRequestMastAFGain(void);
void SendMastAFGainClicks(void);

//
// request drive level;
// drive level encoder clicks
//
void CATRequestDriveLevel(void);
void SendDriveLevelClicks(void);

//
// request mic gain;
// mic gain encoder clicks
//
void CATRequestMicGain(void);
void SendMicGainClicks(void);

//
// request vox gain;
// vox gain encoder clicks
//
void CATRequestVoxGain(void);
void SendVoxGainClicks(void);

//
// request vox delay;
// vox delay encoder clicks
//
void CATRequestVoxDelay(void);
void SendVoxDelayClicks(void);

//
// request CW sidetone;
// CW sidetone encoder clicks
//
void CATRequestCWTone(void);
void SendCWToneClicks(void);

//
// request CW speed;
// CW speed encoder clicks
//
void CATRequestCWSpeed(void);
void SendCWSpeedClicks(void);

//
// request Diversity gain;
// diversity gain encoder clicks
//
void CATRequestDiversityGain(void);
void SendDiversityGainClicks(void);

//
// request Diversity phase;
// diversity phase encoder clicks
//
void CATRequestDiversityPhase(void);
void SendDiversityPhaseClicks(void);

//
// request Diversity reference source
//
void CATRequestDiversityRefSource(void);

//
// encoder assigned to VFO tune
// similar to above, but can be A/B
// simply request N steps up or down
//
void CATEncoderVFOABTune(int Clicks, bool IsA);

//
// request compander threshold;
// compander threshold encoder clicks
//
void CATRequestCompThreshold(void);
void SendCompThresholdClicks(void);

//
// request display pan;
// display pan encoder clicks
//
void CATRequestDisplayPan(void);
void SendDisplayPanClicks(void);

//
// request display zoom;
// display zoom encoder clicks
//
void CATRequestDisplayZoom(void);
void SendDisplayZoomClicks(void);

//
// request sub-RX AF gain;
// sub-RX AF gain encoder clicks
//
void CATRequestSubRXAFGain(void);
void SendSubRXAFGainClicks(void);

//
// request sub-RX stereo balance;
// sub-RX stereo balance encoder clicks
//
void CATRequestSubRXStereo(void);
void SendSubRXStereoClicks(void);

//
// request RX1 stereo balance;
// RX1 stereo balance encoder clicks
//
void CATRequestRX1Stereo(void);
void SendRX1StereoClicks(void);

//
// request RX2 stereo balance;
// RX2 stereo balance encoder clicks
//
void CATRequestRX2Stereo(void);
void SendRX2StereoClicks(void);

//
// handlers for pushbutton:
//
void CATHandlePushbutton(unsigned int Button, EButtonActions Action, bool IsPressed);

//
// encoder passes the following parameters: 
// encoder number 0-6);
// number of +/- clicks
// the programmed function the encoder is assigned to
//
void CATHandleEncoder(unsigned int Encoder, int Clicks, EEncoderActions AssignedAction);
void CATHandleVFOEncoder(int Clicks);


//
// handlers for received CAT commands
//
void HandleCATCommandStringParam(ECATCommands MatchedCAT, char* ParsedParam);
void HandleCATCommandNumParam(ECATCommands MatchedCAT, int ParsedParam);
void HandleCATCommandBoolParam(ECATCommands MatchedCAT, bool ParsedParam);
void HandleCATCommandNoParam(ECATCommands MatchedCAT);

//
// get the low, high erdges of "ideal" filter settings
// returns a frequency in Hz
//
int GetOptimumIFFilterLow(void);
int GetOptimumIFFilterHigh(void);

//
// helper to make a softkey message
// parameter is 1-8
//
void MakeSoftkeyMessage(int KeyNum, bool IsPressed);


#endif //not defined
