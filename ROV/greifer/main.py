import time
from machine import Pin
from rp_2350_can import RP2350_CAN
from sts3215 import STS3215  # Importiere deine Servo-Bibliothek

# ================= CAN IDs (Passend zum C# ViewModel) =================
CMD_J1_ID = 0x301
FB_J1_ID  = 0x4B1

# ================= Servo Konfiguration =================
SERVO_1_ID = 1

# ================= Hilfsfunktionen für CAN (16-Bit Signed Integer) =================
def unpack_signed_16(high, low):
    """Macht aus 2 Bytes (High, Low) eine vorzeichenbehaftete Zahl (z.B. -90 bis 90)."""
    val = (high << 8) | low
    if val > 32767:
        val -= 65536
    return val

def pack_signed_16(val):
    """Macht aus einer vorzeichenbehafteten Zahl 2 Bytes (High, Low)."""
    val = int(val)
    if val < 0:
        val += 65536
    high = (val >> 8) & 0xFF
    low = val & 0xFF
    return high, low

# ================= Hilfsfunktionen für Servo-Winkel-Mapping =================
def degrees_to_servo(degrees):
    """Wandelt Grad (-90 bis 90) in Servo-Schritte (0 bis 4095, Mitte=2048) um."""
    # 360 Grad = 4096 Schritte -> 1 Grad = 11.377 Schritte
    steps = int(2048 + (degrees * 4096 / 360))
    # Begrenze auf das physische Maximum des Servos (Sicherheitsfunktion)
    return max(0, min(4095, steps))

def servo_to_degrees(position):
    """Wandelt Servo-Schritte (0 bis 4095) zurück in Grad (-90 bis 90) für die GUI."""
    if position is None:
        return 0
    return int((position - 2048) * 360 / 4096)

# ================= Hauptprogramm =================
def main():
    print("Starte CAN-Node für MechProA Gripper (Nur Achse J1)...")

    # 1. Initialisiere den Servo-Bus
    print("Initialisiere Servo-Bus auf UART 0...")
    servo_bus = STS3215(uart_id=0, tx_pin=0, rx_pin=1, baudrate=1_000_000)
    
    if not servo_bus.ping(SERVO_1_ID):
        print(f"❌ WARNUNG: Servo mit ID {SERVO_1_ID} antwortet nicht. Bitte Verkabelung prüfen!")
    else:
        print(f"✅ Servo ID {SERVO_1_ID} gefunden. Aktiviere Drehmoment...")
        servo_bus.set_torque(SERVO_1_ID, True)

    # 2. Initialisiere CAN
    print("Initialisiere CAN-Bus mit 125KBPS...")
    can = RP2350_CAN(rate_kbps="125KBPS")

    # Zustandsspeicher für das Feedback
    joint1_degrees = 0

    last_feedback_time = time.ticks_ms()
    FEEDBACK_INTERVAL = 250  # Sende Feedback jede Sekunde (1000 ms)

    while True:
        # --- TEIL 1: EMPFANGE BEFEHLE VON DER GUI ---
        messages = can.recv()
        for rxId, data in messages:
            if rxId == CMD_J1_ID and len(data) >= 2:
                # 1. Zielwinkel aus dem CAN-Frame entpacken
                target_degrees = unpack_signed_16(data[0], data[1])
                print(f"GUI Befehl -> J1 Target: {target_degrees}°")
                
                # 2. Winkel in Servo-Position umrechnen
                target_servo_pos = degrees_to_servo(target_degrees)
                
                # 3. Servo bewegen (Geschwindigkeit 2000, Beschleunigung 50)
                servo_bus.move(SERVO_1_ID, position=target_servo_pos, speed=2000, acc=50)


        # --- TEIL 2: SENDE FEEDBACK AN DIE GUI ---
        current_time = time.ticks_ms()
        if time.ticks_diff(current_time, last_feedback_time) >= FEEDBACK_INTERVAL:
            
            # 1. Aktuelle Position direkt vom Servo abfragen (0-4095)
            actual_servo_pos = servo_bus.read_position(SERVO_1_ID)
            
            if actual_servo_pos is not None:
                # 2. Servo-Position zurück in Grad umrechnen für die GUI
                joint1_degrees = servo_to_degrees(actual_servo_pos)

                # 3. 2-Byte Payload für J1 verpacken und senden
                j1_h, j1_l = pack_signed_16(joint1_degrees)
                can.send(FB_J1_ID, bytearray([j1_h, j1_l]))
            else:
                # Fehlerfall: Wenn der Servo nicht gelesen werden konnte, 
                # senden wir die letzte bekannte Position.
                pass 

            last_feedback_time = current_time

        # Kurze Pause für die CPU
        time.sleep_ms(1)

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        # Wenn das Skript manuell in Thonny gestoppt wird, schalte das Drehmoment ab
        print("\nSkript beendet. Deaktiviere Drehmoment...")
        servo_bus = STS3215(uart_id=0, tx_pin=0, rx_pin=1, baudrate=1_000_000)
        servo_bus.set_torque(SERVO_1_ID, False)