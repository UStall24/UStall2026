from libs.imu import IMU
from libs.LSM6DSM import LSM6DSM
from libs.hmc5883l import HMC5883L

class IMUWrapper:
    def __init__(self, i2c_gyro, i2c_mag):
        """
        Initialisiert das IMU-System (Gyroskop, Beschleunigungsmesser und Magnetometer)
        und führt die Kalibrierung durch.
        """
        self.imu = IMU(i2c_gyro, i2c_mag)
        self.imu.calibrate()

    def getData(self):
        """
        Gibt Sensordaten im Format [gyro_x, gyro_y, gyro_z, accel_x, accel_y, accel_z] zurück,
        wie es vom Calculator erwartet wird.
        """
        accel = self.imu.gyro.read_accel()
        gyro = self.imu.gyro.read_gyro()
        return list(gyro) + list(accel)
