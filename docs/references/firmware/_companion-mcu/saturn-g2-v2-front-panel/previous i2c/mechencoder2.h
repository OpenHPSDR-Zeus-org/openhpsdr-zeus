// ----------------------------------------------------------------------------
// Rotary Encoder Driver based on ClickEncoder 
//
// (c) 2010 karl@pitrich.com
// (c) 2014 karl@pitrich.com
// 
// Timer-based rotary encoder logic by Peter Dannegger
// ----------------------------------------------------------------------------

#ifndef __have__NoClickEncoder2_h__
#define __have__NoClickEncoder2_h__

#include "Arduino.h"

// ----------------------------------------------------------------------------

#define ENC_NORMAL        (1 << 1)   // use Peter Danneger's decoder
#define ENC_FLAKY         (1 << 2)   // use Table-based decoder

// ----------------------------------------------------------------------------

#ifndef ENC_DECODER
#  define ENC_DECODER     ENC_NORMAL
#endif

// ----------------------------------------------------------------------------
  typedef uint8_t pinMode_t;

class NoClickEncoder2
{
public:

public:
  NoClickEncoder2(uint8_t stepsPerNotch = 4, byte InitialState=0, bool active = LOW);

  void service(byte BitState);                           // bit 0 = A; bit 1 = B  
  int16_t getValue(void);


public:
protected:
  bool pinsActive;
  volatile int16_t delta;
  volatile int16_t last;
  volatile uint8_t steps;
};


// ----------------------------------------------------------------------------

#endif // __have__NoClickEncoder_h__
