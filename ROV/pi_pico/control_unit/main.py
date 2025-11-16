import time
from machine import I2C, Pin
from libs.hardwareHandler import HardwareHandler
from libs.calculator import Calculator
from libs.configLoader import *
from libs.imu_wrapper import IMUWrapper
from libs.communicationHelperLib import *

def print_verbose(msg):
    if verbose:
        print(msg)
        
def print_as_hex(byte_array):
    msg = "Received data (hex):" + " ".join(f"{byte:02X}" for byte in byte_array)
    print_verbose(msg)

def task_one_herz():  # Execution every second
    global state, led_toggles
    if state == 0:  # Waiting init State
        led_toggles += 4
    elif state == 1:  # Execute mode State
        led_toggles += 2
    elif state == 2:  # Error mode State
        led_toggles += 6

def task_ten_herz():  # Execution every 100ms
    global led_toggles
    if led_toggles > 0:
        hardware.led.value(1 - hardware.led.value())  # Toggle LED
        led_toggles -= 1  # Decrement the toggle count
    else:
        hardware.led.value(0)  # Turn off the LED

def apply_manual_control(data):
    if hardware.mot_pwms[0] != None:
        motor_values = list(data[1:7])
        pwm_values = calculator.calc_all_motor_pwm2(motor_values)
        print_verbose(pwm_values)
        hardware.set_motor_speed_all(pwm_values)
        hardware.send_message_code_uart(0x02)

def apply_controller_values(data, use_dynamic_throttle):
    if hardware.mot_pwms[0] != None:
        controller_values = list(data[1:8])
        throttle_value = calculator.calc_throttle_value2(controller_values)
        if not use_dynamic_throttle:
            pwm_values = calculator.calc_all_motor_pwm(throttle_value, False)
            print("Static PWM value:" , pwm_values)
            
        else:
            pwm_values, force_values, current_values = calculator.dynamic_throttle_2_pwm(throttle_value)
            print("Dynamic PWM value:", pwm_values)

            
        hardware.set_motor_speed_all(pwm_values)

def init_motor_sequence():
    hardware.init_motor_all()
    print_verbose("Motors initialized")
    hardware.write_to_frontend([0xF3, 0x01])
    state = 1

def send_direction_values():
    print_verbose("Sending direction values")
    payload = get_direction_uart_values_payload(calculator.direction_values)

    for motor_values in payload:
        print_verbose(f"Sending: {motor_values}")
        hardware.write_to_frontend(bytes(motor_values))
        time.sleep(0.02)

def recieve_direction_values(data):
    if len(data) == 7 * 6:
        new_direction_values_matrix = []
        for i in range(6):
            new_direction_values_matrix.append(data[i * 7 + 1:i * 7 + 7])
            new_direction_values_matrix[i] = [calculator.byte_to_float(value) for value in new_direction_values_matrix[i]]

        update_direction_values(calculator.direction_values, new_direction_values_matrix)
        print(calculator.direction_values)
        write_json_file(direction_values_path, calculator.direction_values)
        hardware.send_message_code_uart(0x20)

def send_PID_values():
    print_verbose("Send PID values")
    byte_array = [
        0x31,
        convert_255_float_to_byte(calculator.Kp),
        convert_255_float_to_byte(calculator.Kd),
        0x01 if calculator.gyro_enabled else 0x00
    ]
    print_as_hex(byte_array)
    hardware.write_to_frontend(byte_array)

def recieve_PID_values(data):
    print_verbose("Receive PID values")
    if len(data) < 4:
        print_verbose("Error: Received data is too short.")
        return

    calculator.Kp = convert_byte_to_255_float(data[1])
    calculator.Kd = convert_byte_to_255_float(data[2])
    calculator.gyro_enabled = (data[3] == 0x01)
    print(f"Received PID values: Kp = {calculator.Kp:.2f}, Kd = {calculator.Kd:.2f}, Gyro Enabled = {calculator.gyro_enabled}")
    hardware.write_to_frontend([0x33])

def update_cycle_time(data):
    new_cycle_time = data[1]
    if new_cycle_time >= 2:
        global cycle_time
        cycle_time = new_cycle_time
    hardware.write_to_frontend([0xF9, cycle_time])

def init_connection_to_frontend():
    print("Not implemented yet: init_connection_to_frontend")

def reset_error():
    print("Reset Error")
    hardware.write_to_frontend([0xF7, 0x01])

# === MAIN PROGRAM ===
verbose = True
print_verbose("Starting initialization")
machine.freq(200000000)
direction_values_path = "directionValues.json"
dynamic_throttle_path = "dynamic_throttle_data.json"
direction_values_data = read_json_file(direction_values_path)
dynamic_throttle_data = read_json_file(dynamic_throttle_path)


state = 0
cycle_time = 20  # 20ms loop
hardware = HardwareHandler()

try:
    i2c_gyro = I2C(0, scl=Pin(17), sda=Pin(16), freq=400_000)
    i2c_mag = I2C(1, scl=Pin(19), sda=Pin(18), freq=400_000)
    gyro = IMUWrapper(i2c_gyro, i2c_mag)
except Exception as e:
    gyro = None
    print_verbose(f"Gyro init failed: {e}")

calculator = Calculator(direction_values_data, dynamic_throttle_data,gyro)

last_send_address = 0x00
last_execution_time_1000ms = time.ticks_ms()
last_execution_time_100ms = time.ticks_ms()
led_toggles = 0

print_verbose("Program started. Waiting for initialization...")

while True:
    start_time = time.ticks_ms()

    if time.ticks_diff(start_time, last_execution_time_1000ms) >= 1000:
        task_one_herz()
        last_execution_time_1000ms = start_time

    if time.ticks_diff(start_time, last_execution_time_100ms) >= 100:
        task_ten_herz()
        last_execution_time_100ms = start_time

    try:
        if hardware.uart.any():
            data = hardware.uart.read()
            if data:
                print_as_hex(data)
                command = data[0]
                if command == 0xF0:
                    init_connection_to_frontend()
                elif command == 0xF2:
                    init_motor_sequence()
                elif command == 0xF6:
                    reset_error()
                elif command == 0xE0:
                    apply_manual_control(data)
                elif command == 0xD0:
                    apply_controller_values(data, False)
                elif command == 0xC0:
                    apply_controller_values(data, True)
                elif command == 0x10:
                    send_direction_values()
                elif command == 0x21:
                    recieve_direction_values(data)
                elif command == 0x30:
                    send_PID_values()
                elif command == 0x32:
                    recieve_PID_values(data)
                elif command == 0xF8:
                    update_cycle_time(data)

    except Exception as e:
        print_verbose(f"[ERROR] {e.__class__.__name__}: {e}")
        state = 2
        hardware.send_message_code_uart(0x00)

    delta_time = time.ticks_diff(time.ticks_ms(), start_time)
    if delta_time < cycle_time:
        time.sleep_ms(cycle_time - delta_time)
    else:
        print_verbose(f"Warning: Cycle Time exceeded! - delay {-cycle_time + delta_time}ms")
        hardware.send_message_code_uart(0x03)
