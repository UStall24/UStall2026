using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Threading.Tasks;
using System.Timers;
using UStallGUI.Helpers;
using UStallGUI.Model;
using Timer = System.Timers.Timer;

namespace UStallGUI.ViewModel
{
    public class SetupHandlerViewModel : ObservableObject
    {
        public static SetupHandlerViewModel Instance { get; private set; }

        #region Pixhawk Setup

        private byte _updateInterval = 10;
        private int _controlType = 2;
        private string _comPort = "Auto";

        private ConfigGUI currentConfig;

        private UsbCANWrapper can;

        public string ComPort
        {
            get => _comPort;
            set => Set(ref _comPort, value);
        }

        public byte UpdateInterval // in ms
        {
            get => _updateInterval;
            set => Set(ref _updateInterval, value);
        }

        /// <summary>
        /// 1 = Manual control, 2 = Controller, others = off
        /// </summary>
        public int ControlType
        {
            get => _controlType;
            set
            {
                if (!Set(ref _controlType, value))
                    return;

                switch (_controlType)
                {
                    case 1:
                        if (ControllerHandlerViewModel.Instance != null)
                            ControllerHandlerViewModel.Instance.SendControllerValues = false;
                        InitManualControl();
                        break;

                    case 2:
                        if (ControllerHandlerViewModel.Instance != null)
                            ControllerHandlerViewModel.Instance.SendControllerValues = true;
                        StopManualControl();
                        break;

                    default:
                        if (ControllerHandlerViewModel.Instance != null)
                            ControllerHandlerViewModel.Instance.SendControllerValues = false;
                        StopManualControl();
                        break;
                }
            }
        }

        public RelayCommand ConnectToPixhawk_Command { get; }
        public RelayCommand ArmThrusters_Command { get; }
        public RelayCommand DisarmThrusters_Command { get; }
        public RelayCommand ApplyUpdateInterval_Command { get; }
        public RelayCommand ResetWatchdog_Command { get; }
        public RelayCommand HealthCommand { get; }

