import time
from libs.rp_2350_can import RP2350_CAN, pretty_can_print

# ---- CONFIG ----
CAN_BITRATE = "500KBPS"
TX_ID = 0x123
SEND_INTERVAL_MS = 1000

# ---- INIT ----
can = RP2350_CAN(rate_kbps=CAN_BITRATE)

last_send = time.ticks_ms()
seq = 0

print("Starting CAN test...")

while True:
    try:
        # ---- RECEIVE (non-blocking, gets ALL frames) ----
        frames = can.recv(0)  # 0 = non-blocking

        if frames:
            for can_id, data in frames:
                print("[RX]", pretty_can_print(can_id, data))

        # ---- TRANSMIT every 1s ----
        now = time.ticks_ms()
        if time.ticks_diff(now, last_send) >= SEND_INTERVAL_MS:

            payload = bytearray([
                seq & 0xFF,
                (seq >> 8) & 0xFF,
                0xAA, 0x55,
                0xDE, 0xAD,
                0xBE, 0xEF
            ])

            try:
                can.send(TX_ID, payload)
                print("[TX]", pretty_can_print(TX_ID, payload))
            except Exception as e:
                print("[ERR] TX failed:", e)

            seq = (seq + 1) & 0xFFFF
            last_send = now

        # ---- SMALL DELAY (prevents CPU hogging) ----
        time.sleep(0.005)

    except Exception as e:
        print("[FATAL]", e)
        time.sleep(1)
