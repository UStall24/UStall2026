from machine import Pin, SPI
import time

def pretty_can_print(rxId, data):
    msg = " ".join(f"{b:02X}" for b in data)
    return f"RxId: {rxId} - Data: {msg}"

class RP2350_CAN:
    """
    Simple MCP2515/XL2515 CAN driver for RP2350 (Pico-like board) using SPI.

    - No IRQ / interrupt usage (poll-only to avoid TinyUSB recursion issues)
    - send(can_id: int, data: bytes/bytearray/list[int])
    - recv(expected_id: Optional[int] = None, timeout_ms: int = 0) -> Optional[bytearray]
      * timeout_ms = 0 : single poll, non-blocking (returns None if no frame)
      * timeout_ms > 0 : block up to timeout_ms, polling CANINTF
    """

    # MCP2515 SPI commands
    _CAN_RESET = 0xC0
    _CAN_READ = 0x03
    _CAN_WRITE = 0x02
    _CAN_BIT_MODIFY = 0x05
    _CAN_READ_STATUS = 0xA0

    # Registers
    _CNF3 = 0x28
    _CNF2 = 0x29
    _CNF1 = 0x2A

    _TXB0CTRL = 0x30
    _TXB0SIDH = 0x31
    _TXB0SIDL = 0x32
    _TXB0EID8 = 0x33
    _TXB0EID0 = 0x34
    _TXB0DLC  = 0x35
    _TXB0D0   = 0x36

    _RXB0CTRL = 0x60
    _RXB0SIDH = 0x61
    _RXB0SIDL = 0x62
    _RXB0DLC  = 0x65
    _RXB0D0   = 0x66

    _RXF0SIDH = 0x00
    _RXF0SIDL = 0x01
    _RXM0SIDH = 0x20
    _RXM0SIDL = 0x21

    _CANINTF = 0x2C
    _CANINTE = 0x2B

    _CANCTRL = 0x0F
    _CANSTAT = 0x0E

    # CANCTRL bits
    _REQOP_NORMAL = 0x00
    _CLKOUT_ENABLED = 0x04
    _OPMODE_MASK = 0xE0
    _OPMODE_NORMAL = 0x00

    def __init__(
        self,
        rate_kbps="500KBPS",
        spi_cs=9,
        irq=None,               # unused now, kept for compat
        spi_port=1,
        spi_clk=10,
        spi_mosi=11,
        spi_miso=12,
        spi_freq=10_000_000,
    ):
        # Predefined timing configuration for MCP2515 @ some oscillator (commonly 16MHz)
        self.can_rate_arr = {
            "5KBPS":    (0xA7, 0xBF, 0x07),
            "10KBPS":   (0x31, 0xA4, 0x04),
            "20KBPS":   (0x18, 0xA4, 0x04),
            "50KBPS":   (0x09, 0xA4, 0x04),
            "100KBPS":  (0x04, 0x9E, 0x03),
            "125KBPS":  (0x03, 0x9E, 0x03),
            "250KBPS":  (0x01, 0x1E, 0x03),
            "500KBPS":  (0x00, 0x9E, 0x03),
            "800KBPS":  (0x00, 0x92, 0x02),
            "1000KBPS": (0x00, 0x82, 0x02),
        }

        if rate_kbps not in self.can_rate_arr:
            raise ValueError("Unsupported CAN bitrate: {}".format(rate_kbps))

        # SPI + CS
        self.spi = SPI(
            spi_port,
            spi_freq,
            polarity=0,
            phase=0,
            bits=8,
            sck=Pin(spi_clk),
            mosi=Pin(spi_mosi),
            miso=Pin(spi_miso),
        )
        self.cs = Pin(spi_cs, Pin.OUT)
        self._cs_high()

        # Reset and configure
        self.reset()
        time.sleep_ms(100)
        self._config(rate_kbps)
        self.print_settings()

    # ---- Low-level helpers ----

    def _cs_low(self):
        self.cs(0)

    def _cs_high(self):
        self.cs(1)

    def reset(self):
        """Send MCP2515 reset command."""
        self._cs_low()
        self.spi.write(bytearray([self._CAN_RESET]))
        self._cs_high()

    def _read_byte(self, reg):
        self._cs_low()
        self.spi.write(bytearray([self._CAN_READ, reg]))
        data = self.spi.read(1)
        self._cs_high()
        return data[0]

    def _write_byte(self, reg, val):
        self._cs_low()
        self.spi.write(bytearray([self._CAN_WRITE, reg, val & 0xFF]))
        self._cs_high()

    def _bit_modify(self, reg, mask, data):
        self._cs_low()
        self.spi.write(bytearray([self._CAN_BIT_MODIFY, reg, mask, data & 0xFF]))
        self._cs_high()

    def _set_mode(self, mode):
        """Set CANCTRL operation mode (REQOP bits)."""
        # Clear REQOP bits, keep others
        current = self._read_byte(self._CANCTRL)
        current &= ~self._OPMODE_MASK
        current |= (mode & self._OPMODE_MASK)
        self._write_byte(self._CANCTRL, current)

        # Wait until CANSTAT reflects the new mode
        for _ in range(20):
            stat = self._read_byte(self._CANSTAT)
            if (stat & self._OPMODE_MASK) == mode:
                return True
            time.sleep_ms(10)
        return False

    # ---- Configuration ----

    def _config(self, rate_kbps):
        """Configure bit-timing, RX/TX buffers, and normal mode."""
        cnf1, cnf2, cnf3 = self.can_rate_arr[rate_kbps]

        # Enter configuration mode first
        self._set_mode(0x80)  # Configuration mode (REQOP = 100b)

        # Set bit timing
        self._write_byte(self._CNF1, cnf1)
        self._write_byte(self._CNF2, cnf2)
        self._write_byte(self._CNF3, cnf3)

        # Setup TXB0: for now, just leave default ID, DLC will be set per frame
        self._write_byte(self._TXB0SIDH, 0xFF)
        self._write_byte(self._TXB0SIDL, 0xE0)
        self._write_byte(self._TXB0DLC, 0x40 | 0x08)  # TXRTR=0, DLC=8 default

        # Setup RXB0: receive all std IDs (you can refine filters/masks later)
        self._write_byte(self._RXB0SIDH, 0x00)
        self._write_byte(self._RXB0SIDL, 0x60)  # BUKT, rollover, etc. if desired
        self._write_byte(self._RXB0CTRL, 0x60)  # receive all valid messages
        self._write_byte(self._RXB0DLC, 0x08)

        # Simple mask/filter: receive everything
        self._write_byte(self._RXF0SIDH, 0x00)
        self._write_byte(self._RXF0SIDL, 0x00)
        self._write_byte(self._RXM0SIDH, 0x00)
        self._write_byte(self._RXM0SIDL, 0x00)

        # Clear and enable receive interrupts
        self._write_byte(self._CANINTF, 0x00)
        self._write_byte(self._CANINTE, 0x01)  # RX0 interrupt enable

        # Normal mode, CLKOUT enabled
        self._write_byte(self._CANCTRL, self._REQOP_NORMAL | self._CLKOUT_ENABLED)

        # Confirm normal mode
        _ = self._read_byte(self._CANSTAT)
        if (_ & self._OPMODE_MASK) != self._OPMODE_NORMAL:
            # Try again
            self._write_byte(self._CANCTRL, self._REQOP_NORMAL | self._CLKOUT_ENABLED)

    # ---- Public API: send / receive ----

    def send(self, can_id, data):
        """
        Send a standard 11-bit CAN frame on TXB0.

        :param can_id: 11-bit CAN ID (0..0x7FF)
        :param data: bytes/bytearray/list[int] of length 0..8
        """
        if isinstance(data, list):
            data = bytearray(data)
        else:
            data = bytearray(data)

        dlc = len(data)
        if dlc > 8:
            dlc = 8
            data = data[:8]

        # Wait until TXB0 not pending
        dly = 0
        while (self._read_byte(self._TXB0CTRL) & 0x08) and (dly < 50):
            dly += 1
            time.sleep_ms(1)

        # Load ID (standard frame)
        self._write_byte(self._TXB0SIDH, (can_id >> 3) & 0xFF)
        self._write_byte(self._TXB0SIDL, (can_id & 0x07) << 5)

        # Extended ID bits not used
        self._write_byte(self._TXB0EID8, 0x00)
        self._write_byte(self._TXB0EID0, 0x00)

        # DLC
        self._write_byte(self._TXB0DLC, dlc & 0x0F)

        # Data bytes
        for i in range(dlc):
            self._write_byte(self._TXB0D0 + i, data[i])

        # Request to send TXB0
        self._write_byte(self._TXB0CTRL, 0x08)

        print(f"Sending -> {pretty_can_print(can_id, data)}")

    def recv(self, timeout_ms=0):
        """
        Poll RXB0 and return a list of (can_id, data) tuples.

        :param timeout_ms:  0 = non-blocking (single poll),
                            >0 = block up to timeout_ms, polling RX0IF.
        :return: list of (can_id:int, data:bytearray)
        """
        frames = []
        start = time.ticks_ms()

        while True:
            # Check RX0IF (bit 0 of CANINTF)
            intf = self._read_byte(self._CANINTF)
            if intf & 0x01:
                # There is a frame in RXB0
                sid_h = self._read_byte(self._RXB0SIDH)
                sid_l = self._read_byte(self._RXB0SIDL)

                can_id = ((sid_h << 3) | (sid_l >> 5)) & 0x7FF

                dlc = self._read_byte(self._RXB0DLC) & 0x0F
                buf = bytearray(dlc)
                for i in range(dlc):
                    buf[i] = self._read_byte(self._RXB0D0 + i)

                # Clear RX0IF
                self._write_byte(self._CANINTF, intf & ~0x01)

                # Re-arm RXB0 (optional cleanup)
                self._write_byte(self._RXB0SIDH, 0x00)
                self._write_byte(self._RXB0SIDL, 0x60)

                frames.append((can_id, buf))

                # Loop again immediately to see if another frame is already waiting
                # (with rollover / continuous traffic)
                continue

            # No RX0IF set
            if frames:
                # We already collected at least one frame in this call
                return frames

            if timeout_ms == 0:
                # Non-blocking: no frames available
                return frames

            if time.ticks_diff(time.ticks_ms(), start) >= timeout_ms:
                # Timed out without receiving anything
                return frames

            # Small sleep to avoid busy-wait
            time.sleep_ms(1)

    def print_settings(self):
        cnf1 = self._read_byte(self._CNF1)
        cnf2 = self._read_byte(self._CNF2)
        cnf3 = self._read_byte(self._CNF3)
        canctrl = self._read_byte(self._CANCTRL)
        canstat  = self._read_byte(self._CANSTAT)

        print("=== RP2350_CAN Settings ===")
        print(f"SPI freq        : {self.spi.spi_freq}")
        print(f"CNF1 / CNF2 / CNF3 : 0x{cnf1:02X} 0x{cnf2:02X} 0x{cnf3:02X}")
        print(f"CANCTRL         : 0x{canctrl:02X}")
        print(f"CANSTAT         : 0x{canstat:02X}")
        print(f"OPMODE          : {(canstat & self._OPMODE_MASK) >> 5}")
        print("==========================")



if __name__=="__main__":
    can = RP2350_CAN()
    while 1:
        msg = can.recv()
        for rxId, data in msg:
            print(f"RxId: {rxId} - Data: {data}")
        time.sleep(0.01)