        #endregion Pixhawk Setup

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
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAllVertical, clamped))
                    return;

                if (ManualControlValues != null)
                {
                    ManualControlValues.M1 = clamped;
                    ManualControlValues.M2 = clamped;
                    ManualControlValues.M3 = clamped;
                }
            }
        }

        private float _manualControlAllHorizontal;

        public float ManualControlAllHorizontal
        {
            get => _manualControlAllHorizontal;
            set
            {
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAllHorizontal, clamped))
                    return;

                if (ManualControlValues != null)
                {
                    ManualControlValues.M4 = clamped;
                    ManualControlValues.M5 = clamped;
                    ManualControlValues.M6 = clamped;
                }
            }
        }

        private float _manualControlAll;

        public float ManualControlAll
        {
            get => _manualControlAll;
            set
            {
                var clamped = Math.Clamp(value, -1f, 1f);
                if (!Set(ref _manualControlAll, clamped))
                    return;

                ManualControlAllVertical = clamped;
                ManualControlAllHorizontal = clamped;
            }
        }

        public RelayCommand ManualControlReset_Command { get; }

        #endregion Manual Control

        #region Flight Mode Options

        private FlightMode _selectedFlightMode;

        public FlightMode SelectedFlightMode
        {
            get => _selectedFlightMode;
            set
            {
                if (!Set(ref _selectedFlightMode, value))
                    return;

                currentConfig.FlightMode = value;
                ControllerHandlerViewModel.CurrentFlightMode = value;
                UpdateConfig();
                RaisePropertyChanged(nameof(SelectedFlightModeIndex));
                QuickSetupViewModel.Instance?.NotifyFlightModeChanged();

                string modeMessage = value switch
                {
                    FlightMode.Manual => "Flight Mode set to MANUAL (Direct Control, No Attitude/Depth Hold)",
                    FlightMode.DepthHold => "Flight Mode set to DEPTH_HOLD (Automatic Depth + Attitude Hold)",
                    _ => "Flight Mode set to STABILIZE (Attitude Hold)",
                };
                MainWindowViewModel.Instance?.LogToControlBox(modeMessage);
            }
        }

        public int SelectedFlightModeIndex
        {
            get => (int)SelectedFlightMode;
            set
            {
                if (Enum.IsDefined(typeof(FlightMode), value))
                    SelectedFlightMode = (FlightMode)value;
            }
        }

        private int _maxThrusterSpeedPercent = 100;

        public int MaxThrusterSpeedPercent
        {
            get => _maxThrusterSpeedPercent;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (!Set(ref _maxThrusterSpeedPercent, clamped))
                    return;

                currentConfig.MaxThrusterSpeedPercent = clamped;
                ControllerHandlerViewModel.MaxThrusterSpeedPercent = clamped;
                UpdateConfig();
                QuickSetupViewModel.Instance?.NotifyMaxThrusterSpeedChanged();
            }
        }

        #endregion Flight Mode Options

        public SetupHandlerViewModel()
        {
            Instance = this;
            can = new UsbCANWrapper();
            ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

            ManualControlValues = new MotorValues([0, 0, 0, 0, 0, 0]);
            currentConfig = ConfigLoader.LoadConfigGUI();
            _selectedFlightMode = currentConfig.FlightMode;
            ControllerHandlerViewModel.CurrentFlightMode = _selectedFlightMode;
            _maxThrusterSpeedPercent = Math.Clamp(currentConfig.MaxThrusterSpeedPercent, 0, 100);
            ControllerHandlerViewModel.MaxThrusterSpeedPercent = _maxThrusterSpeedPercent;

            ArmThrusters_Command = new RelayCommand(() => _ = ArmThrustersAsync());
            DisarmThrusters_Command = new RelayCommand(() => _ = DisarmThrustersAsync());
            ApplyUpdateInterval_Command = new RelayCommand(() => _ = ApplyUpdateIntervalAsync());
            ResetWatchdog_Command = new RelayCommand(() => _ = ResetWatchdogAsync());
            //PullGyroValues_Command = new RelayCommand(() => _ = PullGyroValuesAsync());
            //ApplyGyroValues_Command = new RelayCommand(() => _ = ApplyGyroValuesAsync());
            ManualControlReset_Command = new RelayCommand(ManualControlReset);

            ConnectToPixhawk_Command = new RelayCommand(ConnectToControlUnit);
            HealthCommand = new RelayCommand(SendHealthMessage);
        }

        private async Task<byte[]> SendAndWaitForResponseAsync(ushort sendId, ushort recvId, byte[] payload = null, int timeoutMs = 400)
        {
            if (can == null || !can.IsOpen)
                return Array.Empty<byte>();

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(ushort id, byte[] data)
            {
                if (id == recvId)
                    tcs.TrySetResult(data ?? Array.Empty<byte>());
            }

            can.RegisterCallback(recvId, Handler);
            try
            {
                can.SendFrame(sendId, payload);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return completed == tcs.Task ? await tcs.Task : Array.Empty<byte>();
            }
            finally
            {
                can.UnregisterCallback(recvId, Handler);
            }
        }

        public async void SendHealthMessage()
        {
            MainWindowViewModel.Instance.LogToControlBox("Checking Pixhawk CAN link...");
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("CAN adapter is not open.");
                return;
            }

            bool ok = await can.TestConnectionAsync(300);
            if (ok)
                MainWindowViewModel.Instance.LogToControlBox("Health Ping OK (Pixhawk responded)");
            else
                MainWindowViewModel.Instance.LogToControlBox("Health Ping failed (no response from Pixhawk)");
        }

        #region Config helpers

        private void UpdateConfig() => ConfigLoader.UpdateConfigGUI(currentConfig);

        public async void ConnectToControlUnit()
        {
            if (can == null)
                can = new UsbCANWrapper();
            else
                can.ConnectToCanAdapter(force: true);

            ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

            // Check if the adapter is connected before proceeding
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("USB serial CAN adapter not connected.");
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                return;
            }

            MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connecting);
            var initResp = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.InitConnection,
                (ushort)RecieveAddressesControlUnit.InitConnection,
                null,
                500);

            bool ok = (initResp.Length > 0 && initResp[0] == 1) || await can.TestConnectionAsync(500);
            if (ok)
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);
                MainWindowViewModel.Instance.LogToControlBox($"Connected to Pixhawk on {ComPort} (Disarmed - click Arm Thrusters when ready).");
            }
            else
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.ErrorConnecting);
                MainWindowViewModel.Instance.LogToControlBox("CAN adapter open, but no reply from Pixhawk.");
            }
        }

        public void DisconnectFromLCE()
        {
            StopManualControl();

            if (can != null && can.IsOpen)
            {
                bool closingSuccessful = can.Close();
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(
                    closingSuccessful ? LceConnectionStatus.ClosingSuccessful : LceConnectionStatus.ClosingFailed
                );
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("CAN interface is currently closed");
            }

            ComPort = "Disconnected";
        }

        public UsbCANWrapper GetCANWrapper()
        {
            return can;
        }

        #endregion Config helpers

        #region Arm / Disarm Thrusters

        public async Task ArmThrustersAsync()
        {
            if (can == null)
                can = new UsbCANWrapper();
            else if (!can.IsOpen)
                can.ConnectToCanAdapter();

            ComPort = !string.IsNullOrEmpty(can.ComPort) ? can.ComPort : "Not Connected";

            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot arm thrusters: USB-CAN adapter not connected.");
                return;
            }

            var response = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.ArmThrusters,
                (ushort)RecieveAddressesControlUnit.ArmThrusters,
                null,
                500);

            if (response.Length > 0 && response[0] == 1)
            {
                MainWindowViewModel.Instance.UpdateConnectionStatusLCE(LceConnectionStatus.Connected);
                MainWindowViewModel.Instance.LogToControlBox("Thrusters ARMED successfully.");
            }
            else if (response.Length > 0 && response[0] == 0)
            {
                MainWindowViewModel.Instance.LogToControlBox("Thrusters arming REJECTED by Pixhawk (check pre-arm checks / safety switch).");
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("Arming failed (no CAN response from Pixhawk).");
            }
        }

        public async Task DisarmThrustersAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot disarm thrusters: USB-CAN adapter not connected.");
                return;
            }

            var response = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.DisarmThrusters,
                (ushort)RecieveAddressesControlUnit.DisarmThrusters,
                null,
                500);

            if (response.Length > 0 && response[0] == 1)
            {
                MainWindowViewModel.Instance.LogToControlBox("Thrusters DISARMED safely.");
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("Thrusters disarm failed or timed out.");
            }
        }

        #endregion Arm / Disarm Thrusters

        #region Manual Control Timer

        private void InitManualControl()
        {
            StopManualControl();

            if (UpdateInterval == 0)
                UpdateInterval = 10; // fallback

            _manualControlTimer = new Timer(UpdateInterval * 2); // interval in ms
            _manualControlTimer.Elapsed += ApplyManualControl;
            _manualControlTimer.AutoReset = true;
            _manualControlTimer.Enabled = true;
        }

        private void StopManualControl()
        {
            if (_manualControlTimer != null)
            {
                _manualControlTimer.Elapsed -= ApplyManualControl;
                _manualControlTimer.Stop();
                _manualControlTimer.Dispose();
                _manualControlTimer = null;
            }
        }

        private void ApplyManualControl(object sender, ElapsedEventArgs e)
        {
            if (can != null && can.IsOpen && ManualControlValues != null)
            {
                byte[] payload = ManualControlValues.GetAsByte();
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = LCECommunicationHelper.ScaleCenteredByte(payload[i], MaxThrusterSpeedPercent);
                }
                can.SendFrame((ushort)SendAddressesControlUnit.ApplyManualControl, payload);
            }
        }

        private void ManualControlReset()
        {
            ManualControlValues?.UpdateAsArray([0, 0, 0, 0, 0, 0]);
            ManualControlAll = 0f;
        }

        #endregion Manual Control Timer

        #region Update Interval

        private async Task ApplyUpdateIntervalAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot update cycle time: CAN not open.");
                return;
            }

            byte targetInterval = Math.Clamp(UpdateInterval, (byte)5, (byte)100);
            UpdateInterval = targetInterval;

            byte[] resp = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.UpdateCycleTime,
                (ushort)RecieveAddressesControlUnit.UpdateCycleTime,
                new byte[] { targetInterval },
                300);

            if (resp.Length > 0)
            {
                UpdateInterval = resp[0];
                MainWindowViewModel.Instance.LogToControlBox($"Pixhawk CAN bridge cycle time updated to {resp[0]} ms.");
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("Updating cycle time failed (no response).");
            }
        }

        #endregion Update Interval

        #region Watchdog Reset

        private async Task ResetWatchdogAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot reset watchdog: CAN not open.");
                return;
            }

            byte[] response = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.ResetWatchdog,
                (ushort)RecieveAddressesControlUnit.ResetWatchdog,
                null,
                300);

            if (response.Length != 0 && response[0] == 0x01)
            {
                MainWindowViewModel.Instance.LogToControlBox("Pixhawk Watchdog reset successful.");
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("Watchdog reset failed.");
            }
        }

        #endregion Watchdog Reset
    }
}
