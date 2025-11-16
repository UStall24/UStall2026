import time
import math

class IMU:
    def __init__(self, i2c_gyro, i2c_mag):
        from libs.LSM6DSM import LSM6DSM
        from libs.hmc5883l import HMC5883L

        self.gyro = LSM6DSM(i2c_gyro)
        self.mag = HMC5883L(i2c_mag)

        self.offset_roll = 0.0
        self.offset_pitch = 0.0
        self.offset_yaw = 0.0
        self.offset_mag_x = 0.0
        self.offset_mag_y = 0.0
        self.offset_mag_z = 0.0

        self.last_time = time.ticks_ms()
        self.roll = 0.0
        self.pitch = 0.0
        self.yaw = 0.0

    def calibrate(self):
        print("Kalibriere Sensoren...")
        accel = self.gyro.read_accel()
        mag = self.mag.read()

        self.offset_roll, self.offset_pitch = self.compute_roll_pitch(accel)
        self.offset_yaw = self.compute_yaw(accel, mag)

        self.offset_mag_x = mag[0]
        self.offset_mag_y = mag[1]
        self.offset_mag_z = mag[2]

        print(f"Kalibrierung abgeschlossen: Roll={self.offset_roll:.2f}°, Pitch={self.offset_pitch:.2f}°, Yaw={self.offset_yaw:.2f}°")
        print(f"Magnetometer-Offset: X={self.offset_mag_x:.1f}, Y={self.offset_mag_y:.1f}, Z={self.offset_mag_z:.1f}")

    def compute_roll_pitch(self, accel):
        ax, ay, az = accel
        roll = math.atan2(ay, az)
        pitch = math.atan2(-ax, math.sqrt(ay**2 + az**2))
        return math.degrees(roll), math.degrees(pitch)

    def compute_yaw(self, accel, mag):
        roll, pitch = self.compute_roll_pitch(accel)
        roll_rad = math.radians(roll)
        pitch_rad = math.radians(pitch)

        mag_x, mag_y, mag_z = mag

        # Tilt-Kompasskorrektur
        mx = mag_x * math.cos(pitch_rad) + mag_z * math.sin(pitch_rad)
        my = (
            mag_x * math.sin(roll_rad) * math.sin(pitch_rad) +
            mag_y * math.cos(roll_rad) -
            mag_z * math.sin(roll_rad) * math.cos(pitch_rad)
        )
        yaw = math.atan2(-my, mx)
        return (math.degrees(yaw) + 360) % 360

    def update(self):
        now = time.ticks_ms()
        dt = time.ticks_diff(now, self.last_time) / 1000.0
        self.last_time = now

        accel = self.gyro.read_accel()
        gyro = self.gyro.read_gyro()
        mag = self.mag.read()

        gx, gy, gz = [math.radians(g) for g in gyro]
        roll_acc, pitch_acc = self.compute_roll_pitch(accel)

        alpha = 0.96
        self.roll = alpha * (self.roll + gx * dt) + (1 - alpha) * roll_acc
        self.pitch = alpha * (self.pitch + gy * dt) + (1 - alpha) * pitch_acc
        self.yaw = self.compute_yaw(accel, mag)

        # Offsets abziehen
        self.roll -= self.offset_roll
        self.pitch -= self.offset_pitch
        self.yaw -= self.offset_yaw
        self.yaw %= 360

        # Tilt-korrigierte Magnetometerdaten (optional zur Anzeige)
        mag_x = mag[0] - self.offset_mag_x
        mag_y = mag[1] - self.offset_mag_y
        mag_z = mag[2] - self.offset_mag_z

        return {
            "roll": self.roll,
            "pitch": self.pitch,
            "yaw": self.yaw,
            "mag": (mag_x, mag_y, mag_z)
        }
