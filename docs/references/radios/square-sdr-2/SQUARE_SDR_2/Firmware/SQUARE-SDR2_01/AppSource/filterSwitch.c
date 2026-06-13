/**
********************************************************************************
* @file     filterSwitch.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     8.8.2024
* @brief    Emulation of ICs MCP23008 and 74LVC00 on the N2ADR filter board
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "filterSwitch.h"
#include "I2C1.h"

extern uint8_t i2c1RXData[I2C1_RX_BUFF_SIZE];
extern uint8_t i2c1TXData[I2C1_TX_BUFF_SIZE];

/* Private define ------------------------------------------------------------*/
#define FILTER_DATA_B160    ((uint8_t)0x01) /* Data OLAT for select band 160M */
#define FILTER_DATA_B80     ((uint8_t)0x02) /* Data OLAT for select band 80M  */
#define FILTER_DATA_B40     ((uint8_t)0x04) /* Data OLAT for select band 40M  */
#define FILTER_DATA_B20     ((uint8_t)0x08) /* Data OLAT for select band 20M  */
#define FILTER_DATA_B15     ((uint8_t)0x10) /* Data OLAT for select band 15M  */
#define FILTER_DATA_B10     ((uint8_t)0x20) /* Data OLAT for select band 10M  */
#define FILTER_DATA_HPF     ((uint8_t)0x40) /* Data OLAT for deselect RX HPF  */

/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
uint8_t mOLAT = 0;
uint8_t stateTX = 0;
uint8_t countTXUp = 0;        //for Debug
uint8_t countTXDown = 0;      //for Debug

/* Private function prototypes -----------------------------------------------*/
/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void filterSwitchWriteProcess(void)
{
  // Check pin status PA_INTTR_Pin (signal TX)
  if(HAL_GPIO_ReadPin (PA_INTTR_GPIO_Port, PA_INTTR_Pin))
  {
    stateTX = 1;
  }
  else
  {
    stateTX = 0;
  }
  
  // Emulation Expander Chip MCP23008 for select/deselect bands 160-10M
  if (i2c1RXData[0] == 0x0a) //MCP23008 Address Register OLAT
  {
    // Save the content OLAT
    mOLAT = i2c1RXData[1];
    
    // 160M
    if (i2c1RXData[1] & FILTER_DATA_B160)
    {
      HAL_GPIO_WritePin(EXP_160_GPIO_Port, EXP_160_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_160_GPIO_Port, EXP_160_Pin, GPIO_PIN_RESET);
    }
    
    // 80M
    if (i2c1RXData[1] & FILTER_DATA_B80)
    {
      HAL_GPIO_WritePin(EXP_80_GPIO_Port, EXP_80_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_80_GPIO_Port, EXP_80_Pin, GPIO_PIN_RESET);
    }
    
    // 40M
    if (i2c1RXData[1] & FILTER_DATA_B40)
    {
      HAL_GPIO_WritePin(EXP_40_GPIO_Port, EXP_40_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_40_GPIO_Port, EXP_40_Pin, GPIO_PIN_RESET);
    }
    
    // 20M
    if (i2c1RXData[1] & FILTER_DATA_B20)
    {
      HAL_GPIO_WritePin(EXP_20_GPIO_Port, EXP_20_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_20_GPIO_Port, EXP_20_Pin, GPIO_PIN_RESET);
    }
    
    // 15M
    if (i2c1RXData[1] & FILTER_DATA_B15)
    {
      HAL_GPIO_WritePin(EXP_15_GPIO_Port, EXP_15_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_15_GPIO_Port, EXP_15_Pin, GPIO_PIN_RESET);
    }
    
    // 10M
    if (i2c1RXData[1] & FILTER_DATA_B10)
    {
      HAL_GPIO_WritePin(EXP_10_GPIO_Port, EXP_10_Pin, GPIO_PIN_SET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_10_GPIO_Port, EXP_10_Pin, GPIO_PIN_RESET);
    }
    
    i2c1RXData[0] = 0x00;
  }
  
  // Emulation NAND Gate 74LVC00 + MCP23008 for select/deselect RX HPF
  if (stateTX)
  {
    HAL_GPIO_WritePin(EXP_HPF_GPIO_Port, EXP_HPF_Pin, GPIO_PIN_SET);
  }
  else
  {
    if (i2c1RXData[1] & FILTER_DATA_HPF)
    {
      HAL_GPIO_WritePin(EXP_HPF_GPIO_Port, EXP_HPF_Pin, GPIO_PIN_RESET);
    }
    else
    {
      HAL_GPIO_WritePin(EXP_HPF_GPIO_Port, EXP_HPF_Pin, GPIO_PIN_SET);
    }
  }
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void filterSwitchReadProcess(void)
{
  i2c1TXData[0] = mOLAT;
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void filterSwitchInit(void)
{
  filterSwitchWriteProcess();
}

/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void HAL_GPIO_EXTI_Rising_Callback(uint16_t GPIO_Pin)
{
  if (GPIO_Pin == PA_INTTR_Pin)
  {
    countTXUp++;
    filterSwitchWriteProcess();
  }
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void HAL_GPIO_EXTI_Falling_Callback(uint16_t GPIO_Pin)
{
  if (GPIO_Pin == PA_INTTR_Pin)
  {
    countTXDown++;
    filterSwitchWriteProcess();
  }
}

/******************************************************************************/
/**
* End Of File filterSwitch.c
*/
