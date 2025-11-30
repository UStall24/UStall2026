from machine import Pin, PWM
import machine
import time
import asyncio
from libs.rp_2350_can import RP2350_CAN
# Do not use 1,8,9,10,11,12 for other purposes as they are used for CAN SPI

class HardwareHandler:
    def __init__(self):
        machine.freq(200_000_000)
        # Motor setup
        self.mot_pwm_pins = [2,3,4,5,6,7]
        self.mot_pwms = [None] * len(self.mot_pwm_pins)
        
        # UART setup
        self.can = RP2350_CAN() 
        # LED setup
        self.led = Pin(25, Pin.OUT)
        
        self.last_message_sent = None

    def set_motor_speed_all(self, pwm_values):
        """Set speed for all motors based on throttle input."""
        for i in range(len(self.mot_pwms)):
            self.mot_pwms[i].duty_u16(int(pwm_values[i] * 65535 / 20000))
            # print(f"M{i}: {pwm_values[i]}us", end=' ')
             
    def init_motor_all(self):
        """Initialize all motors."""
        for i in range(len(self.mot_pwm_pins)):
            self.mot_pwms[i] = PWM(Pin(self.mot_pwm_pins[i]))
            self.mot_pwms[i].freq(50)
        self.set_motor_speed_all([1500]*6)
    
    def sendcan(self, rxId, data=[]):
        self.can.send(rxId, data)
    
    def recvcan(self):
        return self.can.recv()
    



        

