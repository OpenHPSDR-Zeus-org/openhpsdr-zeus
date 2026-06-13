/**
********************************************************************************
* @file     I2C1.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     8.8.2024
* @brief    I2C driver, SQUARE SDR bus I2C-2 (Emulation of ICs MCP23008)
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "I2C1.h"
#include "filterSwitch.h"
#include "bandVoltage.h"


extern I2C_HandleTypeDef hi2c1;

/* Private define ------------------------------------------------------------*/
/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
uint8_t i2c1RXData[I2C1_RX_BUFF_SIZE] = {0x00, 0x00};
uint8_t i2c1TXData[I2C1_TX_BUFF_SIZE] = {0x00, 0x00, 0x00, 0x00};

uint8_t i2c1DataCounter = 0;  //for Debug


/* Private function prototypes -----------------------------------------------*/
void i2c1ListenCallBack(I2C_HandleTypeDef *hi2c);
void i2c1RXCompleteCallBack(I2C_HandleTypeDef *hi2c);
void i2c1AddressCallBack(I2C_HandleTypeDef *hi2c, uint8_t TransferDirection, uint16_t AddrMatchCode);
void i2c1ErrorCallBack(I2C_HandleTypeDef *hi2c);

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
void i2c1Init(void)
{
	// Do handleru hi2c1 zaregistruj CallBack funkce pro zarizeni I2C1
	HAL_I2C_RegisterCallback(&hi2c1, HAL_I2C_LISTEN_COMPLETE_CB_ID, i2c1ListenCallBack);
	HAL_I2C_RegisterCallback(&hi2c1, HAL_I2C_SLAVE_RX_COMPLETE_CB_ID, i2c1RXCompleteCallBack);
	HAL_I2C_RegisterAddrCallback(&hi2c1, i2c1AddressCallBack);
	HAL_I2C_RegisterCallback(&hi2c1, HAL_I2C_ERROR_CB_ID, i2c1ErrorCallBack);
  
  if (HAL_I2C_EnableListen_IT(&hi2c1) != HAL_OK)
  {
    Error_Handler();
  }
}

/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void i2c1ListenCallBack(I2C_HandleTypeDef *hi2c)
{
	HAL_I2C_EnableListen_IT(hi2c);
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void i2c1RXCompleteCallBack(I2C_HandleTypeDef *hi2c)
{
  i2c1DataCounter++;
  
  if (i2c1RXData[0] == 0x0a) //MCP23008 Address Register OLAT
  {
    filterSwitchWriteProcess();
  }
  
  if (i2c1RXData[0] >= 0xb0 && i2c1RXData[0] <= 0xb9) //BVO Address Register
  {
    bandVoltageWriteSetting();
  }
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void i2c1AddressCallBack(I2C_HandleTypeDef *hi2c, uint8_t TransferDirection, uint16_t AddrMatchCode)
{
	if(TransferDirection == I2C_DIRECTION_TRANSMIT)  // if the master wants to transmit the data
	{
		HAL_I2C_Slave_Sequential_Receive_IT(hi2c, i2c1RXData, 2, I2C_FIRST_AND_LAST_FRAME);
	}
	else  // master requesting the data
	{
    i2c1TXData[0] = 0;
    i2c1TXData[1] = 0;
    i2c1TXData[2] = 0;
    i2c1TXData[3] = 0;
    
    if (i2c1RXData[0] == 0x0a) //MCP23008 Address Register OLAT
    {
      filterSwitchReadProcess();
    }
    
    if (i2c1RXData[0] >= 0xb0 && i2c1RXData[0] <= 0xb9) //BVO Address Register
    {
      bandVoltageReadSetting();
    }
    
    HAL_I2C_Slave_Sequential_Transmit_IT(hi2c, i2c1TXData, 4, I2C_FIRST_AND_LAST_FRAME);
	}
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void i2c1ErrorCallBack(I2C_HandleTypeDef *hi2c)
{
	HAL_I2C_EnableListen_IT(hi2c);
}

/******************************************************************************/
/**
* End Of File I2C1.c
*/
