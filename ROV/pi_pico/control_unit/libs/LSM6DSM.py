import struct

class LSM6DSM:
    def __init__(self, i2c, addr=0x6B):
        self.i2c = i2c
        self.addr = addr

        # Initialisiere Beschleunigungssensor: ±2g, 104 Hz (CTRL1_XL = 0x10)
        self.i2c.writeto_mem(self.addr, 0x10, b'\x40')

        # Initialisiere Gyroskop: ±250 dps, 104 Hz (CTRL2_G = 0x11)
        self.i2c.writeto_mem(self.addr, 0x11, b'\x40')

    def read_accel(self):
        """
        Liest die Beschleunigungswerte in g (Gravitationskraft) und gibt ein Tupel (x, y, z) zurück.
        Skalierung: ±2g → 0.061 mg/LSB
        """
        data = self.i2c.readfrom_mem(self.addr, 0x28, 6)
        x, y, z = struct.unpack('<hhh', data)
        return (x * 0.061 / 1000, y * 0.061 / 1000, z * 0.061 / 1000)

    def read_gyro(self):
        """
        Liest die Gyroskop-Werte in °/s (Grad pro Sekunde) und gibt ein Tupel (x, y, z) zurück.
        Skalierung: ±250 dps → 8.75 mdps/LSB
        """
        data = self.i2c.readfrom_mem(self.addr, 0x22, 6)
        x, y, z = struct.unpack('<hhh', data)
        return (x * 8.75 / 1000, y * 8.75 / 1000, z * 8.75 / 1000)
