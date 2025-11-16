using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Threading.Tasks;
using System.Timers;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class SetupHandlerViewModel : ObservableObject
    {
        #region LCE Setup

        private byte _updateInterval;

        public byte UpdateInterval // in ms
        {
            get => _updateInterval;
            set => Set(ref _updateInterval, value);
        }

        private int _controlType = 2;

        public int ControlType
        {
            get => _controlType;
            set
            {
                Set(ref _controlType, value);
                switch (_controlType)
                {
                    case 1:
                        ControllerHandlerViewModel.SendControllerValues = false;
                        InitManualControl();
                        break;

                    case 2:
                        ControllerHandlerViewModel.SendControllerValues = true;
                        break;

                    default:
                        ControllerHandlerViewModel.SendControllerValues = false;
                        _manualControlTimer?.Stop();
                        break;
                }
            }
        }

        public RelayCommand ConnectToLCE_Command { get; set; }
        public RelayCommand InitThrusters_Command { get; set; }
        public RelayCommand DeinitThrusters_Command { get; set; }
        public RelayCommand ApplyUpdateInterval_Command { get; set; }
        public RelayCommand ResetErrorLCE_Command { get; set; }

        #endregion LCE Setup

        #region Manual Control

        private Timer _manualControlTimer;
        private MotorValues _manualControlValues;

        public MotorValues ManualControlValues
        {
            get => _manualControlValues;
            set => Set(ref _manualControlValues, value);
        }

        private float _manualControlAllVertical;

        public float ManualControlAllVertical
        {
            get => _manualControlAllVertical;
            set
            {
                Set(ref _manualControlAllVertical, Math.Clamp(value, -1f, 1f));
                ManualControlValues.VM1 = value;
                ManualControlValues.VM2 = value;
                ManualControlValues.VM3 = value;
            }
        }

        private float _manualControlAllHorizontal;

        public float ManualControlAllHorizontal
        {
            get => _manualControlAllHorizontal;
            set
            {
                Set(ref _manualControlAllHorizontal, Math.Clamp(value, -1f, 1f));
                ManualControlValues.HM1 = value;
                ManualControlValues.HM2 = value;
                ManualControlValues.HM3 = value;
            }
        }

        private float _manualControlAll;

        public float ManualControlAll
        {
            get => _manualControlAll;
            set
            {
                Set(ref _manualControlAll, Math.Clamp(value, -1f, 1f));
                ManualControlAllVertical = value;
                ManualControlAllHorizontal = value;
            }
        }

        public RelayCommand ManualControlReset_Command { get; set; }

        #endregion Manual Control

        #region Gyro

        private bool _gyroEnabled;

        public bool GyroEnabled
        {
            get => _gyroEnabled;
            set
            {
                if (Set(ref _gyroEnabled, value))
                {
                    currentConfig.GyroEnabled = value;
                    UpdateConfig();
                }
            }
        }

        private float _gyroPValue;

        public float GyroPValue
        {
            get => _gyroPValue;
            set => Set(ref _gyroPValue, Clamp255Float(value));
        }

        private float _gyroDValue;

        public float GyroDValue
        {
            get => _gyroDValue;
            set => Set(ref _gyroDValue, Clamp255Float(value));
        }

        // Method to clamp the value between 0 and 2.55
        private static float Clamp255Float(float value)
        {
            if (value < 0)
                return 0;
            if (value > 2.55)
                return 2.55f;
            return value;
        }

        public RelayCommand PullGyroValues_Command { get; set; }
        public RelayCommand ApplyGyroValues_Command { get; set; }

        #endregion Gyro

        #region Options

        private bool _dynamicThrottleEnabled;

        public bool DynamicThrottleEnabled
        {
            get
            {
                _dynamicThrottleEnabled = currentConfig.DynamicThrottleEnabled;
                return _dynamicThrottleEnabled;
            }
            set
            {
                Set(ref _dynamicThrottleEnabled, value);
                currentConfig.DynamicThrottleEnabled = value;
                ControllerHandlerViewModel.UseDynamicThrottle = value;
                UpdateConfig();
            }
        }

        private byte _maxCurrent;

        public byte MaxCurrent
        {
            get => _maxCurrent;
            set => Set(ref _maxCurrent, value);
        }

        #endregion Options

        private ConfigGUI currentConfig;

        public static UsbCANWrapper can;

        public SetupHandlerViewModel()
        {
            ManualControlValues = new MotorValues([0, 0, 0, 0, 0, 0]);
            currentConfig = ConfigLoader.LoadConfigGUI();
            ControlType = 2;
            ConnectToLCE_Command = new(ConnectToLCE);
            InitThrusters_Command = new(InitThrusters);
            DeinitThrusters_Command = new(DeinitThrusters);
            ApplyUpdateInterval_Command = new(ApplyUpdateInterval);
            ResetErrorLCE_Command = new(ResetErrorLCE);
            PullGyroValues_Command = new(PullGyroValues);
            ApplyGyroValues_Command = new(ApplyGyroValues);
            ManualControlReset_Command = new(ManualControlReset);
        }

        private void UpdateConfig() => ConfigLoader.UpdateConfigGUI(currentConfig);

        /// <summary>
        /// Helper: wait for a CAN response with a given ID (with timeout).
        /// Returns empty byte[] on timeout.
        /// </summary>
        private async Task<byte[]> WaitForResponseAsync(ushort responseId, int timeoutMs = 100)
        {
            if (can == null)
                return Array.Empty<byte>();

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            Action<ushort, byte[]> handler = null;
            var timeoutTimer = new System.Timers.Timer(timeoutMs)
            {
                AutoReset = false
            };

            timeoutTimer.Elapsed += (s, e) =>
            {
                can.UnregisterCallback(responseId, handler);
                tcs.TrySetResult(Array.Empty<byte>());
                timeoutTimer.Dispose();
            };

            handler = (id, data) =>
            {
                timeoutTimer.Stop();
                timeoutTimer.Dispose();
                can.UnregisterCallback(responseId, handler);
                tcs.TrySetResult(data ?? Array.Empty<byte>());
            };

            can.RegisterCallback(responseId, handler);
            timeoutTimer.Start();

            return await tcs.Task;
        }

        private async void ConnectToLCE()
        {
            DisconnectFromLCE();

            can = new UsbCANWrapper();

            // NOTE: ideally UsbCANWrapper.IsOpen should be "sp != null && sp.IsOpen"
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(3);
                await Task.Delay(3000);
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(0);
                return;
            }

            MainWindowViewModel.Instance.UpdateConnectionStatusLCE(1);

            // Send init command
            can.WriteBytes((ushort)LCE_CommandAddresses.InitThrusters);

            // Wait for init response
            byte[] response = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.InitThrusters_Response, 250);

            if (response.Length != 0)
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(2);
                MainWindowViewModel.Instance.ControlBoxConsoleText =
                    LCECommunicationHelper.LCE_MessageAddresses[response[0]];

                UpdateInterval = await PullUpdateInterval();
            }
            else
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(3);
            }
        }

        public void DisconnectFromLCE()
        {
            if (can != null && can.IsOpen)
            {
                bool closingSuccessful = can.Close();
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(closingSuccessful ? 4 : 5);
            }
            else
            {
                MainWindowViewModel.Instance.ControlBoxConsoleText = "CAN interface is currently closed";
            }
        }

        private async void InitThrusters()
        {
            // TODO: implement if needed (similar pattern: send command, await response)
        }

        private async void DeinitThrusters()
        {
            // TODO: implement if needed
        }

        private void InitManualControl()
        {
            _manualControlTimer = new Timer(UpdateInterval * 2); // interval in ms
            _manualControlTimer.Elapsed += ApplyManualControl;
            _manualControlTimer.AutoReset = true;
            _manualControlTimer.Enabled = true;
        }

        private void ApplyManualControl(object sender, ElapsedEventArgs e)
        {
            if (can != null && can.IsOpen)
            {
                can.WriteBytes((ushort)LCE_CommandAddresses.ApplyManualControl, ManualControlValues.GetAsByte());
            }
        }

        private async void ApplyUpdateInterval()
        {
            if (can == null || !can.IsOpen)
                return;

            byte oldCycleInterval = await PullUpdateInterval();
            if (oldCycleInterval == 0)
                return;

            can.WriteBytes((ushort)LCE_CommandAddresses.UpdateCylceTime, new byte[] { UpdateInterval });

            byte[] resp = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.UpdateCycleTime_Response, 100);
            if (resp.Length > 0)
            {
                MainWindowViewModel.Instance.ControlBoxConsoleText =
                    $"Updating Cycle Time from {oldCycleInterval} to {resp[0]} {(oldCycleInterval == resp[0] ? "Failed!" : "was Successful!")}";
            }
        }

        private async Task<byte> PullUpdateInterval()
        {
            if (can == null || !can.IsOpen)
                return 0x00;

            can.WriteBytes((ushort)LCE_CommandAddresses.UpdateCylceTime, new byte[] { 0x00 });

            byte[] response = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.UpdateCycleTime_Response, 100);
            if (response.Length > 0)
                return response[0];

            return 0x00;
        }

        private async void ResetErrorLCE()
        {
            if (can == null || !can.IsOpen)
                return;

            can.WriteBytes((ushort)LCE_CommandAddresses.ResetError);

            byte[] response = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.ResetError_Response, 100);
            if (response.Length != 0 && response[0] == 0x01)
            {
                MainWindowViewModel.Instance.ControlBoxConsoleText = "Resetting Error was successful";
            }
        }

        private async void PullGyroValues()
        {
            if (can == null || !can.IsOpen)
                return;

            can.WriteBytes((ushort)LCE_CommandAddresses.PullPidValues);

            byte[] response = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.PullPidValues_Response, 100);
            if (response.Length > 0)
            {
                if (response.Length >= 2)
                {
                    GyroPValue = LCECommunicationHelper.ConvertByteTo255Float(response[0]);
                    GyroDValue = LCECommunicationHelper.ConvertByteTo255Float(response[1]);
                }

                if (response.Length >= 3)
                {
                    GyroEnabled = (response[2] == 1);
                }

                MainWindowViewModel.Instance.ControlBoxConsoleText = "Pulling PID values was Successful!";
            }
            else
            {
                MainWindowViewModel.Instance.ControlBoxConsoleText = "Pulling PID values failed!";
            }
        }

        private async void ApplyGyroValues()
        {
            if (can == null || !can.IsOpen)
                return;

            byte[] payload =
            {
                LCECommunicationHelper.Convert255FloatToByte(GyroPValue),
                LCECommunicationHelper.Convert255FloatToByte(GyroDValue),
                (byte)(GyroEnabled ? 0x01 : 0x00)
            };

            can.WriteBytes((ushort)LCE_CommandAddresses.ApplyPidValues, payload);

            byte[] response = await WaitForResponseAsync((ushort)LCE_ResponseAddresses.ApplyPidValues_Response, 100);
            if (response.Length > 0)
                MainWindowViewModel.Instance.ControlBoxConsoleText = "Applying PID values was Successful!";
            else
                MainWindowViewModel.Instance.ControlBoxConsoleText = "Applying PID values failed!";
        }

        private void ManualControlReset() => ManualControlValues.UpdateAsArray([0, 0, 0, 0, 0, 0]);
    }
}