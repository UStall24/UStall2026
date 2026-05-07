import time
from machine import I2C, Pin
from libs.hardwareHandler import HardwareHandler
from libs.calculator import Calculator, round_direction_values, byte_array_to_float
from libs.configLoader import *
from libs.imu_wrapper import IMUWrapper
from libs.communicationHelperLib import *
from libs.logger import *
from libs.helper_functions import *

verbose = True
ENABLE_TRY_CATCH = True
direction_values_dirty = False
last_direction_update_time = 0
SAVE_TIMEOUT_MS = 2000
state = 0
cycle_time = 20                     # 20 ms loop
direction_values_path = "directionValues.json"
dynamic_throttle_path = "dynamic_throttle_data.json"


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
        for i in range(len(pwm_values)):
            print(f"M{i}: {pwm_values[i]:+.02f}us", end=' ')
        print("")
        hardware.set_motor_speed_all(pwm_vals)
        trigger_status_led()  
        hardware.sendcan(0x02)

def apply_controller_values(motor_thrust_in_bytes, use_dynamic_throttle):
    if hardware.mot_pwms[0] is not None:
        motor_thrust_in_float = byte_array_to_float(motor_thrust_in_bytes)
        if not use_dynamic_throttle:
            throttle_vals = calculator.calc_throttle_from_dof(motor_thrust_in_float)
            # print("Throttle: ", throttle_vals)
            force_vals = calculator.throttle_to_force(throttle_vals)
            pwm_vals = calculator.calc_pwm_values(force_vals)
            # print("pwm_vals: ", pwm_vals)
        
        hardware.set_motor_speed_all(pwm_vals)
        trigger_status_led()   
        
def direct_motor_test(data):
    print("direct_motor_test")

    if len(data) < 7:
        print("Invalid data length")
        return
    
    duration_ms = data[6] * 10  # value * 10 ms
    
    motor_thrust_in_bytes = data[0:6]
    #logger.print_as_bytes(motor_thrust_in_bytes)
    motor_thrust_in_float = byte_array_to_float(motor_thrust_in_bytes)
    #logger.print_as_percent(motor_thrust_in_float)
    motor_thrust_in_pwm   = calculator.calc_all_motor_pwm2(motor_thrust_in_float)
    #logger.print_raw_list(motor_thrust_in_pwm)
    motor_thrust_in_pwm_deadzone = [calculator.deadZoneDefault] * 6

    hardware.set_motor_speed_all(motor_thrust_in_pwm)

    time.sleep_ms(duration_ms)

    hardware.set_motor_speed_all(motor_thrust_in_pwm_deadzone)

    hardware.sendcan(SendAddr.TestMotor, [1])


def init_motor_sequence():
    hardware.init_motor_all()
    logger.print_debug("Motors initialized", verbose=verbose)
    hardware.sendcan(SendAddr.InitThrusters, [0x01])

    global state
    state = 1 

    logger.print_system("Motors initialized", verbose=verbose)

_received_dofs = {
    "forwards_backwards": False,
    "left_right": False,
    "up_down": False,
    "yaw": False,
    "roll": False,
    "pitch": False
}


import time

# State tracking
_is_saving = False
_received_dofs = {
    "forwards_backwards": False,
    "left_right": False,
    "up_down": False,
    "yaw": False,
    "roll": False,
    "pitch": False
}

