from machine import UART, Pin, PWM
import time
import asyncio

class HardwareHandler:
    def __init__(self):
        # Motor setup
        self.mot_pwm_pins = [8, 2, 4, 6, 0, 10]
        self.mot_pwms = [None] * len(self.mot_pwm_pins)
        
        # UART setup
        self.uart = UART(0, baudrate=115200, tx=Pin(12), rx=Pin(13))

        # LED setup
        self.led = Pin(25, Pin.OUT)
        
        self.last_message_sent = None

    def set_motor_speed_all(self, pwm_values):
        """Set speed for all motors based on throttle input."""
        for i in range(len(self.mot_pwms)):
            self.mot_pwms[i].duty_u16(int(pwm_values[i] * 65535 / 20000))
            
    def send_message_code_uart(self, message_code):
        if self.last_message_sent is not message_code:
            self.uart.write(bytes([message_code] * 7))
            last_message_sent = message_code
            
    def init_motor_all(self):
        """Initialize all motors."""
        for i in range(len(self.mot_pwm_pins)):
            self.mot_pwms[i] = PWM(Pin(self.mot_pwm_pins[i]))
            self.mot_pwms[i].freq(50)
        self.set_motor_speed_all([1500]*6)
        
    def write_to_frontend(self, byte_array):
        # Pad the byte_array with 0x00 until its length is 7
        while len(byte_array) < 7:
            byte_array.append(0x00)
        payload = bytes(byte_array)
        self.uart.write(payload)
        print_as_hex(payload)
        
    
def print_as_hex(byte_array):
    msg = "Sent data (hex):" + " ".join(f"{byte:02X}" for byte in byte_array)
    print(msg)



        

