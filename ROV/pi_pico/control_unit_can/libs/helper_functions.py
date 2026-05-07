def print_direction_values(direction_values):
    DOF = [
        "forwards_backwards",
        "left_right",
        "up_down",
        "yaw",
        "roll",
        "pitch"
    ]

    MOTOR = ["M1", "M2", "M3", "M4", "M5", "M6"]

    print("\n[DIRECTION VALUES]")

    for dof in DOF:
        print(f"\n{dof}:")
        row = []
        for motor in MOTOR:
            val = direction_values[dof][motor]
            row.append(f"{motor}:{val:+.2f}")
        print("  " + "  ".join(row))