def handle_set_direction(rx_id, data):
    global _received_dofs, _is_saving

    # 1. Ignore new requests if we are currently busy writing to flash
    if _is_saving:
        return

    if len(data) < 6:
        return

    # 2. Map CAN IDs to the internal dictionary keys
    id_map = {
        RecvAddr.Set_FB_Response: "forwards_backwards",
        RecvAddr.Set_LR_Response: "left_right",
        RecvAddr.Set_UD_Response: "up_down",
        RecvAddr.Set_YAW_Response: "yaw",
        RecvAddr.Set_ROLL_Response: "roll",
        RecvAddr.Set_PITCH_Response: "pitch"
    }

    dof_key = id_map.get(rx_id)
    if not dof_key:
        return

    # 3. Process and store the values
    values = [calculator.byte_to_float(v) for v in data]
    calculator.direction_values[dof_key] = {
        "M1": values[0],
        "M2": values[1],
        "M3": values[2],
        "M4": values[3],
        "M5": values[4],
        "M6": values[5],
    }
    calculator.direction_values = round_direction_values(calculator.direction_values)
    _received_dofs[dof_key] = True

    # 4. ACK BACK (Essential for C# TaskCompletionSource)
    hardware.sendcan(rx_id, [])

    # 5. Final Save Check
    if all(_received_dofs.values()):
        _is_saving = True  # Lock the function
        try:
            # Save to flash memory (slow operation)
            write_json_file(direction_values_path, calculator.direction_values)
            
            # Brief pause to ensure flash is stable before sending next CAN frame
            time.sleep(0.1) 
            hardware.sendcan(SendAddr.All_DoF_Saved, [])

            # Reset tracking for next full batch
            for k in _received_dofs:
                _received_dofs[k] = False
        finally:
            _is_saving = False # Unlock

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

def handle_get_direction(rx_id):
    if rx_id == RecvAddr.Get_FB_Response:
        dof = "forwards_backwards"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_FB

    elif rx_id == RecvAddr.Get_LR_Response:
        dof = "left_right"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_LR

    elif rx_id == RecvAddr.Get_UD_Response:
        dof = "up_down"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_UD

    elif rx_id == RecvAddr.Get_YAW_Response:
        dof = "yaw"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_YAW

    elif rx_id == RecvAddr.Get_ROLL_Response:
        dof = "roll"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_ROLL

    elif rx_id == RecvAddr.Get_PITCH_Response:
        dof = "pitch"
        values = calculator.direction_values[dof]
        tx_id = SendAddr.Get_PITCH

    else:
        return

    print(
        f"{dof}: "
        f"M1:{values['M1']:+.2f} "
        f"M2:{values['M2']:+.2f} "
        f"M3:{values['M3']:+.2f} "
        f"M4:{values['M4']:+.2f} "
        f"M5:{values['M5']:+.2f} "
        f"M6:{values['M6']:+.2f}"
    )

    payload = [calculator.float_to_byte(values[m]) for m in ["M1","M2","M3","M4","M5","M6"]]

    print("Sending DOF values:", payload)

    hardware.sendcan(tx_id, payload)

class SendAddr:
    Health                       = 41
    InitConnection               = 51
    InitThrusters                = 61
    DeinitThrusters              = 71
    ResetError                   = 81
    ApplyManualControl           = 91
    ApplyControllerValues        = 101
    ApplyControllerValues_Dyn    = 111

    # --- NEW: per DoF direction values ---
    Set_FB                       = 121  # forwards_backwards
    Set_LR                       = 122  # left_right
    Set_UD                       = 123  # up_down
    Set_YAW                      = 124
    Set_ROLL                     = 125
    Set_PITCH                    = 126
    All_DoF_Saved                = 127

    # Pull values (optional, same structure)
    Get_FB                       = 131
    Get_LR                       = 132
    Get_UD                       = 133
    Get_YAW                      = 134
    Get_ROLL                     = 135
    Get_PITCH                    = 136

    PullPidValues                = 141
    ApplyPidValues               = 151
    UpdateCycleTime              = 161

    TestMotor = 171
    
class RecvAddr:
    TestMotor = 170
    Health_Response                       = 40
    InitConnection_Response               = 50
    InitThrusters_Response                = 60
    DeinitThrusters_Response              = 70
    ResetError_Response                   = 80
    ApplyManualControl_Response           = 90
    ApplyControllerValues_Response        = 100
    ApplyControllerValues_Dyn_Response    = 110

    # --- NEW: per DoF responses ---
    Set_FB_Response                       = 120
    Set_LR_Response                       = 121
    Set_UD_Response                       = 122
    Set_YAW_Response                      = 123
    Set_ROLL_Response                     = 124
    Set_PITCH_Response                    = 125

    Get_FB_Response                       = 130
    Get_LR_Response                       = 131
    Get_UD_Response                       = 132
    Get_YAW_Response                      = 133
    Get_ROLL_Response                     = 134
    Get_PITCH_Response                    = 135

    PullPidValues_Response                = 140
    ApplyPidValues_Response               = 150
    UpdateCycleTime_Response              = 160


