# Pico-LouisControlEngine GitHub

**Table of Contents**

[[_TOC_]]

These micropython scripts run on the RaspberryPI Pico on the upper box to control the thrusters.

## main.py

- Gives the thrusters the values received via UART from the PC
- Handles all UART communication with the PC

## communicationHelperLib.py

- Handles the communication with the PC

## directionValues.json

- gives the offset applied to the motors of the ROV

## libs

### calculator.py

- calculates the value for each thruster

### configLoader.py

-Loads the config for the pico

### gyroscopeMPU6050.py

- Gets the values from the MPU6050

### hardwareHandler.py

- Handles hardware issues

## Changelog

- **17 April 2025**: Initial version of the document.
- **02 Mai 2025**: Fixed bug with applying manual values.