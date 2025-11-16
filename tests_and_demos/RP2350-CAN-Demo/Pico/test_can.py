import time
import _thread
from rp2350_can import RP2350_CAN

INTERVAL_MS = 250
INTERVAL_NS = INTERVAL_MS * 1_000_000

can = RP2350_CAN(rate_kbps="500KBPS")

# simple lock so send/recv don’t collide on the SPI/CS lines
can_lock = _thread.allocate_lock()

running = True

def core1_thread():
    """Runs on core 1: send CAN frame every 100 ms."""
    next_send = time.time_ns()

    while running:
        now = time.time_ns()
        t0 = now
        t0_ms = t0 // 1_000_000

        # Build payload: last 8 digits of epoch ms
        s_full = f"{t0_ms:010d}"
        s = s_full[-8:]
        payload = bytearray(s, "ascii")

        # Send on CAN (guard with lock)
        with can_lock:
            can.send(0x123, payload)

        print(f"Core 1: [TX @ {t0_ms} ms] Sent ID 0x123 Data: {s}")

        # Schedule next send time
        next_send += INTERVAL_NS

        # Sleep until next scheduled time
        now = time.time_ns()
        if next_send > now:
            delay_ns = next_send - now
            time.sleep(delay_ns / 1_000_000_000)
        else:
            # We’re late, resync
            next_send = now + INTERVAL_NS

def core0_thread():
    while running:
        # example: listen on 0x101 (change as needed; 0x123 if you’ve got loopback)
        with can_lock:
            rx = can.recv(0x101)

        if rx is not None:
            on_can_message(0x101, rx)

        # small sleep to avoid 100% CPU on core 0
        time.sleep(0.01)

def on_can_message(can_id, data):
    """Callback-style handler called when a message is received."""
    now_ms = time.time_ns() // 1_000_000
    try:
        text = data.decode("ascii")
    except Exception:
        text = repr(data)
    print(f"Core 0: [RX @ {now_ms} ms] ID 0x{can_id:03X} Data: {text}")

# --- Start sender on core 1 ---
core1_thread_handle = _thread.start_new_thread(core1_thread, ())

try:
    core0_thread()
except KeyboardInterrupt:
    print("Keyboard interrupt received. Shutting down gracefully...")
    running = False
    # Wait for the core1_thread to finish
    
    print("Shutdown complete.")
