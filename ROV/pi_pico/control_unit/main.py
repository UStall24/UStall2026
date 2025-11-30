import time
from machine import I2C, Pin
from libs.hardwareHandler import HardwareHandler
from libs.calculator import Calculator
from libs.configLoader import *
from libs.imu_wrapper import IMUWrapper
from libs.communicationHelperLib import *

# ----------------------------------------------------------------------
# Helper printing functions
# ----------------------------------------------------------------------
def print_verbose(msg):
    if verbose:
        print(msg)

def print_as_hex(byte_array):
    msg = "Received data (hex): " + " ".join(f"{b:02X}" for b in byte_array)
    print_verbose(msg)

def print_can_msg(rxId, data):
    msg = " ".join(f"{b:02X}" for b in data)
    print(f"RxId: {rxId} - Data: {msg}")


# ----------------------------------------------------------------------
# Task timing (1 Hz / 10 Hz)
# ----------------------------------------------------------------------
def task_one_hz():                     # runs every second
    global state, led_toggles
    if state == 0:          # waiting for init
        led_toggles += 4
    elif state == 1:        # normal operation
        led_toggles += 2
    elif state == 2:        # error state
        led_toggles += 6

def task_ten_hz():                     # runs every 100 ms
    global led_toggles
    if led_toggles > 0:
        hardware.led.value(1 - hardware.led.value())
        led_toggles -= 1
    else:
        hardware.led.value(0)

# ----------------------------------------------------------------------
# Command handlers (receive side)
# ----------------------------------------------------------------------
def apply_manual_control(data):
    if hardware.mot_pwms[0] is not None:
        motor_vals = list(data[1:7])
        pwm_vals = calculator.calc_all_motor_pwm2(motor_vals)
        print_verbose(pwm_vals)
        hardware.set_motor_speed_all(pwm_vals)
        hardware.send_message_code_uart(0x02)

def apply_controller_values(data, use_dynamic_throttle):
    if hardware.mot_pwms[0] is not None:
        ctrl_vals = list(data[1:8])
        throttle = calculator.calc_throttle_value2(ctrl_vals)

        if not use_dynamic_throttle:
            pwm_vals = calculator.calc_all_motor_pwm(throttle, False)
            print("Static PWM value:", pwm_vals)
        else:
            pwm_vals, _, _ = calculator.dynamic_throttle_2_pwm(throttle)
            print("Dynamic PWM value:", pwm_vals)

        hardware.set_motor_speed_all(pwm_vals)

def init_motor_sequence():
    hardware.init_motor_all()
    print_verbose("Motors initialized")
    hardware.sendcan(SendAddr.InitThrusters, [0x01])
    global state
    state = 1

def send_direction_values():
    print_verbose("Sending direction values")
    payload = get_direction_uart_values_payload(calculator.direction_values)

    for motor_vals in payload:
        print_verbose(f"Sending: {motor_vals}")
        hardware.write_to_frontend(bytes(motor_vals))
        time.sleep(0.02)

def receive_direction_values(data):
    if len(data) == 7 * 6:
        matrix = []
        for i in range(6):
            vals = data[i * 7 + 1:i * 7 + 7]
            matrix.append([calculator.byte_to_float(v) for v in vals])

        update_direction_values(calculator.direction_values, matrix)
        print(calculator.direction_values)
        write_json_file(direction_values_path, calculator.direction_values)
        hardware.write_to_frontend([RecvAddr.PullDirectionValues_Response])

def send_PID_values():
    print_verbose("Send PID values")
    payload = [
        SendAddr.PullPidValues,                                   # request
        convert_255_float_to_byte(calculator.Kp),
        convert_255_float_to_byte(calculator.Kd),
        0x01 if calculator.gyro_enabled else 0x00
    ]
    print_as_hex(payload)
    hardware.write_to_frontend(payload)

def receive_PID_values(data):
    print_verbose("Receive PID values")
    if len(data) < 4:
        print_verbose("Error: Received data is too short.")
        return

    calculator.Kp = convert_byte_to_255_float(data[1])
    calculator.Kd = convert_byte_to_255_float(data[2])
    calculator.gyro_enabled = (data[3] == 0x01)
    print(f"Received PID values: Kp={calculator.Kp:.2f}, Kd={calculator.Kd:.2f}, Gyro={calculator.gyro_enabled}")
    hardware.write_to_frontend([RecvAddr.ApplyPidValues_Response])

def update_cycle_time(data):
    new_cycle = data[1]
    if new_cycle >= 2:
        global cycle_time
        cycle_time = new_cycle
    hardware.write_to_frontend([RecvAddr.UpdateCycleTime_Response, cycle_time])

def init_connection_to_frontend():
    print_verbose("Init connection request received")
    hardware.write_to_frontend([RecvAddr.InitConnection_Response, 0x01])

def reset_error():
    print_verbose("Reset error command")
    hardware.write_to_frontend([RecvAddr.ResetError_Response, 0x01])

# ----------------------------------------------------------------------
# Address definitions – send IDs + 1 = receive IDs
# ----------------------------------------------------------------------
# --------------------------------------------------------------
#  CAN address mapping – receive IDs are the original send IDs,
#  and the new send IDs are the original receive IDs (original + 1)
# --------------------------------------------------------------

