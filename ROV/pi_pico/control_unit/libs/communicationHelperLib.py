def convert_255_float_to_byte(value):
    if not (0 <= value <= 2.55):
        raise ValueError("Value must be between 0 and 2.55")
    byte_value = int(value * 100)
    return max(0, min(255, byte_value))

def convert_byte_to_255_float(byte_value):
    if not (0 <= byte_value <= 255):
        raise ValueError("Byte value must be between 0 and 255")
    float_value = byte_value / 100.0        
    return float_value
