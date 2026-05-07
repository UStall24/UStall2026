from machine import Pin, PWM
import machine
import time
from libs.rp_2350_can import RP2350_CAN


class HardwareHandler:
    def __init__(self, verbose=True):
        machine.freq(200_000_000)

        self.mot_pwm_pins = [2, 3, 4, 5, 6, 7]
        self.mot_pwms = [None] * len(self.mot_pwm_pins)

        self.can = RP2350_CAN()
        self.led = Pin(25, Pin.OUT)

        self.last_message_sent = None
        self._last_print_time = time.ticks_ms()

        self.motor_initialized = False
        self.verbose = verbose

    def set_verbose(self, value: bool):
        self.verbose = bool(value)

    def _print(self, msg):
        if self.verbose:
            print(msg)

    def set_motor_speed_all(self, pwm_values):
        if not self.motor_initialized:
            raise RuntimeError("Motors are not initialized. Call init_motor_all() first.")

        self._print(pwm_values)

        for i in range(len(self.mot_pwms)):
            self.mot_pwms[i].duty_u16(int(pwm_values[i] * 65535 / 20000))
        
        periodic_print = False
        if periodic_print:
            now = time.ticks_ms()
            if time.ticks_diff(now, self._last_print_time) >= 1000:
                if self.verbose:
                    for i in range(len(pwm_values)):
                        print(f"M{i}: {pwm_values[i]}us", end=' ')
                    print("")
                self._last_print_time = now

    def init_motor_all(self):
        self.motor_initialized = True

        for i in range(len(self.mot_pwm_pins)):
            self.mot_pwms[i] = PWM(Pin(self.mot_pwm_pins[i]))
            self.mot_pwms[i].freq(50)

        self.set_motor_speed_all([1500] * 6)

    def sendcan(self, rxId, data=[]):
        self.can.send(rxId, data)

    def recvcan(self):
        return self.can.recv()