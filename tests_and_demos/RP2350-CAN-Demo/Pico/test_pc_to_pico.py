import time
from rp2350_can import RP2350_CAN  # or however your file is named
can = RP2350_CAN(rate_kbps="500KBPS")
while True:
    rx_data = can.recv(0x101)
    if rx_data is not None:
        print("recv:", [hex(i) for i in rx_data])
    time.sleep(0.001)

