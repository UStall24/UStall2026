using GalaSoft.MvvmLight;
using SharpDX.XInput;
using System;

namespace UStallGUI.Model
{
    public class ControllerModel : ObservableObject
    {
        public ControllerModel(Controller controller)
        {
            Controller = controller;
            SetupControlStyle2();
        }

        public ControllerModel() => SetupControlStyle2();

        // Variables used one time
        public Controller Controller { get; set; }

        // Variables to avoid excess declaring
        private State controllerState;

        private int _controlStyle = 0;

        public int ControlStyle
        {
            get
            {
                _controlStyle = ConfigLoader.CurrentConfig.ControlStyle;
                return _controlStyle;
            }
            set
            {
                Set(ref _controlStyle, value);
                ConfigLoader.CurrentConfig.ControlStyle = _controlStyle;
                ConfigLoader.UpdateConfigGUI();
            }
        }

        // ControlStyle 2 Variables
        public byte AltRightJoystickX { get; set; } = 127;

        public byte AltRightJoystickY { get; set; } = 127;

        /// Binding Variables
        // Movement
        public byte LeftJoystickX { get; set; } = 0;

        public byte LeftJoystickY { get; set; } = 0;
        public byte RightJoystickX { get; set; } = 0;
        public byte RightJoystickY { get; set; } = 0;
        public bool L1Trigger { get; set; } = false;
        public bool R1Trigger { get; set; } = false;
        public byte L2Trigger { get; set; } = 0;
        public byte R2Trigger { get; set; } = 0;

        // Free Variables
        public bool LeftJoystickDown { get; set; } = false;

        public bool RightJoystickDown { get; set; } = false;
        public bool DPadUp { get; set; } = false;
        public bool DPadDown { get; set; } = false;
        public bool DPadLeft { get; set; } = false;
        public bool DPadRight { get; set; } = false;
        public bool ButtonA { get; set; } = false;
        public bool ButtonB { get; set; } = false;
        public bool ButtonX { get; set; } = false;
        public bool ButtonY { get; set; } = false;
        public bool ButtonStart { get; set; } = false;
        public bool ButtonBack { get; set; } = false;

        // Positive Flank Callbacks
        public event Action DPadUpPressed, DPadDownPressed, DPadLeftPressed, DPadRightPressed;

        public event Action ButtonAPressed, ButtonBPressed, ButtonXPressed, ButtonYPressed;

        public event Action ButtonStartPressed, ButtonBackPressed;

        public event Action LeftJoystickDownPressed, RightJoystickDownPressed;

        private bool prevDPadUp, prevDPadDown, prevDPadLeft, prevDPadRight;
        private bool prevButtonA, prevButtonB, prevButtonX, prevButtonY;
        private bool prevButtonStart, prevButtonBack;
        private bool prevLeftJoystickDown, prevRightJoystickDown;

