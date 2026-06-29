// ----------------------------------------------------------------------------
// Rotary Encoder Driver based on ClickEncoder 
//
// (c) 2010 karl@pitrich.com
// (c) 2014 karl@pitrich.com
// 
// Timer-based rotary encoder logic by Peter Dannegger
// ----------------------------------------------------------------------------

#include "mechencoder.h"

// ----------------------------------------------------------------------------

NoClickEncoder::NoClickEncoder(int8_t A, int8_t B, uint8_t stepsPerNotch, bool active)
  : delta(0), last(0), steps(stepsPerNotch),
    pinA(A), pinB(B), pinsActive(active)
{
  pinMode_t configType = (pinsActive == LOW) ? INPUT_PULLUP : INPUT;
  if (pinA >= 0) {pinMode(pinA, configType);}
  if (pinB >= 0) {pinMode(pinB, configType);}
  
  if (digitalRead(pinA) == pinsActive) {
    last = 3;
  }

  if (digitalRead(pinB) == pinsActive) {
    last ^=1;
  }
}


// ----------------------------------------------------------------------------
// call this every 1 millisecond via timer ISR
//
void NoClickEncoder::service(void)
{
  bool moved = false;

  int8_t curr = 0;

  if (digitalRead(pinA) == pinsActive) 
    curr = 3;

  if (digitalRead(pinB) == pinsActive) 
    curr ^= 1;
  
  int8_t diff = last - curr;
  if (diff & 1) 
  {            // bit 0 = step
    last = curr;
    delta += (diff & 2) - 1; // bit 1 = direction (+/-)
    moved = true;    
  }
}

// ----------------------------------------------------------------------------

int16_t NoClickEncoder::getValue(void)
{
  int16_t val;
  
  noInterrupts();
  val = delta;

  if (steps == 2) delta = val & 1;
  else if (steps == 4) delta = val & 3;
  else delta = 0; // default to 1 step per notch

  if (steps == 4) val >>= 2;
  if (steps == 2) val >>= 1;

  int16_t r = 0;

  if (val < 0) 
    r =- 1;
  else if (val > 0) 
    r = 1;
  interrupts();

  return r;
}


