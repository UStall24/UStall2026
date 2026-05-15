""" Test programm for the T200 Thruster"""
try:
    from machine import Pin, PWM  # Fixed typo
    import time
except ImportError as e:
    print(f"Fehler: konnte den import nicht finden.: {e}")

# Pinout for the Thrusters
# CRITICAL: Every thruster needs a UNIQUE GPIO pin.
# Adjust these numbers (10-15) to match your actual microcontroller wiring!
thruster_1 = PWM(Pin(4), freq=50)
thruster_2 = PWM(Pin(2), freq=50)
thruster_3 = PWM(Pin(6), freq=50)
thruster_4 = PWM(Pin(3), freq=50)
thruster_5 = PWM(Pin(5), freq=50)#Pin 2 geht
thruster_6 = PWM(Pin(7), freq=50)


all_thruster = [thruster_1, thruster_2, thruster_3,thruster_4, thruster_5, thruster_6]

values = [1540, 1550, 1560]

def set_speed(thruster_, pulse_us):
    # MicroPython uses a 16-bit duty cycle (0 to 65535)
    # A 50Hz signal has a period of 20ms (20,000 microseconds)
    duty_cycle = int((pulse_us / 20000.0) * 65535)
    thruster_.duty_u16(duty_cycle)

def testing_of_thruster() -> None:
    print("Arming all thrusters...")
        
    # Arming Phase: Send 1500us to ALL thrusters at the same time
    for t in all_thruster:
        set_speed(t, 1500)
        
    # Wait 5 seconds to ensure all ESCs hear the signal and arm
    time.sleep(5) 
    print("Arming complete. Starting tests...")

    # Testing Phase
    for i, t in enumerate(all_thruster):
        print(f"Testing Thruster {i + 1}...")
            
        for v in values:
            set_speed(t, v)
            time.sleep(3) # Let it run at this speed for 3 seconds
                
        # Stop the current thruster before testing the next one
        print(f"Stopping Thruster {i + 1}")
        set_speed(t, 1500)
        time.sleep(1)
try:
    # Run the test sequence
    testing_of_thruster()
except KeyboardInterrupt:
    print("Stop testing")
