from math import atan2, sqrt


class Calculator:
    def __init__(self, direction_values, dynamic_throttle_data, gyro=None):

        # ---------------- PWM / DEADZONE ----------------
        self.maxPWM = 1900
        self.minPWM = 1100
        self.safeMaxPWM = 1700
        self.safeMinPWM = 1300

        self.deadZoneMin = 1464
        self.deadZoneMax = 1536
        self.deadZoneDefault = 1500

        # ---------------- CONTROL ----------------
        self.direction_values = direction_values
        
        self.gyro = gyro
        self.gyro_enabled = False

        # ---------------- PD ----------------
        self.Kp = 0.05
        self.Kd = 0.018
        self.filtered_angle_x = 0
        self.filtered_angle_y = 0
        self.alpha = 0.98
        self.dt = 0.02

        self.ANGLE_DEADZONE = 0.175
        self.GYRO_RATE_DEADZONE = 3.5

        # ---------------- POWER MODEL ----------------
        self.max_current_load = 20

        self.pwm_low_reg_coeff = dynamic_throttle_data["force2pwm"]["low"]
        self.pwm_high_reg_coeff = dynamic_throttle_data["force2pwm"]["high"]
        self.current_low_reg_coeff = dynamic_throttle_data["force2current"]["low"]
        self.current_high_reg_coeff = dynamic_throttle_data["force2current"]["high"]

        self.max_force_forward = abs(dynamic_throttle_data["max_force_forward"])
        self.max_force_reverse = abs(dynamic_throttle_data["max_force_reverse"])

        # ---------------- MAPPING ----------------
        self.DOF = [
            "forwards_backwards",
            "left_right",
            "up_down",
            "yaw",
            "roll",
            "pitch"
        ]

        self.MOTOR = ["M1", "M2", "M3", "M4", "M5", "M6"]

        # ---------------- DEBUG ----------------
        self.debug_mode = 0  # 0=OFF, 1=SHORT, 2=FULL

    # =========================================================
    # UTIL
    # =========================================================

    def byte_to_float(self, value):
        return (value / 127.5) - 1

    def normalize(self, v):
        return self.byte_to_float(v) if abs(v) > 1 else v

    def float_to_byte(self, v):
        v = max(-1.0, min(1.0, v))
        return int((v + 1.0) * 127.5)

    # =========================================================
    # DEBUG
    # =========================================================

    def _dbg(self, msg, level):
        if self.debug_mode >= level:
            print(msg)

    # =========================================================
    # PD CONTROLLER
    # =========================================================

    def update_pd_controller(self, gx, gy, ax, ay, az):

        ax_ang = atan2(ay, sqrt(ax**2 + az**2))
        ay_ang = atan2(ax, sqrt(ay**2 + az**2))

        self.filtered_angle_x = self.alpha * (self.filtered_angle_x + gx * self.dt) + (1 - self.alpha) * ax_ang
        self.filtered_angle_y = self.alpha * (self.filtered_angle_y + gy * self.dt) + (1 - self.alpha) * ay_ang

        if abs(self.filtered_angle_x) < self.ANGLE_DEADZONE and abs(gx) < self.GYRO_RATE_DEADZONE:
            self.filtered_angle_x = 0
            gx = 0

        if abs(self.filtered_angle_y) < self.ANGLE_DEADZONE and abs(gy) < self.GYRO_RATE_DEADZONE:
            self.filtered_angle_y = 0
            gy = 0

        cx = -self.Kp * self.filtered_angle_x - self.Kd * gx if abs(self.filtered_angle_x) > self.ANGLE_DEADZONE else 0
        cy = -self.Kp * self.filtered_angle_y - self.Kd * gy if abs(self.filtered_angle_y) > self.ANGLE_DEADZONE else 0

        return [
            cy * 0.866 - cx * 0.5,
            -cx * 0.5,
            cy * 0.866 + cx * 0.5
        ]

    # =========================================================
    # CORE DOF MAPPING (FIXED FEATURE)
    # =========================================================

    def calc_throttle_from_dof(self, dof_command):
        dv = self.direction_values
        motor_outputs = [0.0] * 6
        # print("DOF: ", dof_command)
        # Accumulating all direction weights
        for i, dof_name in enumerate(self.DOF):
            weighted_dv = [dv[dof_name][m] * dof_command[i] for m in self.MOTOR]
            motor_outputs = [x + y for x, y in zip(motor_outputs, weighted_dv)]
        
        # Normalizing it
        max_abs = max(abs(v) for v in motor_outputs)
        if max_abs > 1:
            motor_outputs = [v / max_abs for v in motor_outputs]

        return motor_outputs

    # =========================================================
    # PWM
    # =========================================================

    def calc_motor_pwm(self, throttle):
        r = self.deadZoneDefault - self.safeMinPWM
        f = self.safeMaxPWM - self.deadZoneDefault

        pwm = self.deadZoneDefault + throttle * (r if throttle <= 0 else f)

        return self.deadZoneDefault if self.deadZoneMin < pwm < self.deadZoneMax else pwm

    def throttle_to_force(self, throttle_vals):

        force_vals = []

        for t in throttle_vals:
            t = self.normalize(t)

            # deadzone
            if abs(t) < 0.01:
                force_vals.append(0.0)
                continue

            # linear mapping
            if t > 0:
                force_vals.append(t * self.max_force_forward)
            else:
                force_vals.append(t * self.max_force_reverse)

        return force_vals
    
    def calc_pwm_values(self, forces):
        return [
            round(poly_eval(self.pwm_high_reg_coeff, f)) if f > 0.01
            else round(poly_eval(self.pwm_low_reg_coeff, f)) if f < -0.01
            else self.deadZoneDefault
            for f in forces
        ]
    
    def calc_all_motor_pwm(self, throttle_values, byte_to_float_calculation):
        pwm_values = []
        for value in throttle_values:
            if byte_to_float_calculation:
                pwm_value = self.calc_motor_pwm(self.byte_to_float(value))
            else:
                pwm_value = self.calc_motor_pwm(value)
            pwm_values.append(pwm_value)
        return pwm_values
    
    def calc_all_motor_pwm2(self, throttle_values):
        force_values = [throttle_val * 1.15 for throttle_val in throttle_values]
        return self.calc_pwm_values(force_values)

    # =========================================================
    # DYNAMIC THROTTLE
    # =========================================================

    def dynamic_throttle_2_pwm(self, throttle_values):

        forces = []
        for t in throttle_values:
            if abs(t) > 0.01:
                forces.append(t * (self.max_force_forward if t > 0 else self.max_force_reverse))
            else:
                forces.append(0)

        currents = self.calc_current_values(forces)

        total = sum(currents)
        factor = total / self.max_current_load
        i = 0

        while factor > 1 and i < 15:
            scale = 0.3 if factor > 2 else 0.15 if factor > 1.4 else 0.02
            forces = [f * (1 - scale) for f in forces]
            currents = self.calc_current_values(forces)
            total = sum(currents)
            factor = total / self.max_current_load
            i += 1

        if total > self.max_current_load * 1.1:
            return [self.deadZoneDefault] * 6, forces, currents

        return self.calc_pwm_values(forces), forces, currents

    def calc_current_values(self, forces):
        return [
            poly_eval(self.current_high_reg_coeff, f) if f > 0.01
            else poly_eval(self.current_low_reg_coeff, f) if f < -0.01
            else 0.0
            for f in forces
        ]


# =========================================================
# POLY
# =========================================================

def poly_eval(coeffs, x):
    return sum(c * x ** i for i, c in enumerate(reversed(coeffs)))
def round_direction_values(direction_values, decimals=2):
    result = {}

    for dof, motors in direction_values.items():
        result[dof] = {}

        for motor, value in motors.items():
            try:
                result[dof][motor] = round(float(value), decimals)
            except:
                result[dof][motor] = value  # fallback if not numeric

    return result

def byte_array_to_float(values):
    return [round((v / 127.5) - 1, 2) for v in values]
def byte_to_pwm(value):
    value = max(0, min(255, value))  # clamp
    return int(1100 + (value / 255) * 800)
def byte_array_to_pwm(values):
    return [byte_to_pwm(v) for v in values]