        public void UpdateControllerValues()
        {
            if (Controller != null)
            {
                controllerState = Controller.GetState();
                // Movement Variables
                LeftJoystickX = (byte)((controllerState.Gamepad.LeftThumbX + 32768) / 256);
                LeftJoystickY = (byte)((controllerState.Gamepad.LeftThumbY + 32768) / 256);
                RightJoystickX = (byte)((controllerState.Gamepad.RightThumbX + 32768) / 256);
                RightJoystickY = (byte)((controllerState.Gamepad.RightThumbY + 32768) / 256);
                L2Trigger = controllerState.Gamepad.LeftTrigger;
                R2Trigger = controllerState.Gamepad.RightTrigger;

                L1Trigger = (controllerState.Gamepad.Buttons & GamepadButtonFlags.LeftShoulder) != 0;
                R1Trigger = (controllerState.Gamepad.Buttons & GamepadButtonFlags.RightShoulder) != 0;

                // D-Pad
                bool currentDPadUp = (controllerState.Gamepad.Buttons & GamepadButtonFlags.DPadUp) != 0;
                bool currentDPadDown = (controllerState.Gamepad.Buttons & GamepadButtonFlags.DPadDown) != 0;
                bool currentDPadLeft = (controllerState.Gamepad.Buttons & GamepadButtonFlags.DPadLeft) != 0;
                bool currentDPadRight = (controllerState.Gamepad.Buttons & GamepadButtonFlags.DPadRight) != 0;

                if (!prevDPadUp && currentDPadUp) DPadUpPressed?.Invoke();
                if (!prevDPadDown && currentDPadDown) DPadDownPressed?.Invoke();
                if (!prevDPadLeft && currentDPadLeft) DPadLeftPressed?.Invoke();
                if (!prevDPadRight && currentDPadRight) DPadRightPressed?.Invoke();

                prevDPadUp = DPadUp = currentDPadUp;
                prevDPadDown = DPadDown = currentDPadDown;
                prevDPadLeft = DPadLeft = currentDPadLeft;
                prevDPadRight = DPadRight = currentDPadRight;

                // Buttons A/B/X/Y
                bool currentA = (controllerState.Gamepad.Buttons & GamepadButtonFlags.A) != 0;
                bool currentB = (controllerState.Gamepad.Buttons & GamepadButtonFlags.B) != 0;
                bool currentX = (controllerState.Gamepad.Buttons & GamepadButtonFlags.X) != 0;
                bool currentY = (controllerState.Gamepad.Buttons & GamepadButtonFlags.Y) != 0;

                if (!prevButtonA && currentA) ButtonAPressed?.Invoke();
                if (!prevButtonB && currentB) ButtonBPressed?.Invoke();
                if (!prevButtonX && currentX) ButtonXPressed?.Invoke();
                if (!prevButtonY && currentY) ButtonYPressed?.Invoke();

                prevButtonA = ButtonA = currentA;
                prevButtonB = ButtonB = currentB;
                prevButtonX = ButtonX = currentX;
                prevButtonY = ButtonY = currentY;

                // Start / Back / Joystick Press
                bool currentStart = (controllerState.Gamepad.Buttons & GamepadButtonFlags.Start) != 0;
                bool currentBack = (controllerState.Gamepad.Buttons & GamepadButtonFlags.Back) != 0;
                bool currentLeftJoyDown = (controllerState.Gamepad.Buttons & GamepadButtonFlags.LeftThumb) != 0;
                bool currentRightJoyDown = (controllerState.Gamepad.Buttons & GamepadButtonFlags.RightThumb) != 0;

                if (!prevButtonStart && currentStart) ButtonStartPressed?.Invoke();
                if (!prevButtonBack && currentBack) ButtonBackPressed?.Invoke();
                if (!prevLeftJoystickDown && currentLeftJoyDown) LeftJoystickDownPressed?.Invoke();
                if (!prevRightJoystickDown && currentRightJoyDown) RightJoystickDownPressed?.Invoke();

                prevButtonStart = ButtonStart = currentStart;
                prevButtonBack = ButtonBack = currentBack;
                prevLeftJoystickDown = LeftJoystickDown = currentLeftJoyDown;
                prevRightJoystickDown = RightJoystickDown = currentRightJoyDown;
            }
        }

        public byte[] GetMovementBytes()
        {
            byte l1r1Trigger = (byte)((L1Trigger ? 1 : 0) + (R1Trigger ? 2 : 0));
            byte[] value = new byte[] { };
            if (ControlStyle == 0)
            {
                value = new byte[] { LeftJoystickX, LeftJoystickY, RightJoystickX, RightJoystickY, L2Trigger, R2Trigger, l1r1Trigger };
            }
            if (ControlStyle == 1)
            {
                value = new byte[] { LeftJoystickX, LeftJoystickY, AltRightJoystickX, AltRightJoystickY, L2Trigger, R2Trigger, l1r1Trigger };
            }
            return value;
        }

        public string GetMovementBytesAsString()
        {
            byte[] movementBytes = GetMovementBytes();
            return string.Join(", ", movementBytes);
        }

        public override string ToString()
        {
            return $"LeftJoystickX: {LeftJoystickX}, LeftJoystickY: {LeftJoystickY}, RightJoystickX: {RightJoystickX}, RightJoystickY: {RightJoystickY}\n" +
                   $"L1Trigger: {L1Trigger}, R1Trigger: {R1Trigger}, L2Trigger: {L2Trigger}, R2Trigger: {R2Trigger}\n" +
                   $"DPadUp: {DPadUp}, DPadDown: {DPadDown}, DPadLeft: {DPadLeft}, DPadRight: {DPadRight}\n" +
                   $"ButtonA: {ButtonA}, ButtonB: {ButtonB}, ButtonX: {ButtonX}, ButtonY: {ButtonY}";
        }

        private void SetupControlStyle2()
        {
            int step = 21;
            DPadUpPressed += () =>
            {
                if (AltRightJoystickY + step <= 255)
                    AltRightJoystickY += 21;
            };
            DPadDownPressed += () =>
            {
                if (AltRightJoystickY - step >= 0)
                    AltRightJoystickY -= 21;
            };
            DPadRightPressed += () =>
            {
                if (AltRightJoystickX + step <= 255)
                    AltRightJoystickX += 21;
            };
            DPadLeftPressed += () =>
            {
                if (AltRightJoystickX - step >= 0)
                    AltRightJoystickX -= 21;
            };
        }
    }
}