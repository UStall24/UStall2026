from libs.calculator import Calculator
from libs.configLoader import*
import time


direction_values_path = "directionValues.json"
dynamic_throttle_path = "dynamic_throttle_data.json"
direction_values_data = read_json_file(direction_values_path)
dynamic_throttle_data = read_json_file(dynamic_throttle_path)

calculator = Calculator(direction_values_data, dynamic_throttle_data)

counter = 0

while 1:
    throttle_values = [counter, counter*2 - 1, -counter, 1 - counter, 1 - counter*2, 0]
    print("-", throttle_values)
    print(calculator.calc_all_motor_pwm2(throttle_values))
    time.sleep(0.2)
    counter += 0.01
    if counter > 1:
        counter = 0