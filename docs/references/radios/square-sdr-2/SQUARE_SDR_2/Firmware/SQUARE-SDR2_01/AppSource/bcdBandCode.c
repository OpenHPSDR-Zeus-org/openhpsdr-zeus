/**
********************************************************************************
* @file     bcdBandCode.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     1.11.2025
* @brief    BCD Band Code generation (Yaesu standard)
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "bcdBandCode.h"
#include "amaBands.h"

/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* BCD code of the bands (Yaesu standard) */
const uint8_t ConstBcdBandcode[AMA_NUM_BANDS + 1] = {
  /*160m 80m 60m 40m 30m 20m 17m 15m 12m 10m outOfBand*/  
    1,   2,  3,  3,  4,  5,  6,  7,  8,  9,  0
};

/* Private variables ---------------------------------------------------------*/
/* Private function prototypes -----------------------------------------------*/
/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  bandBcdCodeSet
* @param  bcd
* @retval None
*/
void bandBcdCodeSet(uint8_t bcd)
{
  // Bit A = PC15  (negovaný)
  HAL_GPIO_WritePin(GPIOC, GPIO_PIN_15, (bcd & 0x01) ? GPIO_PIN_RESET : GPIO_PIN_SET);
  
  // Bit B = PA4   (negovaný)
  HAL_GPIO_WritePin(GPIOA, GPIO_PIN_4,  (bcd & 0x02) ? GPIO_PIN_RESET : GPIO_PIN_SET);
  
  // Bit C = PA5   (negovaný)
  HAL_GPIO_WritePin(GPIOA, GPIO_PIN_5,  (bcd & 0x04) ? GPIO_PIN_RESET : GPIO_PIN_SET);
  
  // Bit D = PC14  (negovaný)
  HAL_GPIO_WritePin(GPIOC, GPIO_PIN_14, (bcd & 0x08) ? GPIO_PIN_RESET : GPIO_PIN_SET);
}

/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  bcdBandCodeInit
* @param  None
* @retval None
*/
void bcdBandCodeInit(void)
{
  bandBcdCodeSet(0);
}

/******************************************************************************/
/**
* @brief  bcdBandCodeProcess
* @param  FRQ in kHz
* @retval None
*/
void bcdBandCodeProcess(uint16_t frq)
{
  bandBcdCodeSet(ConstBcdBandcode[getBandIndex(frq)]); // Set BCD code
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
* End Of File bcdBandCode.c
*/
