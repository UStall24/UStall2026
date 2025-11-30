import struct
import time

class HMC5883L:
    def __init__(self, i2c, addr=0x1E):
        self.i2c = i2c
        self.addr = addr

        # Konfigurationsregister
        self.i2c.writeto_mem(self.addr, 0x00, b'\x70')  # 8-average, 15 Hz, normal measurement
        self.i2c.writeto_mem(self.addr, 0x01, b'\x20')  # Gain = ±1.3 Gauss
        self.i2c.writeto_mem(self.addr, 0x02, b'\x00')  # Continuous measurement mode
        time.sleep(0.1)  # Warten bis Initialisierung abgeschlossen

    def read(self):
        """
        Liest die Magnetometer-Daten in Milligauss (mG) und gibt ein Tupel (x, y, z) zurück.
        Skalierung bei ±1.3 Gauss: 0.92 mG/LSB
        Die Achsen sind im Format X, Z, Y gespeichert.
        """
        data = self.i2c.readfrom_mem(self.addr, 0x03, 6)
        x, z, y = struct.unpack('>hhh', data)
        return (x * 0.92, y * 0.92, z * 0.92)
