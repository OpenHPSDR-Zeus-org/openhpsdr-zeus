/**
********************************************************************************
* @file     analogConverter.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     26.2.2024
* @brief    Emulation IC MAX11613, SQUARE SDR bus I2C-1
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "analogConverter.h"

extern I2C_HandleTypeDef hi2c2;
extern ADC_HandleTypeDef hadc1;


/* Private define ------------------------------------------------------------*/
#define I2C2_RX_BUFF_SIZE   1
#define I2C2_TX_BUFF_SIZE   8

/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
uint8_t analogRXData[I2C2_RX_BUFF_SIZE] = {0x00};

uint8_t analogTXData[I2C2_TX_BUFF_SIZE] = {0xF0, 0x00,  /* REV */
                                           0xF3, 0x6F,  /* TEMP 20.0 */
                                           0xF0, 0x00,  /* IPA */
                                           0xF0, 0x00}; /* FWD */

uint8_t countADCData = 0;  //for Debug

uint16_t resultInternalVREF;

int16_t resultChannelREV;
int16_t resultChannelTEMP;
int16_t resultChannelIPA;
int16_t resultChannelFWD;

/* Private function prototypes -----------------------------------------------*/
void analogConverterListenCallBack(I2C_HandleTypeDef *hi2c);
void analogConverterRXCompleteCallBack(I2C_HandleTypeDef *hi2c);
void analogConverterAddressCallBack(I2C_HandleTypeDef *hi2c, uint8_t TransferDirection, uint16_t AddrMatchCode);
void analogConverterErrorCallBack(I2C_HandleTypeDef *hi2c);

