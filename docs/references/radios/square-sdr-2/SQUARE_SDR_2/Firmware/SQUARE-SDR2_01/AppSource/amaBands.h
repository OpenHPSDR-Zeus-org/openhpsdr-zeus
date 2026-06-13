/**
********************************************************************************
* @file     amaBands.h
* @author   Rene Siroky
* @version  V1.0.0
* @date     1.11.2025
* @brief    Header file
********************************************************************************
* @note
********************************************************************************
*/
/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __AMABANDS_H__
#define __AMABANDS_H__
#ifdef __cplusplus
extern "C" {
#endif
  /* Includes ----------------------------------------------------------------*/
#include "main.h"
  /* User define -------------------------------------------------------------*/
#define AMA_NUM_BANDS       10   /* Number of Bands */
  
  /* Exported types ----------------------------------------------------------*/
  /* Exported constants ------------------------------------------------------*/
  extern const uint16_t ConstBandLowLimits[AMA_NUM_BANDS];
  extern const uint16_t ConstBandUpLimits[AMA_NUM_BANDS];
  
  /* Exported macro ----------------------------------------------------------*/
  /* Exported functions ------------------------------------------------------*/
  uint8_t getBandIndex(uint16_t frq);
  
  /****************************************************************************/
  /* Public Functions Called from Interrupt Service Routines -----------------*/
  /****************************************************************************/
#ifdef __cplusplus
}
#endif
#endif /* __AMABANDS_H__ */
/******************************************************************************/
/**
* End Of File amaBands.h
*/