# ----------------------------------------------------------------------
# Main program
# ----------------------------------------------------------------------
logger.print_system("Starting initialization", verbose=verbose)

# Load persisted data

direction_values_data = read_json_file(direction_values_path)
dynamic_throttle_data = read_json_file(dynamic_throttle_path)

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
calculator.direction_values = round_direction_values(calculator.direction_values)

last_exec_1s   = time.ticks_ms()
last_exec_100ms = time.ticks_ms()
led_toggles = 0

logger.print_system("Program started – waiting for init...", verbose=verbose)

while True:
    start = time.ticks_ms()

    if time.ticks_diff(start, last_exec_1s) >= 1000:
        task_one_hz()
        last_exec_1s = start

    if time.ticks_diff(start, last_exec_100ms) >= 100:
        task_ten_hz()
        last_exec_100ms = start

    if ENABLE_TRY_CATCH:
        try:
            data_list = hardware.recvcan()

            for rx_id, data in data_list:

                if direction_values_dirty:
                    now = time.ticks_ms()
                    if time.ticks_diff(now, last_direction_update_time) > SAVE_TIMEOUT_MS:
                        write_json_file(direction_values_path, calculator.direction_values)
                        direction_values_dirty = False
                        logger.print_system("Direction values saved (debounced)", verbose=verbose)

                # logger.print_can_msg(rx_id, data, verbose=True)
                
                # General
                if rx_id == RecvAddr.Health_Response:
                    hardware.sendcan(SendAddr.Health)
                    logger.print_system("Health Request", verbose=verbose)

                elif rx_id == RecvAddr.InitThrusters_Response:
                    init_motor_sequence()
                    logger.print_system("Executed: InitThrusters", verbose=verbose)
                
                elif rx_id == RecvAddr.UpdateCycleTime_Response:
                    update_cycle_time(data)
                    logger.print_system("Executed: UpdateCycleTime", verbose=verbose)
                # General end
                
                # Testing Motors (Works - not Verified)
                elif rx_id == RecvAddr.TestMotor:
                    direct_motor_test(data)
                # Testing Motors End
                
                # Driving
                elif rx_id == RecvAddr.ApplyControllerValues_Response:
                    apply_controller_values(data, False)

                elif rx_id == RecvAddr.ApplyControllerValues_Dyn_Response:
                    apply_controller_values(data, True)
                # Driving End
                    
                # DirectionValues Get and Set
                elif RecvAddr.Set_FB_Response <= rx_id <= RecvAddr.Set_PITCH_Response:
                    logger.print_system("Executed: RecievedDirectionValues", verbose=verbose)
                    handle_set_direction(rx_id, data)
                
                elif RecvAddr.Get_FB_Response <= rx_id <= RecvAddr.Get_PITCH_Response:
                    handle_get_direction(rx_id)
                # DirectionValues Get and Set End
                
                # Gyro Commands
                elif rx_id == RecvAddr.PullPidValues_Response:
                    send_PID_values()
                    logger.print_system("Executed: PullPidValues", verbose=verbose)

                elif rx_id == RecvAddr.ApplyPidValues_Response:
                    receive_PID_values(data)
                    logger.print_system("Executed: ApplyPidValues", verbose=verbose)
                # Gyro Commands end
                
                
        except Exception as e:
            state = 2
            hardware.sendcan(0x00)
            logger.print_system(f"[ERROR] {e.__class__.__name__}: {e}", verbose=verbose)

    else:
        # no try/except mode (fast path)
        data_list = hardware.recvcan()

        for rx_id, data in data_list:
            logger.print_can_msg(rx_id, data, verbose=True)
            # (same dispatch block as above OR call a function)

    elapsed = time.ticks_diff(time.ticks_ms(), start)

    if elapsed < cycle_time:
        time.sleep_ms(cycle_time - elapsed)
    else:
        hardware.sendcan(0x03)
        # logger.print_system(f"Warning: Cycle time exceeded by {elapsed - cycle_time}ms", verbose=verbose)
