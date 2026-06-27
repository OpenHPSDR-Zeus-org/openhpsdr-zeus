// ----------------------------------------------------------------------------
// Rotary Encoder Driver based on ClickEncoder 
//
// (c) 2010 karl@pitrich.com
// (c) 2014 karl@pitrich.com
// 
// periodic-update based rotary encoder logic by Peter Dannegger; simplified Laurence Barker G8NJJ
// ----------------------------------------------------------------------------

#include "mechencoder2.h"

// ----------------------------------------------------------------------------

NoClickEncoder2::NoClickEncoder2(uint8_t stepsPerNotch, byte InitialState, bool active)
  : delta(0), last(0), steps(stepsPerNotch), pinsActive(active)
{
  if ((bool)(InitialState &0b1) == pinsActive)
    last = 3;

  if ((bool)(InitialState &0b10) == pinsActive)
    last ^=1;
}


// ----------------------------------------------------------------------------
// call this every 1 millisecond via timer ISR
//
void NoClickEncoder2::service(byte BitState)                           // bit 0 = A; bit 1 = B  
{
  bool moved = false;

  int8_t curr = 0;

  if ((bool)(BitState &0b1) == pinsActive) 
    curr = 3;

  if ((bool)(BitState &0b10) == pinsActive) 
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

int16_t NoClickEncoder2::getValue(void)
{
  int16_t val;
  
//  noInterrupts();
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
//  interrupts();

  return r;
}
