using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SharpDX.XInput;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class ControllerHandlerViewModel : ObservableObject
    {
        public static ControllerHandlerViewModel Instance;
        public bool sendControllerValues = true;

        // --- Keyboard Polling via P/Invoke for Background Thread Safety ---
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private bool IsKeyDown(int vKey)
        {
            // If the high bit is set, the key is currently down.
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        // Virtual-Key Codes for mapped keys
        private const int VK_W = 0x57;

        private const int VK_S = 0x53;
        private const int VK_A = 0x41;
        private const int VK_D = 0x44;
        private const int VK_Q = 0x51;
        private const int VK_E = 0x45;
        private const int VK_X = 0x58;
        private const int VK_Y = 0x59;

        // ------------------------------------------------------------------
        private bool isPollingTaskRunning = false;

        private void EnsurePollingStarted()
        {
            if (!isPollingTaskRunning)
            {
                isPollingTaskRunning = true;
                _ = Task.Run(ControllerTimerCallback);
            }
        }

        public bool SendControllerValues
        {
            get => sendControllerValues;
            set
            {
                Set(ref sendControllerValues, value);
            }
        }

        public static bool UseDynamicThrottle = false;

        private readonly int pollingFrequency = 200;
        private readonly int sendingDividor = 1;
        private bool keepPolling = false;

        // Properties for binding
        public ControllerModel CurrentControllerModel { get; set; } = new();

        private readonly UsbCANWrapper can;

        public ControllerHandlerViewModel(UsbCANWrapper can)
        {
            Instance = this;
            this.can = can;
            ConnectToController = new RelayCommand(() => SetController(0));
        }

        public void SetController(int index)
        {
            Controller controller = new Controller((UserIndex)index);
            CurrentControllerModel.Controller = controller;

            // Use the helper method instead of Task.Run directly
            EnsurePollingStarted();

            MainWindowViewModel.Instance.LogToControlBox("Controller with Index 0 is connected");
            MainWindowViewModel.Instance.ConnectionStatusController = "Connected";
        }

        private async Task ControllerTimerCallback()
        {
            keepPolling = true;
            int delayMs = pollingFrequency > 0 ? 1000 / pollingFrequency : 10;
            int dividorCounter = 0;

            try
            {
                while (keepPolling)
                {
                    CurrentControllerModel.UpdateControllerValues();

                    if (dividorCounter == 0)
                    {
                        var command = UseDynamicThrottle
                            ? SendAddressesControlUnit.ApplyControllerValues_Dyn
                            : SendAddressesControlUnit.ApplyControllerValues;

                        var payload = GetMappedPayload();

                        // Allow sending if either Controller or Keyboard is active
                        if (sendControllerValues || EnableKeyboardInput)
                        {
                            can.SendFrame((ushort)command, payload);
                            Console.WriteLine($"[TX: Driving] {string.Join(", ", payload)}");
                        }
                        else
                        {
                            Console.WriteLine($"[Not sending: Driving] {string.Join(", ", payload)}");
                        }
                        dividorCounter = sendingDividor;
                    }
                    else
                    {
                        dividorCounter--;
                    }

                    RaisePropertyChanged(nameof(CurrentControllerModel));
                    await Task.Delay(delayMs);
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance.LogToControlBox($"Controller Exception: {ex}");
            }
        }

        public RelayCommand ConnectToController { get; set; }

        public List<string> MappingOptions { get; } = new()
        {
            "Left Joystick Up/Down",      // 0
            "Left Joystick Left/Right",   // 1
            "Right Joystick Up/Down",     // 2
            "Right Joystick Left/Right",  // 3
            "Back Triggers L2/R2",        // 4
            "Back Bumpers L1/R1",         // 5
            "D-Pad Up/Down",              // 6
            "D-Pad Left/Right",           // 7
            "A/B",                        // 8
            "X/Y",                        // 9
            "None",                       // 10
            "Keyboard W/S",               // 11
            "Keyboard A/D",               // 12
            "Keyboard Q/E",               // 13
            "Keyboard X/Y"                // 14
        };

        public ObservableCollection<ControllerMappingItem> ControllerMappings { get; } = new()
        {
            new() { Name = "Forward/Backwards", SelectedIndex = 0 },
            new() { Name = "Left/Right",        SelectedIndex = 1 },
            new() { Name = "Up/Down",           SelectedIndex = 4 },
            new() { Name = "Yaw",               SelectedIndex = 5 },
            new() { Name = "Pitch",             SelectedIndex = 10},
            new() { Name = "Roll",              SelectedIndex = 10},
            new() { Name = "Gripper Servo S1",  SelectedIndex = 8 },
            new() { Name = "Gripper Servo S2",  SelectedIndex = 10 },
            new() { Name = "Gripper Servo S3",  SelectedIndex = 10 }
        };

        private byte ResolveMapping(int mappingIndex)
        {
            var m = CurrentControllerModel;

            return mappingIndex switch
            {
                // 0
                0 => m.LeftJoystickY,

                // 1
                1 => m.LeftJoystickX,

                // 2
                2 => m.RightJoystickY,

                // 3
                3 => m.RightJoystickX,

                // 4 (Triggers L2/R2 → combine to signed style)
                4 => (byte)((m.R2Trigger - m.L2Trigger + 255) * 0.5),

                // 5 (Bumpers L1/R1 → digital)
                5 => m.R1Trigger ? (byte)255 : m.L1Trigger ? (byte)0 : (byte)127,

                // 6 (DPad Up/Down)
                6 => (byte)(127 + ((m.DPadUp ? 1 : 0) - (m.DPadDown ? 1 : 0)) * 127),

                // 7 (DPad Left/Right)
                7 => (byte)(127 + ((m.DPadRight ? 1 : 0) - (m.DPadLeft ? 1 : 0)) * 127),

                // 8 (A/B)
                8 => (byte)(127 + ((m.ButtonA ? 1 : 0) - (m.ButtonB ? 1 : 0)) * 127),

                // 9 (X/Y)
                9 => (byte)(127 + ((m.ButtonX ? 1 : 0) - (m.ButtonY ? 1 : 0)) * 127),

                // 10 (None)
                10 => 127,

                11 => EnableKeyboardInput ? (byte)(127 + ((IsKeyDown(VK_W) ? 1 : 0) - (IsKeyDown(VK_S) ? 1 : 0)) * 127) : (byte)127,

                // 12 (Keyboard A/D -> D is right/positive, A is left/negative)
                12 => (byte)(127 + ((IsKeyDown(VK_D) ? 1 : 0) - (IsKeyDown(VK_A) ? 1 : 0)) * 127),

                // 13 (Keyboard Q/R -> Q is positive, R is negative)
                13 => (byte)(127 + ((IsKeyDown(VK_Q) ? 1 : 0) - (IsKeyDown(VK_E) ? 1 : 0)) * 127),

                // 14 (Keyboard X/Y -> X is positive, Y is negative)
                14 => (byte)(127 + ((IsKeyDown(VK_X) ? 1 : 0) - (IsKeyDown(VK_Y) ? 1 : 0)) * 127),

                _ => 127
            };
        }

        private bool enableKeyboardInput = false;

        public bool EnableKeyboardInput
        {
            get => enableKeyboardInput;
            set
            {
                if (Set(ref enableKeyboardInput, value))
                {
                    // Start the polling loop if keyboard is enabled
                    if (enableKeyboardInput)
                    {
                        EnsurePollingStarted();
                    }
                }
            }
        }

        private byte[] GetMappedPayload()
        {
            return new byte[]
            {
                ResolveMapping(ControllerMappings[0].SelectedIndex), // Forward/Backward
                ResolveMapping(ControllerMappings[1].SelectedIndex), // Left/Right
                ResolveMapping(ControllerMappings[2].SelectedIndex), // Up/Down
                ResolveMapping(ControllerMappings[3].SelectedIndex), // Yaw
                ResolveMapping(ControllerMappings[4].SelectedIndex), // Pitch
                ResolveMapping(ControllerMappings[5].SelectedIndex), // Roll
            };
        }
    }

    public class ControllerMappingItem : ObservableObject
    {
        public string Name { get; set; }

        private int selectedIndex;

        public int SelectedIndex
        {
            get => selectedIndex;
            set => Set(ref selectedIndex, value);
        }
    }
}