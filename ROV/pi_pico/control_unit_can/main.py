import time
from machine import I2C, Pin
from libs.hardwareHandler import HardwareHandler
from libs.calculator import Calculator
from libs.configLoader import *
from libs.imu_wrapper import IMUWrapper
from libs.communicationHelperLib import *
from libs import logger

# (Printing helpers moved into `libs/logger.py`)


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
        
def trigger_status_led():
    global led_toggles, state
    if state == 1:   # only when initialized / normal operation
        hardware.led.value(1 - hardware.led.value())

# ----------------------------------------------------------------------
# Command handlers (receive side)
# ----------------------------------------------------------------------
def apply_manual_control(data):
    if hardware.mot_pwms[0] is not None:
        motor_vals = list(data[1:7])
        pwm_vals = calculator.calc_all_motor_pwm2(motor_vals)
        logger.print_debug(pwm_vals, verbose=verbose)
        hardware.set_motor_speed_all(pwm_vals)
        trigger_status_led()  
        hardware.sendcan(0x02)

def apply_controller_values(data, use_dynamic_throttle):
    if hardware.mot_pwms[0] is not None:
        throttle = calculator.calc_throttle_value2(data)

        if not use_dynamic_throttle:
            pwm_vals = calculator.calc_all_motor_pwm(throttle, False)
        else:
            pwm_vals, _, _ = calculator.dynamic_throttle_2_pwm(throttle)
        
        hardware.set_motor_speed_all(pwm_vals)
        trigger_status_led()   
        
        
def init_motor_sequence():
    hardware.init_motor_all()
    logger.print_debug("Motors initialized", verbose=verbose)
    hardware.sendcan(SendAddr.InitThrusters, [0x01])

    global state
    state = 1 

    logger.print_system("Motors initialized", verbose=verbose)

def send_direction_values():
    logger.print_system("Sending direction values", verbose=verbose)
    payload = get_direction_uart_values_payload(calculator.direction_values)

    for i, motor_vals in enumerate(payload):
        rxId = SendAddr.PullDirectionValues + i
        hardware.sendcan(rxId=rxId, data=bytes(motor_vals))
        # logger.print_debug(f"Sending: {motor_vals} at RxId: {rxId}", verbose=verbose)
        time.sleep(0.02)

direction_values_buffer = []
def receive_direction_values(rxId, data):
    global direction_values_buffer

    direction_values_buffer.append((rxId, data))

    if len(direction_values_buffer) == 6:
        sorted_buffer = sorted(direction_values_buffer, key=lambda x: x[0])
        direction_values_buffer = []  # Reset early to avoid race conditions

        matrix = []
        for _, frame_data in sorted_buffer:
            matrix.append([calculator.byte_to_float(v) for v in frame_data])

        update_direction_values(calculator.direction_values, matrix)
        logger.print_system(str(calculator.direction_values), verbose=True)
        write_json_file(direction_values_path, calculator.direction_values)
        hardware.sendcan(SendAddr.ApplyDirectionValues)


def send_PID_values():
    logger.print_system("Send PID values", verbose=verbose)
    payload = [
        SendAddr.PullPidValues,                                   # request
        convert_255_float_to_byte(calculator.Kp),
        convert_255_float_to_byte(calculator.Kd),
        0x01 if calculator.gyro_enabled else 0x00
    ]
    logger.print_as_hex(payload, verbose=verbose)
    hardware.sendcan(payload)

def receive_PID_values(data):
    logger.print_system("Receive PID values", verbose=verbose)
    if len(data) < 4:
        logger.print_system("Error: Received data is too short.", verbose=verbose)
        return

    calculator.Kp = convert_byte_to_255_float(data[1])
    calculator.Kd = convert_byte_to_255_float(data[2])
    calculator.gyro_enabled = (data[3] == 0x01)
    logger.print_system(f"Received PID values: Kp={calculator.Kp:.2f}, Kd={calculator.Kd:.2f}, Gyro={calculator.gyro_enabled}", verbose=True)
    hardware.sendcan([RecvAddr.ApplyPidValues_Response])

