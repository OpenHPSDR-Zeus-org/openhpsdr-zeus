/**
********************************************************************************
* @file     bandVoltage.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     11.8.2024
* @brief    Band Voltage generation for PA XPA125B at a PWM frequency of 5 kHz
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "bandVoltage.h"
#include "amaBands.h"
#include "analogConverter.h"
#include "USART2.h"
#include "I2C1.h"

extern TIM_HandleTypeDef htim2;
extern uint8_t i2c1RXData[I2C1_RX_BUFF_SIZE];
extern uint8_t i2c1TXData[I2C1_TX_BUFF_SIZE];


/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Voltage of the bands (for XPA125B) */
const uint16_t ConstBandVoltage[AMA_NUM_BANDS] = {
/*160m 80m  60m  40m  30m   20m   17m   15m   12m   10m */  
  230, 460, 690, 920, 1150, 1380, 1610, 1840, 2070, 2300
};

/* Private variables ---------------------------------------------------------*/
uint16_t bandVoltage[AMA_NUM_BANDS + 1];

/* Private function prototypes -----------------------------------------------*/
void bandVoltageSet(uint16_t voltage);

/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  bandVoltageSet
* @param  Required voltage in mV
* @retval None
*/
void bandVoltageSet(uint16_t voltage)
{
  uint16_t corrFactor = voltage / 27;
  
  uint16_t vddaVoltage = __HAL_ADC_CALC_VREFANALOG_VOLTAGE(analogConverterReadInternalVREF(), ADC_RESOLUTION_12B);
  uint32_t setValueCCR = (((uint32_t)voltage << 16) / vddaVoltage) * (uint32_t)__HAL_TIM_GET_AUTORELOAD(&htim2) >> 16;
  __HAL_TIM_SET_COMPARE(&htim2, TIM_CHANNEL_4, (uint16_t)setValueCCR - corrFactor);
}

/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  bandVoltageInit
* @param  None
* @retval None
*/
void bandVoltageInit(void)
{
  /* BVO settings for PA XPA125B */
  for (uint8_t i = 0; i < AMA_NUM_BANDS; i++)
  {
    bandVoltage[i] = ConstBandVoltage[i];
  }
  
  /* Set BVO for out of band  (0V) */
  bandVoltage[AMA_NUM_BANDS] = 0;
  
  /* Set BVO 0V */
  __HAL_TIM_SET_COMPARE(&htim2, TIM_CHANNEL_4, 0);
  
  /* Start channel 3 */
  if (HAL_TIM_PWM_Start(&htim2, TIM_CHANNEL_4) != HAL_OK)
  {
    /* PWM Generation Error */
    Error_Handler();
  }
}  

/******************************************************************************/
/**
* @brief  bandVoltageProcess
* @param  FRQ in kHz
* @retval None
*/
void bandVoltageProcess(uint16_t frq)
{
  bandVoltageSet(bandVoltage[getBandIndex(frq)]); // Set Band Voltage
}

/******************************************************************************/
/**
* @brief  bandVoltageWriteSetting
* @param  None
* @retval None
*/
void bandVoltageWriteSetting(void)
{
  if (i2c1RXData[0] == 0xb0) bandVoltage[0] = i2c1RXData[1] * 10;      // 160m
  else if (i2c1RXData[0] == 0xb1) bandVoltage[1] = i2c1RXData[1] * 10; // 80m
  else if (i2c1RXData[0] == 0xb2) bandVoltage[2] = i2c1RXData[1] * 10; // 60m
  else if (i2c1RXData[0] == 0xb3) bandVoltage[3] = i2c1RXData[1] * 10; // 40m
  else if (i2c1RXData[0] == 0xb4) bandVoltage[4] = i2c1RXData[1] * 10; // 30m
  else if (i2c1RXData[0] == 0xb5) bandVoltage[5] = i2c1RXData[1] * 10; // 20m
  else if (i2c1RXData[0] == 0xb6) bandVoltage[6] = i2c1RXData[1] * 10; // 17m
  else if (i2c1RXData[0] == 0xb7) bandVoltage[7] = i2c1RXData[1] * 10; // 15m
  else if (i2c1RXData[0] == 0xb8) bandVoltage[8] = i2c1RXData[1] * 10; // 12m
  else if (i2c1RXData[0] == 0xb9) bandVoltage[9] = i2c1RXData[1] * 10; // 10m
}

/******************************************************************************/
/**
* @brief  bandVoltageReadSetting
* @param  None
* @retval None
*/
void bandVoltageReadSetting(void)
{
  if (i2c1RXData[0] == 0xb0) i2c1TXData[0] = bandVoltage[0] / 10;      // 160m
  else if (i2c1RXData[0] == 0xb1) i2c1TXData[0] = bandVoltage[1] / 10; // 80m
  else if (i2c1RXData[0] == 0xb2) i2c1TXData[0] = bandVoltage[2] / 10; // 60m
  else if (i2c1RXData[0] == 0xb3) i2c1TXData[0] = bandVoltage[3] / 10; // 40m
  else if (i2c1RXData[0] == 0xb4) i2c1TXData[0] = bandVoltage[4] / 10; // 30m
  else if (i2c1RXData[0] == 0xb5) i2c1TXData[0] = bandVoltage[5] / 10; // 20m
  else if (i2c1RXData[0] == 0xb6) i2c1TXData[0] = bandVoltage[6] / 10; // 17m
  else if (i2c1RXData[0] == 0xb7) i2c1TXData[0] = bandVoltage[7] / 10; // 15m
  else if (i2c1RXData[0] == 0xb8) i2c1TXData[0] = bandVoltage[8] / 10; // 12m
  else if (i2c1RXData[0] == 0xb9) i2c1TXData[0] = bandVoltage[9] / 10; // 10m
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
* End Of File bandVoltage.c
*/