/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterInit(void)
{
	// Do handleru hi2c2 zaregistruj CallBack funkce pro zarizeni I2C2
	HAL_I2C_RegisterCallback(&hi2c2, HAL_I2C_LISTEN_COMPLETE_CB_ID, analogConverterListenCallBack);
	HAL_I2C_RegisterCallback(&hi2c2, HAL_I2C_SLAVE_RX_COMPLETE_CB_ID, analogConverterRXCompleteCallBack);
	HAL_I2C_RegisterAddrCallback(&hi2c2, analogConverterAddressCallBack);
	HAL_I2C_RegisterCallback(&hi2c2, HAL_I2C_ERROR_CB_ID, analogConverterErrorCallBack);
  
  if (HAL_I2C_EnableListen_IT(&hi2c2) != HAL_OK)
  {
    Error_Handler();
  }
  
  /* Run the ADC calibration */
  if (HAL_ADCEx_Calibration_Start(&hadc1) != HAL_OK)
  {
    /* Calibration Error */
    Error_Handler();
  }
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterProcess(void)
{
  ADC_ChannelConfTypeDef sConfig = {0};
  
  sConfig.Rank = ADC_REGULAR_RANK_1;
  sConfig.SamplingTime = ADC_SAMPLINGTIME_COMMON_1;
  
  /**********************************************************************/
  /* Configure and Convert Regular Channel REV */
  sConfig.Channel = ADC_CHANNEL_6;
  if (HAL_ADC_ConfigChannel(&hadc1, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* Start ADC conversion */
  if (HAL_ADC_Start(&hadc1) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Wait for the ADC conversion to be completed (timeout unit: ms) */
  if (HAL_ADC_PollForConversion(&hadc1, 2) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Get ADC Value REV and filter */
  resultChannelREV += ((int16_t)(HAL_ADC_GetValue(&hadc1) << 3) - resultChannelREV) >> 5;
  
  /**********************************************************************/
  /* Configure and Convert Regular Channel TEMP */
  sConfig.Channel = ADC_CHANNEL_7;
  if (HAL_ADC_ConfigChannel(&hadc1, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* Start ADC conversion */
  if (HAL_ADC_Start(&hadc1) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Wait for the ADC conversion to be completed (timeout unit: ms) */
  if (HAL_ADC_PollForConversion(&hadc1, 2) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Get ADC Value TEMP and filter */
  resultChannelTEMP += ((int16_t)(HAL_ADC_GetValue(&hadc1) << 3) - resultChannelTEMP) >> 5;
  
  /**********************************************************************/
  /* Configure and Convert Regular Channel IPA */
  sConfig.Channel = ADC_CHANNEL_8;
  if (HAL_ADC_ConfigChannel(&hadc1, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* Start ADC conversion */
  if (HAL_ADC_Start(&hadc1) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Wait for the ADC conversion to be completed (timeout unit: ms) */
  if (HAL_ADC_PollForConversion(&hadc1, 2) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Get ADC Value IPA and filter */
  resultChannelIPA += ((int16_t)(HAL_ADC_GetValue(&hadc1) << 3) - resultChannelIPA) >> 5;

  /**********************************************************************/
  /* Configure and Convert Regular Channel FWD */
  sConfig.Channel = ADC_CHANNEL_9;
  if (HAL_ADC_ConfigChannel(&hadc1, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* Start ADC conversion */
  if (HAL_ADC_Start(&hadc1) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Wait for the ADC conversion to be completed (timeout unit: ms) */
  if (HAL_ADC_PollForConversion(&hadc1, 2) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Get ADC Value FWD and filter */
  resultChannelFWD += ((int16_t)(HAL_ADC_GetValue(&hadc1) << 3) - resultChannelFWD) >> 5;
  
  /**********************************************************************/
  /* Store Resuts to TX Data buffer */
  
  analogTXData[0] = ((uint8_t)(resultChannelREV >> (8 + 3))) | 0xF0;
  analogTXData[1] = (uint8_t)(resultChannelREV >> 3);
  
  analogTXData[2] = ((uint8_t)(resultChannelTEMP >> (8 + 3))) | 0xF0;
  analogTXData[3] = (uint8_t)(resultChannelTEMP >> 3);
  
  analogTXData[4] = ((uint8_t)(resultChannelIPA >> (8 + 3))) | 0xF0;
  analogTXData[5] = (uint8_t)(resultChannelIPA >> 3);
  
  analogTXData[6] = ((uint8_t)(resultChannelFWD >> (8 + 3))) | 0xF0;
  analogTXData[7] = (uint8_t)(resultChannelFWD >> 3);
  

  /**********************************************************************/
  /* Configure and Convert Internal voltage reference */

  sConfig.SamplingTime = ADC_SAMPLETIME_160CYCLES_5;
  sConfig.Channel = ADC_CHANNEL_VREFINT;
  if (HAL_ADC_ConfigChannel(&hadc1, &sConfig) != HAL_OK)
  {
    Error_Handler();
  }
  /* Start ADC conversion */
  if (HAL_ADC_Start(&hadc1) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Wait for the ADC conversion to be completed (timeout unit: ms) */
  if (HAL_ADC_PollForConversion(&hadc1, 2) != HAL_OK)
  {
    /* ADC conversion start error */
    Error_Handler();
  }
  /* Get ADC Value Internal voltage reference */
  resultInternalVREF = (uint16_t)(HAL_ADC_GetValue(&hadc1));

}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval ChannelTEMP
*/
uint16_t analogConverterReadChannelTEMP(void)
{
  return ((resultChannelTEMP >> 3) | 0xF000);
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval ChannelTEMP
*/
uint16_t analogConverterReadInternalVREF(void)
{
  return resultInternalVREF;
}

/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterListenCallBack(I2C_HandleTypeDef *hi2c)
{
	HAL_I2C_EnableListen_IT(hi2c);
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterRXCompleteCallBack(I2C_HandleTypeDef *hi2c)
{
  countADCData++;
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterAddressCallBack(I2C_HandleTypeDef *hi2c, uint8_t TransferDirection, uint16_t AddrMatchCode)
{
	if(TransferDirection == I2C_DIRECTION_TRANSMIT)  // if the master wants to transmit the data
	{
		HAL_I2C_Slave_Sequential_Receive_IT(hi2c, analogRXData, 1, I2C_FIRST_AND_LAST_FRAME);
	}
	else  // master requesting the data
	{
		HAL_I2C_Slave_Sequential_Transmit_IT(hi2c, analogTXData, 8, I2C_FIRST_AND_LAST_FRAME);
	}
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void analogConverterErrorCallBack(I2C_HandleTypeDef *hi2c)
{
	HAL_I2C_EnableListen_IT(hi2c);
}

/******************************************************************************/
/**
* End Of File analogConverter.c
*/