def update_cycle_time(data):
    new_cycle = data[1]
    if new_cycle >= 2:
        global cycle_time
        cycle_time = new_cycle
    hardware.sendcan([RecvAddr.UpdateCycleTime_Response, cycle_time])

def init_connection_to_frontend():
    logger.print_system("Init connection request received", verbose=verbose)
    hardware.sendcan([RecvAddr.InitConnection_Response, 0x01])

def reset_error():
    logger.print_system("Reset error command", verbose=verbose)
    hardware.sendcan([RecvAddr.ResetError_Response, 0x01])

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
logger.print_system("Starting initialization", verbose=verbose)

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
    logger.print_system(f"Gyro init failed: {e}", verbose=verbose)

calculator = Calculator(direction_values_data, dynamic_throttle_data, gyro)

last_exec_1s   = time.ticks_ms()
last_exec_100ms = time.ticks_ms()
led_toggles = 0

logger.print_system("Program started – waiting for init...", verbose=verbose)

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
            #logger.print_can_msg(rx_id, data, verbose=True)

            # --------------------------------------------------------------
            # Dispatch based on the received CAN ID and announce the command
            # --------------------------------------------------------------
            if rx_id == RecvAddr.Health_Response:
                hardware.sendcan(SendAddr.Health)
                logger.print_system("Health Request", verbose=verbose)
            elif rx_id == RecvAddr.InitThrusters_Response:
                init_motor_sequence()
                logger.print_system("Executed: InitThrusters", verbose=verbose)
            elif   rx_id == RecvAddr.InitConnection_Response:
                init_connection_to_frontend()
                logger.print_system("Executed: InitConnection", verbose=verbose)
            elif rx_id == RecvAddr.ResetError_Response:
                reset_error()
                logger.print_system("Executed: ResetError", verbose=verbose)
            elif rx_id == RecvAddr.ApplyManualControl_Response:
                apply_manual_control(data)
                logger.print_debug("Executed: ApplyManualControl", verbose=verbose)
            elif rx_id == RecvAddr.ApplyControllerValues_Response:
                apply_controller_values(data, False)
                # logger.print_debug("Executed: ApplyControllerValues (static throttle)", verbose=False)
            elif rx_id == RecvAddr.ApplyControllerValues_Dyn_Response:
                apply_controller_values(data, True)
                # logger.print_debug("Executed: ApplyControllerValues (dynamic throttle)", verbose=False)
            elif rx_id == RecvAddr.PullDirectionValues_Response:
                logger.print_system("Executed: PullDirectionValues", verbose=verbose)
                send_direction_values()
            elif RecvAddr.ApplyDirectionValues_Response <= rx_id < RecvAddr.ApplyDirectionValues_Response + 6:
                # logger.print_system("Executed: ApplyDirectionValues", verbose=verbose)
                receive_direction_values(rx_id, data)
            elif rx_id == RecvAddr.PullPidValues_Response:
                send_PID_values()
                logger.print_system("Executed: PullPidValues", verbose=verbose)
            elif rx_id == RecvAddr.ApplyPidValues_Response:
                receive_PID_values(data)
                logger.print_system("Executed: ApplyPidValues", verbose=verbose)
            elif rx_id == RecvAddr.UpdateCycleTime_Response:
                update_cycle_time(data)
                logger.print_system("Executed: UpdateCycleTime", verbose=verbose)
        # if len(data_list) == 0:
        #    print("no data")

    except Exception as e:
        state = 2
        hardware.sendcan(0x00)   # generic error code
        logger.print_system(f"[ERROR] {e.__class__.__name__}: {e}", verbose=verbose)

    # ---- Loop timing ----
    elapsed = time.ticks_diff(time.ticks_ms(), start)
    if elapsed < cycle_time:
        time.sleep_ms(cycle_time - elapsed)
    else:
        hardware.sendcan(0x03)   # overrun indicator
        logger.print_system(f"Warning: Cycle time exceeded by {elapsed - cycle_time}Âms", verbose=verbose)
