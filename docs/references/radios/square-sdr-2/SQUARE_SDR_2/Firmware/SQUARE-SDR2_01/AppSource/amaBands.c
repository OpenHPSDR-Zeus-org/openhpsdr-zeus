/**
********************************************************************************
* @file     amaBands.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     1.11.2025
* @brief    Define Amateurs Bands
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "amaBands.h"

/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* lower limits[kHz] of the bands */
const uint16_t ConstBandLowLimits[AMA_NUM_BANDS] = {
  /*160m  80m   60m   40m   30m   20m    17m    15m    12m    10m */  
  1000, 2751, 4501, 6001, 8501, 13001, 16001, 19501, 23001, 26001
};
/* upper limits[kHz] of the bands */
const uint16_t ConstBandUpLimits[AMA_NUM_BANDS] = {
  /*160m  80m   60m   40m    30m   20m    17m    15m    12m    10m */  
  2750, 4500, 6000, 8500, 13000, 16000, 19500, 23000, 26000, 30000
};

/* Private variables ---------------------------------------------------------*/
/* Private function prototypes -----------------------------------------------*/
/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/******************************************************************************/
/**
* @brief  getBandIndex
* @param  FRQ in kHz
* @retval None
*/
uint8_t getBandIndex(uint16_t frq)
{
  for (uint8_t i = 0; i < AMA_NUM_BANDS; i++)
  {
    if ((frq >= ConstBandLowLimits[i]) && (frq <= ConstBandUpLimits[i]))
    {
      return i; // return 0 to Number of Bands-1
    }
  }
  return AMA_NUM_BANDS; // no valid band found -> return Number of Bands
}
/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
/******************************************************************************/
/**
* End Of File amaBands.c
*/
