import time
from libs.rp_2350_can import RP2350_CAN  # or however your file is named
can = RP2350_CAN(rate_kbps="100KBPS")
while True:
    rx_data = can.recv(0x001)
    if rx_data is not None:
        print("recv:", rx_data)
    time.sleep(0.001)

