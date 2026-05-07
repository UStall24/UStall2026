import json

def read_json_file(filepath):
    with open(filepath, 'r') as file:
        return json.load(file)


def write_json_file(filepath, data):
    with open(filepath, 'w') as file:
        json_string = json.dumps(data)
        file.write(json_string)


def sanitize_direction_values(data):
    DOF = [
        "forwards_backwards",
        "left_right",
        "up_down",
        "yaw",
        "roll",
        "pitch"
    ]

    MOTOR = ["M1", "M2", "M3", "M4", "M5", "M6"]

    for dof in DOF:
        for motor in MOTOR:
            data[dof][motor] = float(data[dof][motor])

    return data


def get_direction_uart_values_payload(direction_values):
    DOF = [
        "forwards_backwards",
        "left_right",
        "up_down",
        "yaw",
        "roll",
        "pitch"
    ]

    MOTOR = ["M1", "M2", "M3", "M4", "M5", "M6"]

    values = []

    for dof in DOF:
        motor_values = []
        for motor in MOTOR:
            v = float(direction_values[dof][motor])
            motor_values.append(int((v + 1) * 127.5))
        values.append(motor_values)

    return values


def update_direction_values(direction_values, matrix):
    DOF = [
        "forwards_backwards",
        "left_right",
        "up_down",
        "yaw",
        "roll",
        "pitch"
    ]

    MOTOR = ["M1", "M2", "M3", "M4", "M5", "M6"]

    for i, dof in enumerate(DOF):
        for j, motor in enumerate(MOTOR):
            direction_values[dof][motor] = float(matrix[i][j])

    return direction_values