class SendAddr:
    Health                       = 41
    InitConnection               = 51   # 50 + 1
    InitThrusters                = 61   # 60 + 1
    DeinitThrusters              = 71   # 70 + 1
    ResetError                   = 81   # 80 + 1
    ApplyManualControl          = 91   # 90 + 1
    ApplyControllerValues       = 101  # 100 + 1
    ApplyControllerValues_Dyn   = 111  # 110 + 1
    PullDirectionValues          = 121  # 120 + 1
    ApplyDirectionValues        = 131  # 130 + 1
    PullPidValues                = 141  # 140 + 1
    ApplyPidValues               = 151  # 150 + 1
    UpdateCycleTime              = 161  # 160 + 1


class RecvAddr:
    Health_Response                       = 40
    InitConnection_Response               = 50
    InitThrusters_Response                = 60
    DeinitThrusters_Response              = 70
    ResetError_Response                   = 80
    ApplyManualControl_Response          = 90
    ApplyControllerValues_Response       = 100
    ApplyControllerValues_Dyn_Response   = 110
    PullDirectionValues_Response          = 120
    ApplyDirectionValues_Response        = 130
    PullPidValues_Response                = 140
    ApplyPidValues_Response               = 150
    UpdateCycleTime_Response              = 160


# ----------------------------------------------------------------------
# Main program
# ----------------------------------------------------------------------
verbose = True
print_verbose("Starting initialization")
machine.freq(200_000_000)

# Load persisted data
direction_values_path = "directionValues.json"
dynamic_throttle_path = "dynamic_throttle_data.json"
direction_values_data = read_json_file(direction_values_path)
dynamic_throttle_data = read_json_file(dynamic_throttle_path)

state = 0
cycle_time = 20                     # 20 ms loop
hardware = HardwareHandler()

# Initialise IMU (optional)
try:
    i2c_gyro = I2C(0, scl=Pin(17), sda=Pin(16), freq=400_000)
    i2c_mag  = I2C(1, scl=Pin(19), sda=Pin(18), freq=400_000)
    gyro = IMUWrapper(i2c_gyro, i2c_mag)
except Exception as e:
    gyro = None
    print_verbose(f"Gyro init failed: {e}")

calculator = Calculator(direction_values_data, dynamic_throttle_data, gyro)

last_exec_1s   = time.ticks_ms()
last_exec_100ms = time.ticks_ms()
led_toggles = 0

print_verbose("Program started – waiting for init...")

while True:
    start = time.ticks_ms()

    # ---- 1 Hz tasks ----
    if time.ticks_diff(start, last_exec_1s) >= 1000:
        task_one_hz()
        last_exec_1s = start

    # ---- 10 Hz tasks ----
    if time.ticks_diff(start, last_exec_100ms) >= 100:
        task_ten_hz()
        last_exec_100ms = start

    # ---- CAN receive handling ----
    try:
        data_list = hardware.recvcan()
        for rx_id, data in data_list:
            print_can_msg(rx_id, data)

            # --------------------------------------------------------------
            # Dispatch based on the received CAN ID and announce the command
            # --------------------------------------------------------------
            if rx_id == RecvAddr.Health_Response:
                print_verbose("Health Request")
                hardware.sendcan(SendAddr.Health)
            elif   rx_id == RecvAddr.InitConnection_Response:
                print_verbose("Executed: InitConnection")
                init_connection_to_frontend()
            elif rx_id == RecvAddr.InitThrusters_Response:
                print_verbose("Executed: InitThrusters")
                init_motor_sequence()
            elif rx_id == RecvAddr.ResetError_Response:
                print_verbose("Executed: ResetError")
                reset_error()
            elif rx_id == RecvAddr.ApplyManualControl_Response:
                print_verbose("Executed: ApplyManualControl")
                apply_manual_control(data)
            elif rx_id == RecvAddr.ApplyControllerValues_Response:
                print_verbose("Executed: ApplyControllerValues (static throttle)")
                apply_controller_values(data, False)
            elif rx_id == RecvAddr.ApplyControllerValues_Dyn_Response:
                print_verbose("Executed: ApplyControllerValues (dynamic throttle)")
                apply_controller_values(data, True)
            elif rx_id == RecvAddr.PullDirectionValues_Response:
                print_verbose("Executed: PullDirectionValues")
                send_direction_values()
            elif rx_id == RecvAddr.ApplyDirectionValues_Response:
                print_verbose("Executed: ApplyDirectionValues")
                receive_direction_values(data)
            elif rx_id == RecvAddr.PullPidValues_Response:
                print_verbose("Executed: PullPidValues")
                send_PID_values()
            elif rx_id == RecvAddr.ApplyPidValues_Response:
                print_verbose("Executed: ApplyPidValues")
                receive_PID_values(data)
            elif rx_id == RecvAddr.UpdateCycleTime_Response:
                print_verbose("Executed: UpdateCycleTime")
                update_cycle_time(data)
        # if len(data_list) == 0:
        #     print("no data")

    except Exception as e:
        print_verbose(f"[ERROR] {e.__class__.__name__}: {e}")
        state = 2
        hardware.send_message_code_uart(0x00)   # generic error code

    # ---- Loop timing ----
    elapsed = time.ticks_diff(time.ticks_ms(), start)
    if elapsed < cycle_time:
        time.sleep_ms(cycle_time - elapsed)
    else:
        print_verbose(f"Warning: Cycle time exceeded by {elapsed - cycle_time} ms")
        hardware.send_message_code_uart(0x03)   # overrun indicator
